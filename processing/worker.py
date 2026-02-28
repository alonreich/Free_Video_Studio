import os
import tempfile
import time
import uuid
from PyQt5.QtCore import QThread
from .config_data import VideoConfig
from .system_utils import create_subprocess, monitor_ffmpeg_progress, kill_process_tree, check_disk_space, check_filter_option
from .text_ops import TextWrapper, apply_bidi_formatting
from .media_utils import MediaProber, calculate_video_bitrate
from .filter_builder import FilterBuilder
from .encoders import EncoderManager
from .step_intro import IntroProcessor
from .step_concat import ConcatProcessor

class ProgressScaler:
    """Helper to map a 0-100 progress signal to a specific sub-range (e.g., 0-90)."""

    def __init__(self, real_signal, start_pct, range_pct):
        self.real_signal = real_signal
        self.start_pct = start_pct
        self.range_pct = range_pct

    def emit(self, val):
        weighted_val = int(self.start_pct + (val / 100.0) * self.range_pct)
        out_val = min(100, weighted_val)
        if hasattr(self.real_signal, "emit"):
            self.real_signal.emit(out_val)
        elif callable(self.real_signal):
            self.real_signal(out_val)

class ProcessThread(QThread):
    def __init__(self, input_path, start_time_ms, end_time_ms, original_resolution, is_mobile_format, speed_factor,
                 script_dir, progress_update_signal, status_update_signal, finished_signal, logger,
                 is_boss_hp=False, show_teammates_overlay=False, quality_level: int = 2,
                 bg_music_path=None, bg_music_volume=None, bg_music_offset_ms=0, original_total_duration_ms=0,
                 disable_fades=False, intro_still_sec: float = 0.0, intro_from_midpoint: bool = False, intro_abs_time_ms: int = None,
                 portrait_text: str = None, music_config=None, speed_segments=None, hardware_strategy: str = "CPU"):
        super().__init__()
        self.music_config = music_config if music_config else {}
        self.hardware_strategy = str(hardware_strategy or "CPU")
        self.input_path = input_path
        self.start_time_ms = int(start_time_ms)
        self.end_time_ms = int(end_time_ms)
        self.duration_ms = self.end_time_ms - self.start_time_ms
        self.original_resolution = original_resolution
        self.is_mobile_format = is_mobile_format
        self.speed_factor = float(speed_factor)
        self.logger = logger
        self.speed_segments = self._normalize_speed_segments(speed_segments)
        self.script_dir = script_dir
        self.base_dir = os.path.abspath(os.path.join(self.script_dir, os.pardir))
        self.bin_dir = os.path.join(self.base_dir, 'binaries')
        self.progress_update_signal = progress_update_signal
        self.status_update_signal = status_update_signal
        self.finished_signal = finished_signal
        self.bg_music_path = bg_music_path if (bg_music_path and os.path.isfile(bg_music_path)) else None
        self.bg_music_volume = float(bg_music_volume) if bg_music_volume is not None else None
        self.bg_music_offset_ms = int(bg_music_offset_ms or 0)
        self.portrait_text = portrait_text
        self.is_boss_hp = is_boss_hp
        self.show_teammates_overlay = bool(show_teammates_overlay)
        self.disable_fades = bool(disable_fades)
        self.intro_from_midpoint = bool(intro_from_midpoint)
        self.intro_still_sec = float(intro_still_sec or 0.0)
        self.intro_abs_time_ms = int(intro_abs_time_ms) if intro_abs_time_ms is not None else None
        self.original_total_duration_ms = int(original_total_duration_ms or 0)
        self.config = VideoConfig(self.base_dir)
        self.keep_highest_res, self.target_mb, self.quality_level = self.config.get_quality_settings(quality_level)
        self.text_wrapper = TextWrapper(self.config)
        self.filter_builder = FilterBuilder(self.logger)
        self.encoder_mgr = EncoderManager(self.logger, hardware_strategy=self.hardware_strategy)
        self.prober = MediaProber(self.bin_dir, self.input_path)
        self.current_process = None
        self.is_canceled = False
        self.filter_scripts = []
        self.duration_corrected_sec = (self.duration_ms / self.speed_factor / 1000.0) if self.speed_factor != 1.0 else (self.duration_ms / 1000.0)
        self._output_dir = os.path.join(self.base_dir, "!!!_Output_Video_Files_!!!")

    def _emit(self, target, *args):
        try:
            if hasattr(target, "emit"):
                target.emit(*args)
            elif callable(target):
                target(*args)
        except Exception as e:
            try:
                self.logger.warning(f"SIGNAL_EMIT_FAIL: {e}")
            except Exception:
                pass

    def _emit_progress(self, val: int):
        self._emit(self.progress_update_signal, val)

    def _emit_status(self, msg: str):
        self._emit(self.status_update_signal, msg)

    def _emit_finished(self, success: bool, payload: str):
        self._emit(self.finished_signal, success, payload)

    def _normalize_speed_segments(self, raw_segments):
        """Normalize granular segments to a stable internal contract."""
        normalized = []
        if not raw_segments:
            return normalized
        for seg in raw_segments:
            if not isinstance(seg, dict):
                continue
            has_abs = ("start" in seg and "end" in seg)
            has_rel = ("start_ms" in seg and "end_ms" in seg)
            start_raw = seg.get("start") if has_abs else seg.get("start_ms")
            end_raw = seg.get("end") if has_abs else seg.get("end_ms")
            try:
                start = float(start_raw)
                end = float(end_raw)
                speed = float(seg.get("speed", self.speed_factor))
            except Exception:
                continue
            if end <= start:
                continue
            normalized.append({
                "start": start,
                "end": end,
                "start_ms": start,
                "end_ms": end,
                "speed": speed,
                "is_relative": bool(has_rel and not has_abs),
            })
        normalized.sort(key=lambda s: s["start"])
        if normalized and self.logger:
            self.logger.info("GRANULAR: normalized %d segments for worker", len(normalized))
        return normalized

    def _fps_expr_to_float(self, fps_expr, default=60.0):
        try:
            if isinstance(fps_expr, str) and '/' in fps_expr:
                n, d = fps_expr.split('/', 1)
                d_f = float(d)
                if d_f <= 0.0:
                    return float(default)
                return float(n) / d_f
            return float(fps_expr)
        except Exception:
            return float(default)

    def cancel(self):
        self.logger.info("Cancellation requested.")
        self.is_canceled = True
        if self.current_process:
            kill_process_tree(self.current_process.pid, self.logger)

    def _monitor_disk_space(self):
        """[FIX #14] Callback for continuous disk space checking."""
        try:
            if not check_disk_space(self._output_dir, 0.2):
                self.logger.critical("Disk space exhausted during render!")
                self.cancel()
                self._emit_finished(False, "Render cancelled: Disk full.")
                return True
        except Exception:
            pass
        return self.is_canceled

    def run(self):
            textfile_path = None
            size = None
            has_intro = float(self.intro_still_sec or 0.0) > 0
            if has_intro:
                scaler_core = ProgressScaler(self.progress_update_signal, 0, 85)
                scaler_intro = ProgressScaler(self.progress_update_signal, 85, 10)
                scaler_concat = ProgressScaler(self.progress_update_signal, 95, 5)
            else:
                scaler_core = ProgressScaler(self.progress_update_signal, 0, 95)
                scaler_intro = ProgressScaler(self.progress_update_signal, 95, 0)
                scaler_concat = ProgressScaler(self.progress_update_signal, 95, 5)
            self.job_id = str(uuid.uuid4())[:8]
            self.temp_job_dir = os.path.join(tempfile.gettempdir(), f"fvs_job_{self.job_id}")
            os.makedirs(self.temp_job_dir, exist_ok=True)
            if 'timeline_start_ms' in self.music_config:
                self.music_config['timeline_start_sec'] = self.music_config.pop('timeline_start_ms') / 1000.0
            if 'timeline_end_ms' in self.music_config:
                self.music_config['timeline_end_sec'] = self.music_config.pop('timeline_end_ms') / 1000.0
            if 'file_offset_ms' in self.music_config:
                self.music_config['file_offset_sec'] = self.music_config.pop('file_offset_ms') / 1000.0
            if self.bg_music_path:
                self.music_config['path'] = self.bg_music_path
                if 'timeline_start_sec' not in self.music_config:
                    self.music_config['timeline_start_sec'] = self.start_time_ms / 1000.0
                if 'file_offset_sec' not in self.music_config:
                    self.music_config['file_offset_sec'] = self.bg_music_offset_ms / 1000.0
                if 'timeline_end_sec' not in self.music_config:
                    self.music_config['timeline_end_sec'] = self.end_time_ms / 1000.0
                if 'volume' not in self.music_config:
                    self.music_config['volume'] = self.bg_music_volume
                self.logger.info(f"AUDIO CONFIG: {self.music_config}")
            temp_dir = self.temp_job_dir
            core_path = None
            intro_path = None
            output_path = None
            ffmpeg_path = os.path.join(self.bin_dir, 'ffmpeg.exe')
            cuda_overlay_has_format = check_filter_option(ffmpeg_path, "overlay_cuda", "format")
            cuda_scale_has_format = check_filter_option(ffmpeg_path, "scale_cuda", "format")
            if 'cuda_caps' not in dir(self.filter_builder): self.filter_builder.cuda_caps = {}
            self.filter_builder.cuda_caps['overlay_format'] = cuda_overlay_has_format
            self.filter_builder.cuda_caps['scale_format'] = cuda_scale_has_format
            try:
                if self.is_canceled: return
                estimated_output_gb = (self.target_mb if self.target_mb else 500) / 1024.0
                required_space_gb = (estimated_output_gb * 2.0) + 1.0 
                if not check_disk_space(self._output_dir, required_space_gb):
                    self._emit_finished(False, f"Insufficient disk space. Need at least {required_space_gb:.1f} GB free.")
                    return
                has_bg = (self.bg_music_path is not None)
                fade_dur_ms = int(self.config.fade_duration * 1000)
                clip_start_ms = self.start_time_ms
                clip_end_ms = self.end_time_ms
                source_cut_start_ms = self.start_time_ms
                source_cut_end_ms = self.end_time_ms
                vfade_in_duration_ms = 0
                vfade_out_duration_ms = 0
                if not self.disable_fades:
                    if clip_start_ms >= fade_dur_ms:
                        source_cut_start_ms = clip_start_ms - fade_dur_ms
                        vfade_in_duration_ms = fade_dur_ms
                    if self.original_total_duration_ms > 0 and (clip_end_ms <= self.original_total_duration_ms - fade_dur_ms):
                        source_cut_end_ms = clip_end_ms + fade_dur_ms
                        vfade_out_duration_ms = fade_dur_ms
                source_cut_duration_ms = source_cut_end_ms - source_cut_start_ms
                final_clip_duration_ms = int(source_cut_duration_ms / self.speed_factor)
                granular_video_label = "[0:v]"
                granular_audio_label = None
                granular_filter_str = ""
                time_mapper = None
                if self.speed_segments:
                    g_str, g_v, g_a, g_dur, t_map = self.filter_builder.build_granular_speed_chain(
                        self.input_path, source_cut_duration_ms, self.speed_segments, self.speed_factor,
                        source_cut_start_ms=source_cut_start_ms
                    )
                    granular_filter_str = g_str
                    granular_video_label = g_v
                    granular_audio_label = g_a
                    time_mapper = t_map
                    self.duration_corrected_sec = g_dur
                    vfade_out_sec = vfade_out_duration_ms / 1000.0 / self.speed_factor
                    final_clip_duration_ms = int(self.duration_corrected_sec * 1000)
                    vfade_out_start_ms = final_clip_duration_ms - int(vfade_out_duration_ms / self.speed_factor)
                else:
                    vfade_out_start_ms = final_clip_duration_ms - int(vfade_out_duration_ms / self.speed_factor)
                    self.duration_corrected_sec = final_clip_duration_ms / 1000.0
                source_audio_kbps = self.prober.get_audio_bitrate() or 320
                audio_kbps = max(64, min(int(source_audio_kbps), 512))
                self._emit_status(f"Audio quality: source ~{source_audio_kbps} kbps -> output AAC {audio_kbps} kbps.")
                sample_rate = int(self.prober.get_sample_rate() or 48000)
                target_fps_expr = self.prober.get_video_fps_expr(fallback="60000/1001")
                target_fps_value = self._fps_expr_to_float(target_fps_expr, default=60.0)
                if self.is_mobile_format and target_fps_value > 62.0:
                    self.logger.info(f"FPS_CAP: Capping mobile output from {target_fps_value} to 60fps.")
                    target_fps_expr = "60"
                    target_fps_value = 60.0
                video_track_timescale = '120000' if target_fps_value >= 100.0 else '60000'
                if has_bg:
                    sample_rate = 48000
                else:
                    if sample_rate < 32000 or sample_rate > 192000:
                        sample_rate = 48000
                intro_len_sec = max(0.0, self.intro_still_sec) if self.intro_still_sec > 0 else 0.0
                eff_dur_sec = self.duration_corrected_sec + intro_len_sec
                video_bitrate_kbps = calculate_video_bitrate(
                    self.input_path, eff_dur_sec, audio_kbps, self.target_mb, self.keep_highest_res, logger=self.logger
                )
                if video_bitrate_kbps is None:
                    if not self.keep_highest_res:
                        self._emit_finished(False, "Video duration is too short for target size.")
                        return
                    video_bitrate_kbps = 2500
                MAX_SAFE_BITRATE = 200000 
                if video_bitrate_kbps > MAX_SAFE_BITRATE:
                    self.logger.info(f"Clamping bitrate from {video_bitrate_kbps}k to {MAX_SAFE_BITRATE}k.")
                    video_bitrate_kbps = MAX_SAFE_BITRATE
                base_target_bitrate_kbps = int(video_bitrate_kbps)
                q_key = 4 if (self.keep_highest_res or self.quality_level >= 4) else max(0, min(3, int(self.quality_level)))
                try:
                    src_w, src_h = map(int, str(self.original_resolution).lower().split('x'))
                    pixel_scale = max(0.75, min(4.0, (src_w * src_h) / float(1920 * 1080)))
                except Exception:
                    pixel_scale = 1.0
                motion_scale = (pixel_scale ** 0.5) * (1.6 if target_fps_value >= 100.0 else 1.0)
                if self.target_mb and not self.keep_highest_res:
                    self.logger.info(
                        f"SIZE_TARGET_PRIORITY: strictly enforcing calculated bitrate budget {int(video_bitrate_kbps)}k "
                        f"to hit user file size perfectly."
                    )
                else:
                    if video_bitrate_kbps < 2500:
                         video_bitrate_kbps = 2500
                current_encoder = self.encoder_mgr.get_initial_encoder()
                if self.is_mobile_format and self.portrait_text:
                    size, lines = self.text_wrapper.fit_and_wrap(self.portrait_text)
                    txt_wrapped = "\n".join(lines)
                    final_text_content = apply_bidi_formatting(txt_wrapped)
                    textfile_path = os.path.join(temp_dir, f"drawtext-{uuid.uuid4()}.txt")
                    with open(textfile_path, "w", encoding="utf-8") as tf:
                        tf.write(final_text_content)
                    self._emit_status("Applying Canvas Trick with Text.")
                audio_speed_cmd = ""
                if not self.speed_segments and self.speed_factor != 1.0:
                    s = self.speed_factor
                    audio_speed_filters = []
                    temp_speed = s
                    while temp_speed < 0.5:
                        audio_speed_filters.append("atempo=0.5")
                        temp_speed /= 0.5
                    while temp_speed > 2.0:
                        audio_speed_filters.append("atempo=2.0")
                        temp_speed /= 2.0
                    audio_speed_filters.append(f"atempo={temp_speed:.4f}")
                    audio_speed_cmd = ",".join(audio_speed_filters)
                audio_chains = self.filter_builder.build_audio_chain(
                    music_config=self.music_config,
                    video_start_time=source_cut_start_ms / 1000.0,
                    video_end_time=source_cut_end_ms / 1000.0,
                    speed_factor=self.speed_factor,
                    disable_fades=self.disable_fades,
                    vfade_in_d=int(vfade_in_duration_ms / self.speed_factor) / 1000.0,
                    audio_filter_cmd=audio_speed_cmd,
                    time_mapper=time_mapper,
                    sample_rate=sample_rate
                )
                if self.speed_segments and granular_audio_label:
                    first_filter = audio_chains[0]
                    if first_filter.startswith("[0:a]"):
                         audio_chains[0] = first_filter.replace("[0:a]", granular_audio_label)
                core_path = os.path.join(temp_dir, f"core-{uuid.uuid4()}.mp4")
                successful_encoder = None

                def run_ffmpeg_command(encoder_name, use_cuda_filters=True):
                    attempt_is_nvidia = (encoder_name == 'h264_nvenc')
                    actual_use_cuda_filters = attempt_is_nvidia and use_cuda_filters
                    has_text = (self.portrait_text is not None)
                    needs_fade = not self.disable_fades
                    needs_hw_download = has_text or needs_fade
                    vcodec, rc_label = self.encoder_mgr.get_codec_flags(
                        encoder_name,
                        video_bitrate_kbps,
                        eff_dur_sec,
                        fps_expr=target_fps_expr
                    )
                    attempt_core_filters = []
                    v_label = "[0:v]"
                    fps_chain = f"{v_label}setpts=PTS-STARTPTS[v_stabilized]"
                    attempt_core_filters.append(fps_chain)
                    v_label = "[v_stabilized]"
                    if granular_filter_str:
                        attempt_core_filters.append(granular_filter_str)
                        v_label = granular_video_label
                    elif self.speed_factor != 1.0:
                        attempt_core_filters.append(f"{v_label}setpts=PTS/{self.speed_factor}[v_speeded]")
                        v_label = "[v_speeded]"
                    if self.is_mobile_format:
                        mobile_coords = self.config.get_mobile_coordinates(self.logger)
                        v_filter_cmd = self.filter_builder.build_mobile_filter(
                            mobile_coords, self.original_resolution, self.is_boss_hp, self.show_teammates_overlay,
                            use_nvidia=actual_use_cuda_filters,
                            needs_text_overlay=has_text,
                            use_hwaccel=(encoder_name in ('h264_nvenc', 'h264_amf', 'h264_qsv')),
                            needs_hw_download=needs_hw_download, 
                            target_fps=target_fps_value,
                            input_pad=v_label 
                        )
                        if has_text:
                            v_filter_cmd = self.filter_builder.add_drawtext_filter(
                                v_filter_cmd, textfile_path, size, self.config.line_spacing
                            )
                        attempt_core_filters.append(v_filter_cmd + "[v_layout]")
                        v_label = "[v_layout]"
                    else:
                        t_w = 1920
                        if not self.keep_highest_res:
                            if self.quality_level == 1: t_w = 1280
                            elif self.quality_level < 1: t_w = 960
                            elif video_bitrate_kbps < 800: t_w = 1280
                        if actual_use_cuda_filters:
                             res_filter = f"{v_label}hwupload_cuda,scale_cuda='min({t_w},iw)':-2[v_res]"
                        else:
                             res_filter = f"{v_label}scale='min({t_w},iw)':-2:flags=lanczos[v_res]"
                        attempt_core_filters.append(res_filter)
                        v_label = "[v_res]"
                        if needs_hw_download and actual_use_cuda_filters:
                             attempt_core_filters.append(f"{v_label}hwdownload,format=nv12[v_res_sw]")
                             v_label = "[v_res_sw]"
                    final_v_filters = [f"{v_label}trim=duration={self.duration_corrected_sec:.6f}"]
                    if not self.disable_fades:
                         vfade_in_sec = (vfade_in_duration_ms / self.speed_factor) / 1000.0
                         vfade_out_sec = (vfade_out_duration_ms / self.speed_factor) / 1000.0
                         vfade_out_start_sec = vfade_out_start_ms / 1000.0
                         if vfade_in_sec > 0:
                             final_v_filters.append(f"fade=t=in:st=0:d={vfade_in_sec:.4f}")
                         if vfade_out_sec > 0:
                             final_v_filters.append(f"fade=t=out:st={vfade_out_start_sec:.4f}:d={vfade_out_sec:.4f}")
                    final_v_filters.append("setsar=1")
                    if needs_hw_download and actual_use_cuda_filters:
                        final_v_filters.append("format=nv12")
                        final_v_filters.append("hwupload_cuda")
                    attempt_core_filters.append(f"{','.join(final_v_filters)}[vcore]")
                    attempt_core_filters.extend(audio_chains)
                    complex_filter_str = ';'.join(attempt_core_filters)
                    filter_script_path = os.path.join(temp_dir, f"filter_complex-{uuid.uuid4()}.txt")
                    with open(filter_script_path, "w", encoding="utf-8") as f_script:
                        f_script.write(complex_filter_str)
                    if 'filter_scripts' not in dir(self): self.filter_scripts = []
                    self.filter_scripts.append(filter_script_path)
                    self._emit_status(f"Processing video ({rc_label})...")
                    hw_flags = []
                    if encoder_name == 'h264_nvenc':
                        hw_flags = [
                            '-hwaccel', 'd3d11va',
                        ]
                    elif encoder_name in ('h264_amf', 'h264_qsv'):
                        hw_flags = [
                            '-hwaccel', 'd3d11va',
                            '-hwaccel_output_format', 'nv12'
                        ]
                    cmd = [ffmpeg_path, '-y', '-progress', 'pipe:1', '-fflags', '+genpts']
                    cmd.extend(hw_flags)
                    cmd.extend([
                        '-err_detect', 'ignore_err',
                        '-ss', f"{source_cut_start_ms / 1000.0:.3f}", 
                        '-i', os.path.normpath(self.input_path)
                    ])
                    if has_bg:
                        cmd.extend(['-i', os.path.normpath(self.bg_music_path)])
                    cmd.extend(vcodec)
                    cmd.extend([
                        '-r', str(target_fps_expr),
                        '-fps_mode', 'cfr',
                        '-video_track_timescale', str(video_track_timescale),
                        '-movflags', '+faststart',
                        '-max_muxing_queue_size', '2048'
                    ])
                    cmd.extend([
                        '-c:a', 'aac', 
                        '-b:a', f'{audio_kbps}k', 
                        '-ar', str(sample_rate), 
                        '-ac', '2'
                    ])
                    cmd.extend([
                        '-filter_complex_script', filter_script_path,
                        '-map', '[vcore]', 
                        '-map', '[acore]', 
                        '-shortest',
                        '-t', f"{self.duration_corrected_sec:.3f}"
                    ])
                    cmd.append(os.path.normpath(core_path))
                    self.logger.info(f"GEN_CMD: {' '.join(cmd)}")
                    self.logger.info(f"Filter script content length: {len(complex_filter_str)}")
                    self.logger.info(f"Attempting encode with '{encoder_name}' (CUDA Filters: {actual_use_cuda_filters})...")
                    self.current_process = create_subprocess(cmd, self.logger)
                    stderr_accumulator = []

                    def stderr_monitor(line):
                        stderr_accumulator.append(line)
                    monitor_ffmpeg_progress(
                        self.current_process, self.duration_corrected_sec, 
                        scaler_core,
                        self._monitor_disk_space,
                        self.logger,
                        on_error_line=stderr_monitor
                    )
                    self.current_process.wait()
                    full_stderr = "\n".join(stderr_accumulator)
                    low = full_stderr.lower()
                    is_filter_error = any(x in low for x in (
                        "error reinitializing filters",
                        "function not implemented",
                        "overlay_cuda",
                        "scale_cuda",
                        "hwupload_cuda",
                        "hwdownload",
                        "option not found",
                        "avfilter",
                    ))
                    is_corrupt_error = any(x in low for x in (
                        "corrupt input packet in stream",
                        "error while decoding",
                        "invalid data found when processing input",
                    ))
                    if is_corrupt_error:
                        self.logger.warning("Corrupt frame detected. FFmpeg will heal/stitch it automatically to preserve sync.")
                    if not (self.current_process.returncode == 0):
                        if is_filter_error:
                            self.logger.error(f"FILTER_FAIL_DETECTED. Filter script that failed:\n{complex_filter_str}")
                    return self.current_process.returncode == 0, is_filter_error
                success, filter_fail = run_ffmpeg_command(current_encoder, use_cuda_filters=True)
                if success:
                    successful_encoder = current_encoder
                if not success and filter_fail and current_encoder == 'h264_nvenc' and not self.is_canceled:
                    self.logger.warning("NVIDIA GPU Filters failed. Retrying NVENC with Software filters...")
                    success, _ = run_ffmpeg_command(current_encoder, use_cuda_filters=False)
                    if success:
                        successful_encoder = current_encoder
                if not self.is_canceled and not success:
                    self.logger.warning(f"Initial encoder '{current_encoder}' failed. Starting fallback process.")
                    strategy = getattr(self, "hardware_strategy", "CPU")
                    fallback_encoders = self.encoder_mgr.get_fallback_list(failed_encoder=current_encoder)
                    if strategy == "NVIDIA":
                        allowed = ["h264_nvenc", "libx264"]
                    elif strategy == "AMD": allowed = ["h264_amf", "libx264"]
                    elif strategy == "INTEL": allowed = ["h264_qsv", "libx264"]
                    else: allowed = ["libx264"]
                    filtered_fallbacks = [e for e in fallback_encoders if e in allowed]
                    for fallback_encoder in filtered_fallbacks:
                        if self.is_canceled: break
                        self._emit_status(f"Trying fallback: {fallback_encoder}...")
                        success, _ = run_ffmpeg_command(fallback_encoder, use_cuda_filters=False)
                        if success:
                            self.logger.info(f"Fallback to '{fallback_encoder}' succeeded.")
                            successful_encoder = fallback_encoder
                            break
                        else:
                            self.logger.warning(f"Fallback encoder '{fallback_encoder}' also failed.")
                if not success and not self.is_canceled:
                    self._emit_finished(False, "Main video rendering failed after all attempts.")
                    return
                if self.is_canceled: return
                if self.intro_still_sec > 0:
                    intro_time_ms = self.intro_abs_time_ms
                    if intro_time_ms is None and self.intro_from_midpoint:
                        intro_time_ms = (self.start_time_ms + self.end_time_ms) // 2
                    if intro_time_ms is not None:
                        intro_proc = IntroProcessor(ffmpeg_path, self.logger, self.encoder_mgr, temp_dir)
                        intro_path = intro_proc.create_intro(
                            self.input_path, intro_time_ms / 1000.0, self.intro_still_sec,
                            self.is_mobile_format, audio_kbps, video_bitrate_kbps,
                            scaler_intro,
                            lambda: self.is_canceled,
                            sample_rate=sample_rate,
                            fps_expr=target_fps_expr,
                            preferred_encoder=successful_encoder or current_encoder
                        )
                        self.current_process = intro_proc.current_process
                if self.is_canceled: return
                concat_proc = ConcatProcessor(ffmpeg_path, self.logger, self.base_dir, temp_dir)
                output_path = concat_proc.run_concat(
                    intro_path, core_path, scaler_concat,
                    video_bitrate_kbps=video_bitrate_kbps,
                    cancellation_check=lambda: self.is_canceled,
                    fps_expr=target_fps_expr,
                    preferred_encoder=successful_encoder or current_encoder,
                    force_reencode=bool(intro_path and os.path.exists(intro_path)),
                    audio_kbps=audio_kbps,
                    audio_sample_rate=sample_rate
                )
                self.current_process = concat_proc.current_process
                if output_path:
                    self._emit_progress(100)
                    self._emit_finished(True, output_path)
                else:
                    self._emit_finished(False, "Concat failed.")
            except Exception as e:
                if not self.is_canceled:
                    self.logger.exception(f"Job FAILURE: {e}")
                    self._emit_finished(False, f"Error: {e}")
            finally:
                if hasattr(self, 'temp_job_dir') and os.path.exists(self.temp_job_dir):
                    import shutil
                    try:
                        shutil.rmtree(self.temp_job_dir, ignore_errors=True)
                        self.logger.info(f"CLEANUP: Removed temporary job directory {self.temp_job_dir}")
                    except Exception as ce:
                        self.logger.error(f"CLEANUP ERROR: Failed to remove job temp folder: {ce}")
                if textfile_path and os.path.exists(textfile_path):
                    try: os.remove(textfile_path)
                    except: pass
                if self.is_canceled and output_path and os.path.exists(output_path):
                    try: os.remove(output_path)
                    except: pass