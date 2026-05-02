import os
import tempfile
import uuid
import shutil
from typing import Tuple, Dict, Any, Optional, List
from PyQt5.QtCore import QThread, pyqtSignal
from .system_utils import create_subprocess, kill_process_tree, check_disk_space, monitor_ffmpeg_progress
from .filter_builder import FilterBuilder
from .step_concat import ConcatProcessor
from .step_intro import IntroProcessor
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
                 quality_level=2, bg_music_path=None, bg_music_volume=0.8,
                 bg_music_offset_ms=0, original_total_duration_ms=0,
                 disable_fades=False, intro_still_sec=0,
                 intro_from_midpoint=False, intro_abs_time_ms=None,
                 portrait_text=None, music_config=None, speed_segments=None,
                 hardware_strategy='CPU', music_tracks=None, script_dir=None,
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
        self.portrait_text = portrait_text
        self.bg_music_path = bg_music_path
        self.bg_music_volume = bg_music_volume
        self.bg_music_offset_ms = bg_music_offset_ms
        self.disable_fades = disable_fades
        self.intro_still_sec = float(intro_still_sec or 0.0)
        self.intro_from_midpoint = bool(intro_from_midpoint)
        self.intro_abs_time_ms = int(intro_abs_time_ms) if intro_abs_time_ms is not None else None
        self.speed_segments = self._normalize_speed_segments(speed_segments)
        self.hardware_strategy = hardware_strategy
        self.music_config = music_config or {}
        self.music_tracks = music_tracks or []
        self.config = VideoConfig(self.base_dir)
        self.keep_highest_res, self.target_mb, self.quality_level = self.config.get_quality_settings(quality_level)
        self.filter_builder = FilterBuilder(self.logger)
        self.ffmpeg_path = os.path.join(self.base_dir, 'binaries', 'ffmpeg.exe')
        if not os.path.exists(self.ffmpeg_path):
            self.ffmpeg_path = 'ffmpeg'
        self.encoder_mgr = EncoderManager(self.logger, hardware_strategy=self.hardware_strategy, ffmpeg_path=self.ffmpeg_path)
        self.prober = MediaProber(os.path.join(self.base_dir, 'binaries'), self.input_path)
        self.current_process = None
        self.is_canceled = False
        self.duration_corrected_sec = (self.end_time_ms - self.start_time_ms) / 1000.0 / self.speed_factor
        self._output_dir = os.path.join(os.path.expanduser("~"), "Downloads")

    def _normalize_speed_segments(self, raw_segments):
        normalized = []
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
            normalized.append({"start_ms": s_ms, "end_ms": e_ms, "speed": spd})
        normalized.sort(key=lambda x: x["start_ms"])
        return normalized

    def _hardware_decode_flags(self, encoder_name: str) -> list[str]:
        if str(self.hardware_strategy or "").upper() == "CPU" or encoder_name == "libx264":
            return []
        return ["-hwaccel", "dxva2"]
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
            pass

    def cancel(self):
        self.is_canceled = True
        if self.current_process: kill_process_tree(self.current_process.pid, self.logger)

    def _monitor_disk_space(self):
        try:
            os.makedirs(self._output_dir, exist_ok=True)
        except Exception:
            pass
        dynamic_threshold_gb = 0.5
        if hasattr(self, 'target_mb') and self.target_mb:
            dynamic_threshold_gb = max(0.5, (self.target_mb * 3.0) / 1024.0)
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
            self.job_id = str(uuid.uuid4())[:8]
            self.temp_job_dir = os.path.join(tempfile.gettempdir(), f"fvs_job_{self.job_id}")
            os.makedirs(self.temp_job_dir, exist_ok=True)
            scaler_core = ProgressScaler(self.progress_update_signal, 0, 50)
            source_has_audio = self.prober.has_audio()
            source_audio_kbps = self.prober.get_audio_bitrate() or 192
            audio_kbps = max(192, int(source_audio_kbps))
            target_fps_expr = self.prober.get_video_fps_expr()
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
                g_str, g_v, g_a, g_dur, t_map = self.filter_builder.build_granular_speed_chain(
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
            video_bitrate_kbps = calculate_video_bitrate(
                self.input_path, bitrate_duration_sec, audio_kbps,
                self.target_mb, self.keep_highest_res, self.logger,
                self.original_resolution, target_fps_expr, self.quality_level,
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

            def run_ffmpeg(use_cuda):
                current_encoder = self.encoder_mgr.get_initial_encoder() if use_cuda else 'libx264'
                while True:
                    vcodec, rc_label = self.encoder_mgr.get_codec_flags(
                        current_encoder, 
                        video_bitrate_kbps, g_dur, target_fps_expr,
                        quality_level=self.quality_level
                    )
                    attempt_core_filters = []
                    v_label = "[0:v]"
                    working_duration_sec = g_dur
                    cfr_filter = f"fps={target_fps_expr}:round=near"
                    txt_input_label = None
                    if text_png_path and os.path.exists(text_png_path) and os.path.getsize(text_png_path) > 0:
                        txt_input_label = f"[{music_start_index + len(self.music_tracks)}:v]"
                        self.logger.info(f"TEXT_OVERLAY: path={text_png_path} label={txt_input_label} size={os.path.getsize(text_png_path)}")
                    else:
                        if self.portrait_text:
                            self.logger.warning(f"TEXT_OVERLAY_MISSING: path={text_png_path}")
                    if granular_v_a_filters:
                        attempt_core_filters.append(granular_v_a_filters)
                        v_stabilized_pad = g_v
                        a_prepared_pad = g_a
                    else:
                        v_sync = f"{v_label}{cfr_filter},setpts='(PTS-STARTPTS)/{self.speed_factor:.4f}'[v_stabilized]"
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
                            txt_input_label=txt_input_label,
                            use_cuda=(use_cuda and self.hardware_strategy == 'NVIDIA'),
                            original_resolution=self.original_resolution
                        )
                        attempt_core_filters.append(v_mobile_chain)
                        v_final_pad = v_mobile_out
                    else:
                        v_final_pad = v_stabilized_pad
                    for part in audio_chains:
                        if part.startswith("[0:a]"):
                            part = part.replace("[0:a]", a_prepared_pad).replace("anull,", "")
                        attempt_core_filters.append(part)
                    full_filter_str = ";".join(attempt_core_filters)
                    filter_script_path = os.path.join(self.temp_job_dir, "filter_complex.txt")
                    with open(filter_script_path, 'w', encoding='utf-8') as f:
                        f.write(full_filter_str)
                    input_duration_sec = max(0.001, (self.end_time_ms - self.start_time_ms) / 1000.0)
                    ffmpeg_inputs = ['-fflags', '+genpts+discardcorrupt', '-err_detect', 'explode']
                    ffmpeg_inputs += self._hardware_decode_flags(current_encoder)
                    ffmpeg_inputs += ['-ss', f"{self.start_time_ms/1000.0:.3f}", '-t', f"{input_duration_sec:.3f}", '-i', self.input_path]
                    for track_path, _, _ in self.music_tracks:
                        ffmpeg_inputs += ['-i', track_path]
                    if txt_input_label:
                        ffmpeg_inputs += ['-loop', '1', '-i', text_png_path]
                    ffmpeg_cmd = [ffmpeg_path, '-y', '-hide_banner', '-xerror', '-progress', 'pipe:1'] + ffmpeg_inputs + [
                        '-filter_complex_script', filter_script_path,
                        '-map', v_final_pad, '-map', final_a_label,
                        '-fps_mode', 'cfr',
                        '-c:v', vcodec[1]] + vcodec[2:] + [
                        '-c:a', 'aac', '-b:a', f"{audio_kbps}k",
                        '-t', f"{working_duration_sec:.3f}",
                        core_path
                    ]
                    self.logger.info(f"FFMPEG CMD (Encoder: {current_encoder}): {' '.join(ffmpeg_cmd)}")
                    self.current_process = create_subprocess(ffmpeg_cmd)
                    error_lines = []

                    def on_err(line):
                        error_lines.append(line)
                        if self.status_update_signal:
                            self.status_update_signal.emit(f"FFmpeg Error: {line}")

                    def on_output(line):
                        self.logger.info(f"FFmpeg: {line}")
                        if self.status_update_signal:
                            self.status_update_signal.emit(f"FFmpeg: {line}")
                    monitor_ffmpeg_progress(
                        self.current_process,
                        working_duration_sec,
                        scaler_core,
                        self._monitor_disk_space,
                        self.logger,
                        on_error_line=on_err,
                        on_output_line=on_output
                    )
                    exit_code = self.current_process.wait()
                    if exit_code == 0:
                        return True
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
                    return False
            self._emit_status("Encoding core video...")
            success = run_ffmpeg(use_cuda=(self.hardware_strategy != 'CPU'))
            if not success:
                self._emit_finished(False, "FFmpeg core encoding failed.")
                return
            intro_path = None
            intro_abs_time_sec = None
            if self.intro_abs_time_ms is not None:
                intro_abs_time_sec = max(0.0, float(self.intro_abs_time_ms) / 1000.0)
            elif self.intro_from_midpoint:
                intro_abs_time_sec = float(self.start_time_ms + ((self.end_time_ms - self.start_time_ms) // 2)) / 1000.0
            else:
                intro_abs_time_sec = float(self.start_time_ms) / 1000.0
            if self.intro_still_sec > 0.0:
                self._emit_status("Creating intro frame...")
                intro_scaler = ProgressScaler(self.progress_update_signal, 50, 20)
                intro_processor = IntroProcessor(
                    ffmpeg_path=ffmpeg_path,
                    logger=self.logger,
                    encoder_mgr=self.encoder_mgr,
                    temp_dir=self.temp_job_dir,
                )
                intro_path = intro_processor.create_intro(
                    input_path=self.input_path,
                    intro_abs_time=intro_abs_time_sec,
                    intro_still_sec=self.intro_still_sec,
                    is_mobile=self.is_mobile_format,
                    audio_kbps=audio_kbps,
                    video_bitrate_kbps=video_bitrate_kbps,
                    progress_signal=intro_scaler,
                    is_canceled_func=self._monitor_disk_space,
                    sample_rate=48000,
                    fps_expr=target_fps_expr,
                    preferred_encoder=self.encoder_mgr.get_initial_encoder(),
                    original_res_str=self.original_resolution,
                )
                if not intro_path:
                    self.logger.warning("INTRO: Creation failed. Continuing with core-only output.")
            self._emit_status("Finalizing output...")
            concat_scaler = ProgressScaler(self.progress_update_signal, 70, 30)
            concat_processor = ConcatProcessor(
                ffmpeg_path=ffmpeg_path,
                logger=self.logger,
                base_dir=self.base_dir,
                temp_dir=self.temp_job_dir,
            )
            concat_output = concat_processor.run_concat(
                intro_path=intro_path,
                core_path=core_path,
                progress_signal=concat_scaler,
                video_bitrate_kbps=video_bitrate_kbps,
                cancellation_check=self._monitor_disk_space,
                fps_expr=target_fps_expr,
                preferred_encoder=self.encoder_mgr.get_initial_encoder(),
                force_reencode=bool(intro_path),
                audio_kbps=audio_kbps,
                audio_sample_rate=48000,
                is_mobile=self.is_mobile_format,
            )
            if not concat_output:
                self._emit_finished(False, "Final concat stage failed.")
                return
            final_output = self._resolve_final_output_path()
            try:
                if os.path.exists(final_output):
                    os.remove(final_output)
            except Exception:
                pass
            shutil.move(concat_output, final_output)
            self._emit_progress(100)
            self._emit_finished(True, final_output)
        except Exception as e:
            if self.logger: self.logger.exception(f"FATAL: {e}")
            self._emit_finished(False, str(e))
        finally:
            if hasattr(self, 'temp_job_dir'): shutil.rmtree(self.temp_job_dir, ignore_errors=True)
