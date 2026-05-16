import os
import tempfile
import uuid
import shutil
from fractions import Fraction
from typing import Tuple, Dict, Any, Optional, List
from PyQt5.QtCore import QThread, pyqtSignal
from .system_utils import create_subprocess, kill_process_tree, check_disk_space, monitor_ffmpeg_progress
from .filter_builder import FilterBuilder
from .encoders import EncoderManager
from .media_utils import MediaProber, calculate_video_bitrate
from .processing_utils import ProgressScaler, generate_text_overlay_png
from .config_data import VideoConfig

class ProcessThread(QThread):
    progress_update_signal = pyqtSignal(int)
    status_update_signal = pyqtSignal(str)
    finished_signal = pyqtSignal(bool, str)

    def __init__(self, 
                 input_path, start_time_ms, end_time_ms, original_resolution,
                 is_mobile_format, speed_factor, base_dir=None,
                 progress_signal=None, status_signal=None, finished_signal=None,
                 logger=None, is_boss_hp=False, show_teammates_overlay=False,
                 show_spectating_overlay=False,
                 quality_level=2, bg_music_path=None, bg_music_volume=0.8,
                 bg_music_offset_ms=0, original_total_duration_ms=0,
                  disable_fades=False, intro_still_sec=0,
                  intro_from_midpoint=False, intro_abs_time_ms=None,
                  portrait_text=None, music_config=None, speed_segments=None,
                  hardware_strategy='CPU', music_tracks=None, script_dir=None,
                  target_mb_override=None,
                  progress_update_signal=None, status_update_signal=None):
        super().__init__()
        self.input_path = input_path
        self.start_time_ms = start_time_ms
        self.end_time_ms = end_time_ms
        self.original_resolution = original_resolution
        self.is_mobile_format = is_mobile_format
        self.speed_factor = speed_factor
        self.base_dir = base_dir or script_dir
        if self.base_dir:
            self.base_dir = os.path.normpath(self.base_dir)
            if os.path.basename(self.base_dir) == 'processing':
                self.base_dir = os.path.dirname(self.base_dir)
        if progress_update_signal: progress_signal = progress_update_signal
        if status_update_signal: status_signal = status_update_signal
        self.progress_update_signal = progress_signal
        self.status_update_signal = status_signal
        self.finished_signal = finished_signal
        self.logger = logger
        self.is_boss_hp = is_boss_hp
        self.show_teammates_overlay = show_teammates_overlay
        self.show_spectating_overlay = show_spectating_overlay
        self.portrait_text = portrait_text
        self.bg_music_path = bg_music_path
        self.bg_music_offset_ms = bg_music_offset_ms
        self.disable_fades = disable_fades
        self.intro_still_sec = float(intro_still_sec or 0.0)
        self.intro_from_midpoint = bool(intro_from_midpoint)
        self.intro_abs_time_ms = int(intro_abs_time_ms) if intro_abs_time_ms is not None else None
        self.speed_segments = self._normalize_speed_segments(speed_segments)
        self.hardware_strategy = hardware_strategy
        self.music_config = dict(music_config or {})
        if self.bg_music_path and "music_vol" not in self.music_config and "volume" not in self.music_config:
            try:
                self.music_config["music_vol"] = float(bg_music_volume)
            except (TypeError, ValueError):
                self.music_config["music_vol"] = 0.8
        self.music_tracks = music_tracks or []
        self.config = VideoConfig(self.base_dir)
        self.keep_highest_res, self.target_mb, self.quality_level = self.config.get_quality_settings(quality_level, target_mb_override=target_mb_override)
        self.filter_builder = FilterBuilder(self.logger)
        self.ffmpeg_path = os.path.join(self.base_dir, 'binaries', 'ffmpeg.exe')
        if not os.path.exists(self.ffmpeg_path):
            self.ffmpeg_path = 'ffmpeg'
        self.encoder_mgr = EncoderManager(self.logger, hardware_strategy=self.hardware_strategy, ffmpeg_path=self.ffmpeg_path)
        self.prober = MediaProber(os.path.join(self.base_dir, 'binaries'), self.input_path)
        self.current_process = None
        self.is_canceled = False
        self._finish_emitted = False
        self.duration_corrected_sec = (self.end_time_ms - self.start_time_ms) / 1000.0 / self.speed_factor
        self._output_dir = os.path.join(os.path.expanduser("~"), "Downloads")

    def _normalize_speed_segments(self, raw_segments):
        normalized = []
        trim_lo = int(getattr(self, "start_time_ms", 0) or 0)
        trim_hi = int(getattr(self, "end_time_ms", 0) or 0)
        for seg in (raw_segments or []):
            if not isinstance(seg, dict):
                continue
            try:
                s_ms = int(seg.get("start_ms", seg.get("start", 0)))
                e_ms = int(seg.get("end_ms", seg.get("end", 0)))
                spd = float(seg.get("speed", self.speed_factor))
            except Exception:
                continue
            if e_ms <= s_ms:
                continue
            if trim_hi > trim_lo:
                if e_ms <= trim_lo or s_ms >= trim_hi:
                    if self.logger:
                        self.logger.warning(f"GRANULAR: dropped out-of-trim segment [{s_ms},{e_ms}] (trim=[{trim_lo},{trim_hi}])")
                    continue
                clamped_s = max(trim_lo, s_ms)
                clamped_e = min(trim_hi, e_ms)
                if clamped_e - clamped_s <= 0:
                    continue
                s_ms, e_ms = clamped_s, clamped_e
            normalized.append({"start_ms": s_ms, "end_ms": e_ms, "speed": spd})
        normalized.sort(key=lambda x: x["start_ms"])
        return normalized

    def _hardware_decode_flags(self, encoder_name: str) -> list[str]:
        if encoder_name == "h264_nvenc" and str(self.hardware_strategy or "").upper() == "NVIDIA":
            return ['-hwaccel', 'cuda', '-hwaccel_output_format', 'cuda', '-threads', '1']
        return []

    def _uses_cuda_frames(self, encoder_name: str) -> bool:
        return bool(self._hardware_decode_flags(encoder_name))

    def _target_size_bounds(self):
        if not self.target_mb:
            return None
        target = int(Fraction(str(self.target_mb)) * 1024 * 1024)
        slack = max(256 * 1024, int(Fraction(target, 50)))
        return max(1, target - slack), target

    def _choose_audio_bitrate(self, source_audio_kbps, duration_sec):
        source = int(source_audio_kbps or 192)
        source = min(320, max(128, source))
        if not self.target_mb or duration_sec <= 0:
            return min(320, max(192, source))
        total_kbps = (Fraction(str(self.target_mb)) * 8192) / max(Fraction(1, 1000), Fraction(str(duration_sec)))
        if total_kbps < 900:
            return 128
        if total_kbps < 1800:
            return min(160, source)
        if total_kbps < 3200:
            return min(192, max(160, source))
        return min(256, max(192, source))

    def _output_resolution_for_bitrate(self):
        if self.is_mobile_format:
            return "1080x1920"
        if self.intro_still_sec and self.intro_still_sec > 0:
            return "1920x1080"
        return self.original_resolution or "1920x1080"

    def _validate_render_output(self, path, expected_duration_sec, target_fps_expr, monitor_stats):
        if not os.path.exists(path) or os.path.getsize(path) <= 0:
            return False, "Final render output is missing."
        critical = list((monitor_stats or {}).get("critical_lines", []))
        if critical:
            return False, "FFmpeg reported unsafe decode or timestamp errors."
        dropped = int((monitor_stats or {}).get("drop_frames", 0))
        duplicated = int((monitor_stats or {}).get("dup_frames", 0))
        if (dropped or duplicated) and self.logger:
            self.logger.info(f"FFmpeg CFR normalization counters: dropped={dropped}, duplicated={duplicated}")
        out_probe = MediaProber(os.path.join(self.base_dir, 'binaries'), path)
        fps_expr = out_probe.get_video_fps_expr(target_fps_expr)
        try:
            fps_q = Fraction(str(fps_expr))
        except Exception:
            fps_q = Fraction(0, 1)
        if fps_q > Fraction(60, 1):
            return False, f"Output FPS exceeds social limit: {float(fps_q):.3f}"
        duration = out_probe.get_duration()
        fps_value = float(fps_q) if fps_q > 0 else 60.0
        tolerance = max(0.35, 4.0 / max(1.0, fps_value or 60.0))
        if duration > 0 and abs(duration - expected_duration_sec) > tolerance:
            return False, f"Output duration mismatch. Expected {expected_duration_sec:.3f}s, got {duration:.3f}s."
        if not out_probe.get_resolution():
            return False, "Output video stream is unreadable."
        return True, "OK"
    @staticmethod
    def _emit_signal_or_callback(target, *args):
        try:
            emitter = getattr(target, "emit", None)
            if callable(emitter):
                emitter(*args)
                return
            if callable(target):
                target(*args)
        except Exception:
            return

    def cancel(self):
        self.is_canceled = True
        if self.current_process: kill_process_tree(self.current_process.pid, self.logger)

    def _monitor_disk_space(self):
        try:
            os.makedirs(self._output_dir, exist_ok=True)
        except OSError as e:
            if self.logger: self.logger.error(f"Output directory unavailable: {e}")
            self.cancel()
            return True
        dynamic_threshold_gb = 0.5
        if hasattr(self, 'target_mb') and self.target_mb:
            dynamic_threshold_gb = float(max(Fraction(1, 2), (Fraction(str(self.target_mb)) * 3) / 1024))
        if not check_disk_space(self._output_dir, dynamic_threshold_gb):
            if self.logger: self.logger.error(f"Insufficient disk space. Required: {dynamic_threshold_gb:.2f}GB")
            self.cancel()
            return True
        return self.is_canceled

    def _emit_status(self, msg):
        self._emit_signal_or_callback(self.status_update_signal, msg)

    def _emit_progress(self, value):
        self._emit_signal_or_callback(self.progress_update_signal, int(value))

    def _emit_finished(self, success, message):
        if self._finish_emitted:
            return
        self._finish_emitted = True
        if self.is_canceled and not success:
            self._emit_signal_or_callback(self.finished_signal, False, "Canceled by user.")
            return
        self._emit_signal_or_callback(self.finished_signal, bool(success), str(message))

    def _resolve_final_output_path(self):
        os.makedirs(self._output_dir, exist_ok=True)
        idx = 1
        while True:
            target_name = f"Fortnite-Video-{idx}.mp4"
            target_path = os.path.join(self._output_dir, target_name)
            if not os.path.exists(target_path):
                return target_path
            idx += 1

    def run(self):
        try:
            self._emit_status("Starting render worker...")
            pre_err = self.encoder_mgr.get_encoder_preflight_error()
            if pre_err:
                self._emit_status("Encode configuration error.")
                self._emit_finished(False, pre_err)
                return
            self.job_id = str(uuid.uuid4())[:8]
            self.temp_job_dir = os.path.join(tempfile.gettempdir(), f"fvs_job_{self.job_id}")
            os.makedirs(self.temp_job_dir, exist_ok=True)
            scaler_core = ProgressScaler(self.progress_update_signal, 0, 100)
            self._emit_status("Reading source media...")
            source_has_audio = self.prober.has_audio()
            source_audio_kbps = self.prober.get_audio_bitrate() or 192
            self._emit_status("Reading source timing...")
            target_fps_expr = self.prober.get_video_fps_expr()
            timing_info = self.prober.get_video_timing_info()
            if self.logger:
                self.logger.info(f"VIDEO_TIMING: fps={target_fps_expr} vfr={timing_info.get('is_vfr')} observed={timing_info.get('observed_fps'):.3f}")
            text_png_path = None
            if self.portrait_text:
                text_png_path = os.path.join(self.temp_job_dir, "portrait_text.png")
                try:
                    from .text_ops import TextWrapper
                    wrapper = TextWrapper(self.config)
                    final_size, wrapped_lines = wrapper.fit_and_wrap(self.portrait_text, target_width=1000, logger=self.logger)
                    text_to_render = "\n".join(wrapped_lines)
                    gen_ok = generate_text_overlay_png(text_to_render, 1080, 150, final_size, self.config.line_spacing, text_png_path, self.config, self.logger)
                    if not gen_ok or not os.path.exists(text_png_path) or os.path.getsize(text_png_path) == 0:
                        if self.logger: self.logger.error(f"TEXT_GEN_FAILED_OR_EMPTY: ok={gen_ok} exists={os.path.exists(text_png_path)}")
                        text_png_path = None
                    else:
                        if self.logger: self.logger.info(f"TEXT_GEN_VERIFIED: size={os.path.getsize(text_png_path)}")
                except Exception as e:
                    if self.logger: self.logger.warning(f"Skipping text overlay: {e}")
                    text_png_path = None
            music_cfg = self.music_config if hasattr(self, 'music_config') else {}
            g_v, g_a, g_dur = None, None, self.duration_corrected_sec
            granular_v_a_filters = ""
            if self.speed_segments:
                g_str, g_v, g_a, g_dur, _ = self.filter_builder.build_granular_speed_chain(
                    self.input_path, 
                    (self.end_time_ms - self.start_time_ms),
                    self.speed_segments,
                    self.speed_factor,
                    source_cut_start_ms=self.start_time_ms,
                    input_v_label="[0:v]",
                    input_a_label="[0:a]" if source_has_audio else None,
                    target_fps=target_fps_expr
                )
                granular_v_a_filters = g_str
            bitrate_duration_sec = max(0.01, g_dur + max(0.0, self.intro_still_sec))
            audio_kbps = self._choose_audio_bitrate(source_audio_kbps, bitrate_duration_sec)
            video_bitrate_kbps = calculate_video_bitrate(
                self.input_path, bitrate_duration_sec, audio_kbps,
                self.target_mb, self.keep_highest_res, self.logger,
                self._output_resolution_for_bitrate(), target_fps_expr, self.quality_level,
                prober=self.prober
            )
            if self.bg_music_path and not self.music_tracks:
                 self.music_tracks = [(self.bg_music_path, self.bg_music_offset_ms/1000.0, g_dur)]
            music_start_index = 1
            audio_chains, final_a_label = self.filter_builder.build_audio_chain(
                music_config=music_cfg,
                video_start_time=self.start_time_ms/1000.0, video_end_time=self.end_time_ms/1000.0,
                speed_factor=self.speed_factor, disable_fades=self.disable_fades,
                vfade_in_d=0.5 if not self.disable_fades else 0, audio_filter_cmd="", sample_rate=48000,
                music_tracks=self.music_tracks,
                music_start_index=music_start_index,
                total_project_duration=g_dur
            )
            core_path = os.path.normpath(os.path.join(self.temp_job_dir, "core.mp4"))
            ffmpeg_path = self.ffmpeg_path
            if self.intro_abs_time_ms is not None:
                intro_abs_time_sec = max(0.0, float(self.intro_abs_time_ms) / 1000.0)
            elif self.intro_from_midpoint:
                intro_abs_time_sec = float(self.start_time_ms + ((self.end_time_ms - self.start_time_ms) // 2)) / 1000.0
            else:
                intro_abs_time_sec = float(self.start_time_ms) / 1000.0

            def run_ffmpeg(use_cuda, requested_bitrate_kbps):
                current_encoder = self.encoder_mgr.get_initial_encoder() if use_cuda else 'libx264'
                while True:
                    vcodec, rc_label = self.encoder_mgr.get_codec_flags(
                        current_encoder, 
                        requested_bitrate_kbps, g_dur, target_fps_expr,
                        quality_level=self.quality_level,
                        size_locked=bool(self.target_mb)
                    )
                    attempt_core_filters = []
                    v_label = "[0:v]"
                    working_duration_sec = g_dur
                    cfr_filter = f"fps={target_fps_expr}:round=near"
                    txt_input_label = None
                    attempt_granular_filters = granular_v_a_filters
                    attempt_g_v, attempt_g_a = g_v, g_a
                    input_is_cuda_frames = self._uses_cuda_frames(current_encoder)
                    if self.speed_segments and input_is_cuda_frames:
                        attempt_granular_filters, attempt_g_v, attempt_g_a, _, _ = self.filter_builder.build_granular_speed_chain(
                            self.input_path,
                            (self.end_time_ms - self.start_time_ms),
                            self.speed_segments,
                            self.speed_factor,
                            source_cut_start_ms=self.start_time_ms,
                            input_v_label="[0:v]",
                            input_a_label="[0:a]" if source_has_audio else None,
                            target_fps=target_fps_expr,
                            input_is_cuda=True
                        )
                    intro_duration_sec = max(0.0, float(self.intro_still_sec or 0.0))
                    intro_input_index = music_start_index + len(self.music_tracks) if intro_duration_sec > 0.0 else None
                    if text_png_path and os.path.exists(text_png_path) and os.path.getsize(text_png_path) > 0:
                        txt_input_label = f"[{music_start_index + len(self.music_tracks) + (1 if intro_input_index is not None else 0)}:v]"
                        self.logger.info(f"TEXT_OVERLAY: path={text_png_path} label={txt_input_label} size={os.path.getsize(text_png_path)}")
                    else:
                        if self.portrait_text:
                            self.logger.warning(f"TEXT_OVERLAY_MISSING: path={text_png_path}")
                    if attempt_granular_filters:
                        if attempt_granular_filters == granular_v_a_filters:
                            attempt_core_filters.append(granular_v_a_filters)
                        else:
                            attempt_core_filters.append(attempt_granular_filters)
                        v_stabilized_pad = g_v
                        a_prepared_pad = g_a
                    else:
                        if input_is_cuda_frames:
                            attempt_core_filters.append(f"{v_label}hwdownload,format=nv12,format=yuv420p[v_decode_cpu]")
                            v_label = "[v_decode_cpu]"
                        v_sync = f"{v_label}setpts='(PTS-STARTPTS)/{self.speed_factor:.4f}',{cfr_filter}[v_stabilized]"
                        v_stabilized_pad = "[v_stabilized]"
                        a_prepared_pad = "[a_prepared_base]"
                        audio_speed_filters = []
                        tmp_s = self.speed_factor
                        while tmp_s < 0.5: audio_speed_filters.append("atempo=0.5"); tmp_s /= 0.5
                        while tmp_s > 2.0: audio_speed_filters.append("atempo=2.0"); tmp_s /= 2.0
                        audio_speed_filters.append(f"atempo={tmp_s:.4f}")
                        if source_has_audio:
                            a_sync = f"[0:a]asetpts=PTS-STARTPTS,{','.join(audio_speed_filters)},aresample=48000:async=1:min_comp=0.01[a_prepared_base]"
                        else:
                            a_sync = f"anullsrc=r=48000:cl=stereo,atrim=duration={working_duration_sec:.4f},asetpts=PTS-STARTPTS[a_prepared_base]"
                        attempt_core_filters.append(v_sync)
                        attempt_core_filters.append(a_sync)
                    if self.is_mobile_format:
                        coords = self.config.get_mobile_coordinates(self.logger)
                        v_mobile_chain, v_mobile_out = self.filter_builder.build_mobile_filter_chain(
                            v_stabilized_pad, coords, self.is_boss_hp, self.show_teammates_overlay, 
                            show_spectating=self.show_spectating_overlay,
                            txt_input_label=txt_input_label,
                            use_cuda=False,
                            original_resolution=self.original_resolution
                        )
                        if v_mobile_chain:
                            attempt_core_filters.append(v_mobile_chain)
                        v_final_pad = v_mobile_out
                    else:
                        v_final_pad = v_stabilized_pad
                    for part in audio_chains:
                        if part.startswith("[0:a]"):
                            part = part.replace("[0:a]", a_prepared_pad).replace("anull,", "")
                        if part and part.strip():
                            attempt_core_filters.append(part.strip())
                    v_output_pad = v_final_pad
                    a_output_pad = final_a_label
                    output_duration_sec = working_duration_sec
                    if intro_input_index is not None:
                        try:
                            fps_q = min(Fraction(60, 1), max(Fraction(1, 1), Fraction(str(target_fps_expr))))
                        except Exception:
                            fps_q = Fraction(60, 1)
                        intro_frame_count = max(1, int((Fraction(str(intro_duration_sec)) * fps_q) + Fraction(1, 2)))
                        loop_frames = max(0, intro_frame_count - 1)
                        target_w, target_h = (1080, 1920) if self.is_mobile_format else (1920, 1080)
                        if not self.is_mobile_format:
                            attempt_core_filters.append(f"{v_final_pad}scale={target_w}:{target_h}:force_original_aspect_ratio=increase:flags=lanczos,crop={target_w}:{target_h},setsar=1,format=yuv420p[v_core_ready]")
                            v_output_pad = "[v_core_ready]"
                        if self._uses_cuda_frames(current_encoder):
                            attempt_core_filters.append(f"[{intro_input_index}:v]scale_cuda=w={target_w}:h={target_h}:force_original_aspect_ratio=increase:interp_algo=lanczos,hwdownload,format=nv12,crop={target_w}:{target_h},select='eq(n\\,0)',format=yuv420p,setsar=1,loop=loop={loop_frames}:size=1:start=0,fps={target_fps_expr}:round=near,setpts=N/({target_fps_expr})/TB[v_intro]")
                        else:
                            attempt_core_filters.append(f"[{intro_input_index}:v]scale={target_w}:{target_h}:force_original_aspect_ratio=increase:flags=lanczos,crop={target_w}:{target_h},select='eq(n\\,0)',format=yuv420p,setsar=1,loop=loop={loop_frames}:size=1:start=0,fps={target_fps_expr}:round=near,setpts=N/({target_fps_expr})/TB[v_intro]")
                        attempt_core_filters.append(f"anullsrc=r=48000:cl=stereo,atrim=duration={intro_duration_sec:.4f},asetpts=PTS-STARTPTS[a_intro]")
                        attempt_core_filters.append(f"[v_intro][a_intro]{v_output_pad}{a_output_pad}concat=n=2:v=1:a=1[v_concat_raw][a_render_out]")
                        v_output_pad = "[v_concat_raw]"
                        a_output_pad = "[a_render_out]"
                        output_duration_sec = working_duration_sec + intro_duration_sec
                    final_video_pad = "[v_render_out]"
                    attempt_core_filters.append(f"{v_output_pad}fps={target_fps_expr}:round=near,setpts=N/({target_fps_expr})/TB{final_video_pad}")
                    v_output_pad = final_video_pad
                    raw_segments = [f.strip().strip(";,") for f in attempt_core_filters if f and f.strip()]
                    full_filter_str = ";".join(raw_segments)
                    filter_script_path = os.path.join(self.temp_job_dir, "filter_complex.txt")
                    with open(filter_script_path, 'w', encoding='utf-8') as f:
                        f.write(full_filter_str)
                    input_duration_sec = max(0.001, (self.end_time_ms - self.start_time_ms) / 1000.0)
                    ffmpeg_inputs = ['-fflags', '+genpts+igndts', '-err_detect', 'explode']
                    ffmpeg_inputs += self._hardware_decode_flags(current_encoder)
                    ffmpeg_inputs += ['-ss', f"{self.start_time_ms/1000.0:.3f}", '-t', f"{input_duration_sec:.3f}", '-i', self.input_path]
                    for track_path, _, _ in self.music_tracks:
                        ffmpeg_inputs += ['-i', track_path]
                    if intro_input_index is not None:
                        ffmpeg_inputs += ['-fflags', '+genpts+igndts', '-err_detect', 'explode']
                        ffmpeg_inputs += self._hardware_decode_flags(current_encoder)
                        ffmpeg_inputs += ['-ss', f"{intro_abs_time_sec:.6f}", '-t', '0.2', '-i', self.input_path]
                    if txt_input_label:
                        ffmpeg_inputs += ['-loop', '1', '-i', text_png_path]
                    ffmpeg_cmd = [ffmpeg_path, '-y', '-hide_banner', '-progress', 'pipe:1'] + ffmpeg_inputs + [
                        '-filter_complex_script', filter_script_path,
                        '-map', v_output_pad, '-map', a_output_pad,
                        '-fps_mode', 'cfr',
                        '-r', target_fps_expr,
                        '-max_muxing_queue_size', '4096',
                        '-video_track_timescale', '60000',
                        '-c:v', vcodec[1]] + vcodec[2:] + [
                        '-c:a', 'aac', '-b:a', f"{audio_kbps}k",
                        '-t', f"{output_duration_sec:.3f}",
                        '-movflags', '+faststart',
                        core_path
                    ]
                    self.logger.info(f"FFMPEG CMD (Encoder: {current_encoder}, RC: {rc_label}, Bitrate: {requested_bitrate_kbps}k): {' '.join(ffmpeg_cmd)}")
                    self.current_process = create_subprocess(ffmpeg_cmd)
                    error_lines = []

                    def on_err(line):
                        error_lines.append(line)
                        self._emit_status(f"FFmpeg Error: {line}")

                    def on_output(line):
                        self.logger.info(f"FFmpeg: {line}")
                        self._emit_status(f"FFmpeg: {line}")
                    monitor_stats = monitor_ffmpeg_progress(
                        self.current_process,
                        output_duration_sec,
                        scaler_core,
                        self._monitor_disk_space,
                        self.logger,
                        on_error_line=on_err,
                        on_output_line=on_output
                    )
                    exit_code = self.current_process.wait()
                    if exit_code == 0:
                        valid, validation_msg = self._validate_render_output(core_path, output_duration_sec, target_fps_expr, monitor_stats)
                        if valid:
                            return True, output_duration_sec, monitor_stats
                        self.logger.error(f"Render validation failed: {validation_msg}")
                        return False, output_duration_sec, monitor_stats
                    if use_cuda and not self.is_canceled:
                        fallbacks = self.encoder_mgr.get_fallback_list(current_encoder, allow_cpu=False)
                        if fallbacks:
                            next_enc = fallbacks[0]
                            self.logger.warning(f"Hardware encoder '{current_encoder}' failed (exit {exit_code}). Falling back to '{next_enc}'...")
                            current_encoder = next_enc
                            scaler_core.current_base = 0 
                            continue
                    if error_lines:
                        self.logger.error(f"FFmpeg failed with errors: {' | '.join(error_lines[-5:])}")
                    return False, output_duration_sec, {}
            self._emit_status("Encoding core video...")
            size_bounds = self._target_size_bounds()
            current_bitrate_kbps = int(video_bitrate_kbps)
            final_duration_sec = bitrate_duration_sec
            success = False
            for size_attempt in range(1, 6):
                if os.path.exists(core_path):
                    try:
                        os.remove(core_path)
                    except OSError as e:
                        self.logger.warning(f"Could not remove stale core output {core_path}: {e}")
                self.logger.info(f"SIZE_PASS: attempt={size_attempt} bitrate={current_bitrate_kbps}k target_mb={self.target_mb}")
                success, final_duration_sec, _ = run_ffmpeg(use_cuda=(self.hardware_strategy != 'CPU'), requested_bitrate_kbps=current_bitrate_kbps)
                if not success:
                    self._emit_finished(False, "FFmpeg core encoding failed.")
                    return
                if not size_bounds:
                    break
                actual_bytes = os.path.getsize(core_path)
                low_bytes, high_bytes = size_bounds
                self.logger.info(f"SIZE_CHECK: actual={actual_bytes} low={low_bytes} high={high_bytes}")
                if low_bytes <= actual_bytes <= high_bytes:
                    break
                if actual_bytes < low_bytes and current_bitrate_kbps >= EncoderManager.H264_LEVEL_42_MAX_KBPS:
                    self.logger.info("SIZE_CHECK: output under target at legal H.264 level cap; accepting max-quality result.")
                    break
                target_bytes = max(1, int(Fraction(high_bytes * 199, 200)))
                ratio = Fraction(target_bytes, max(1, actual_bytes))
                if actual_bytes > high_bytes:
                    ratio = min(Fraction(97, 100), ratio)
                else:
                    ratio = max(Fraction(103, 100), ratio)
                next_bitrate = int(max(300, min(EncoderManager.H264_LEVEL_42_MAX_KBPS, current_bitrate_kbps * ratio)))
                if abs(next_bitrate - current_bitrate_kbps) < 25:
                    next_bitrate = current_bitrate_kbps + (25 if actual_bytes < low_bytes else -25)
                current_bitrate_kbps = int(max(300, min(EncoderManager.H264_LEVEL_42_MAX_KBPS, next_bitrate)))
            if size_bounds:
                actual_bytes = os.path.getsize(core_path) if os.path.exists(core_path) else 0
                low_bytes, high_bytes = size_bounds
                if actual_bytes > high_bytes:
                    self._emit_finished(False, f"Final file size missed target: {actual_bytes / (1024 * 1024):.2f}MB for {self.target_mb:.2f}MB target.")
                    return
                if actual_bytes < low_bytes and current_bitrate_kbps < EncoderManager.H264_LEVEL_42_MAX_KBPS:
                    self._emit_finished(False, f"Final file size missed target: {actual_bytes / (1024 * 1024):.2f}MB for {self.target_mb:.2f}MB target.")
                    return
            self._emit_status("Finalizing output...")
            if not os.path.exists(core_path) or os.path.getsize(core_path) <= 0:
                self._emit_finished(False, "Final render output is missing.")
                return
            final_output = self._resolve_final_output_path()
            try:
                if os.path.exists(final_output):
                    os.remove(final_output)
            except OSError as e:
                self._emit_finished(False, f"Could not replace existing output file: {e}")
                return
            shutil.move(core_path, final_output)
            self._emit_progress(100)
            self._emit_finished(True, final_output)
        except Exception as e:
            if self.logger: self.logger.exception(f"FATAL: {e}")
            self._emit_finished(False, str(e))
        finally:
            if hasattr(self, 'temp_job_dir'): shutil.rmtree(self.temp_job_dir, ignore_errors=True)
