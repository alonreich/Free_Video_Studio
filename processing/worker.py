import os
import tempfile
import time
from PyQt5.QtCore import QThread
from .config_data import VideoConfig
from .system_utils import create_subprocess, monitor_ffmpeg_progress, kill_process_tree, check_disk_space
from .text_ops import TextWrapper, fix_hebrew_text, apply_bidi_formatting
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
        self.real_signal.emit(min(100, weighted_val))

class ProcessThread(QThread):
    def __init__(self, input_path, start_time_ms, end_time_ms, original_resolution, is_mobile_format, speed_factor,
                 script_dir, progress_update_signal, status_update_signal, finished_signal, logger,
                 is_boss_hp=False, show_teammates_overlay=False, quality_level: int = 2,
                 bg_music_path=None, bg_music_volume=None, bg_music_offset_ms=0, original_total_duration_ms=0,
                 disable_fades=False, intro_still_sec: float = 0.0, intro_from_midpoint: bool = False, intro_abs_time_ms: int = None,
                 portrait_text: str = None, music_config=None, speed_segments=None):
        super().__init__()
        self.music_config = music_config if music_config else {}
        self.input_path = input_path
        self.start_time_ms = int(start_time_ms)
        self.end_time_ms = int(end_time_ms)
        self.duration_ms = self.end_time_ms - self.start_time_ms
        self.original_resolution = original_resolution
        self.is_mobile_format = is_mobile_format
        self.speed_factor = float(speed_factor)
        self.speed_segments = speed_segments if speed_segments else []
        self.script_dir = script_dir
        self.base_dir = os.path.abspath(os.path.join(self.script_dir, os.pardir))
        self.bin_dir = os.path.join(self.base_dir, 'binaries')
        self.progress_update_signal = progress_update_signal
        self.status_update_signal = status_update_signal
        self.finished_signal = finished_signal
        self.logger = logger
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
        self.encoder_mgr = EncoderManager(self.logger)
        self.prober = MediaProber(self.bin_dir, self.input_path)
        self.current_process = None
        self.is_canceled = False
        self.duration_corrected_sec = (self.duration_ms / self.speed_factor / 1000.0) if self.speed_factor != 1.0 else (self.duration_ms / 1000.0)

    def cancel(self):
        self.logger.info("Cancellation requested.")
        self.is_canceled = True
        if self.current_process:
            kill_process_tree(self.current_process.pid, self.logger)

    def run(self):
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
        temp_dir = tempfile.gettempdir()
        core_path = None
        intro_path = None
        output_path = None
        ffmpeg_path = os.path.join(self.bin_dir, 'ffmpeg.exe')
        scaler_core = ProgressScaler(self.progress_update_signal, 0, 80)
        scaler_intro = ProgressScaler(self.progress_update_signal, 80, 10)
        scaler_concat = ProgressScaler(self.progress_update_signal, 90, 10)
        try:
            if self.is_canceled: return
            estimated_output_gb = (self.target_mb if self.target_mb else 500) / 1024.0
            required_space_gb = (estimated_output_gb * 2.0) + 1.0 
            out_dir = os.path.join(self.base_dir, "!!!_Output_Video_Files_!!!")
            if not check_disk_space(out_dir, required_space_gb):
                self.finished_signal.emit(False, f"Insufficient disk space. Need at least {required_space_gb:.1f} GB free.")
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
            audio_kbps = 256
            probed_kbps = self.prober.get_audio_bitrate()
            if probed_kbps:
                audio_kbps = probed_kbps
                self.status_update_signal.emit(f"Audio bitrate: preserving source ~{audio_kbps} kbps.")
            sample_rate = self.prober.get_sample_rate()
            if has_bg:
                sample_rate = 48000
            intro_len_sec = max(0.0, self.intro_still_sec) if self.intro_still_sec > 0 else 0.0
            eff_dur_sec = self.duration_corrected_sec + intro_len_sec
            video_bitrate_kbps = calculate_video_bitrate(
                self.input_path, eff_dur_sec, audio_kbps, self.target_mb, self.keep_highest_res
            )
            if video_bitrate_kbps is None:
                if not self.keep_highest_res:
                    self.finished_signal.emit(False, "Video duration is too short for target size.")
                    return
                video_bitrate_kbps = 2500
            MAX_SAFE_BITRATE = 35000
            if self.quality_level >= 4:
                MAX_SAFE_BITRATE = 200000
            if video_bitrate_kbps > MAX_SAFE_BITRATE:
                self.logger.info(f"Clamping bitrate from {video_bitrate_kbps}k to {MAX_SAFE_BITRATE}k for stability.")
                video_bitrate_kbps = MAX_SAFE_BITRATE
            current_encoder = self.encoder_mgr.get_initial_encoder()
            textfile_path = None
            size = None
            if self.is_mobile_format and self.portrait_text:
                size, lines = self.text_wrapper.fit_and_wrap(self.portrait_text)
                txt_wrapped = "\n".join(lines)
                final_text_content = apply_bidi_formatting(txt_wrapped)
                textfile_path = os.path.join(temp_dir, f"drawtext-{os.getpid()}-{int(time.time())}.txt")
                with open(textfile_path, "w", encoding="utf-8") as tf:
                    tf.write(final_text_content)
                self.status_update_signal.emit("Applying Canvas Trick with Text.")
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
            core_path = os.path.join(temp_dir, f"core-{os.getpid()}-{int(time.time())}.mp4")

            def run_ffmpeg_command(encoder_name):
                attempt_is_nvidia = (encoder_name == 'h264_nvenc')
                vcodec, rc_label = self.encoder_mgr.get_codec_flags(encoder_name, video_bitrate_kbps, eff_dur_sec)
                if self.is_mobile_format:
                    mobile_coords = self.config.get_mobile_coordinates(self.logger)
                    v_filter_cmd = self.filter_builder.build_mobile_filter(
                        mobile_coords, self.original_resolution, self.is_boss_hp, self.show_teammates_overlay
                    )
                    if self.portrait_text:
                        v_filter_cmd = self.filter_builder.add_drawtext_filter(
                            v_filter_cmd, textfile_path, size, self.config.line_spacing
                        )
                else:
                    t_w = 1920
                    if not self.keep_highest_res:
                        if self.quality_level == 1: t_w = 1280
                        elif self.quality_level < 1: t_w = 960
                        elif video_bitrate_kbps < 800: t_w = 1280
                    if self.keep_highest_res:
                        target_res = "scale=iw:ih:flags=bilinear"
                    else:
                        target_res = f"scale='min({t_w},iw)':-2:flags=bilinear"
                    v_filter_cmd = f"fps=60,{target_res}"
                if not self.speed_segments:
                    v_filter_cmd += f",setpts=PTS/{self.speed_factor}"
                attempt_core_filters = []
                if granular_filter_str:
                    attempt_core_filters.append(granular_filter_str)
                vcore_str = f"{granular_video_label}{v_filter_cmd},"
                if not self.disable_fades:
                     vfade_in_sec = (vfade_in_duration_ms / self.speed_factor) / 1000.0
                     vfade_out_sec = (vfade_out_duration_ms / self.speed_factor) / 1000.0
                     vfade_out_start_sec = vfade_out_start_ms / 1000.0
                     if vfade_in_sec > 0:
                         vcore_str += f"fade=t=in:st=0:d={vfade_in_sec:.4f},"
                     if vfade_out_sec > 0:
                         vcore_str += f"fade=t=out:st={vfade_out_start_sec:.4f}:d={vfade_out_sec:.4f},"
                p_fmt = "yuv420p"
                attempt_core_filters.append(
                    f"{vcore_str}format={p_fmt},trim=duration={self.duration_corrected_sec:.6f},setpts=PTS-STARTPTS,setsar=1[vcore]"
                )
                attempt_core_filters.extend(audio_chains)
                self.status_update_signal.emit(f"Processing video ({rc_label})...")
                hw_flags = []
                cmd = [
                    ffmpeg_path, '-y'] + hw_flags + [
                    '-progress', 'pipe:1',
                    '-ss', f"{source_cut_start_ms / 1000.0:.3f}", 
                    '-t', f"{source_cut_duration_ms / 1000.0:.3f}",
                    '-i', self.input_path,
                ]
                if has_bg:
                    cmd.extend(['-i', self.bg_music_path])
                cmd.extend(vcodec)
                complex_filter_str = ';'.join(attempt_core_filters)
                filter_script_path = os.path.join(temp_dir, f"filter_complex-{os.getpid()}-{int(time.time())}.txt")
                with open(filter_script_path, "w", encoding="utf-8") as f_script:
                    f_script.write(complex_filter_str)
                if 'filter_scripts' not in dir(self): self.filter_scripts = []
                self.filter_scripts.append(filter_script_path)
                cmd.extend([
                    '-movflags', '+faststart',
                    '-c:a', 'aac', '-b:a', f'{audio_kbps}k', '-ar', '48000', '-ac', '2',
                    '-filter_complex_script', filter_script_path,
                    '-map', '[vcore]', '-map', '[acore]', '-shortest',
                    core_path
                ])
                self.logger.info(f"Full FFmpeg command: {' '.join(cmd)}")
                self.logger.info(f"Filter script content length: {len(complex_filter_str)}")
                self.logger.info(f"Attempting encode with '{encoder_name}'...")
                self.current_process = create_subprocess(cmd, self.logger)
                monitor_ffmpeg_progress(
                    self.current_process, self.duration_corrected_sec, 
                    scaler_core,
                    lambda: self.is_canceled, self.logger
                )
                try:
                    self.current_process.wait(timeout=5)
                except Exception:
                    self.logger.error("FFmpeg process timed out. Force killing.")
                    kill_process_tree(self.current_process.pid, self.logger)
                return self.current_process.returncode == 0
            success = run_ffmpeg_command(current_encoder)
            if not self.is_canceled and not success:
                self.logger.warning(f"Initial encoder '{current_encoder}' failed. Starting fallback process.")
                fallback_encoders = self.encoder_mgr.get_fallback_list(failed_encoder=current_encoder)
                for fallback_encoder in fallback_encoders:
                    if self.is_canceled: break
                    self.status_update_signal.emit(f"Trying fallback: {fallback_encoder}...")
                    success = run_ffmpeg_command(fallback_encoder)
                    if success:
                        self.logger.info(f"Fallback to '{fallback_encoder}' succeeded.")
                        break
                    else:
                        self.logger.warning(f"Fallback encoder '{fallback_encoder}' also failed.")
            if self.is_canceled: return
            if not success:
                self.finished_signal.emit(False, "All available encoders failed.")
                return
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
                        sample_rate=sample_rate
                    )
                    self.current_process = intro_proc.current_process
            concat_proc = ConcatProcessor(ffmpeg_path, self.logger, self.base_dir, temp_dir)
            output_path = concat_proc.run_concat(
                intro_path, core_path, scaler_concat,
                cancellation_check=lambda: self.is_canceled
            )
            self.current_process = concat_proc.current_process
            if output_path:
                self.progress_update_signal.emit(100)
                self.finished_signal.emit(True, output_path)
            else:
                self.finished_signal.emit(False, "Concat failed.")
        except Exception as e:
            if not self.is_canceled:
                self.logger.exception(f"Job FAILURE: {e}")
                self.finished_signal.emit(False, f"Error: {e}")
        finally:
            if hasattr(self, 'filter_scripts'):
                for fs in self.filter_scripts:
                    if fs and os.path.exists(fs):
                        try: os.remove(fs)
                        except: pass
            if textfile_path and os.path.exists(textfile_path):
                try: os.remove(textfile_path)
                except: pass
            if self.is_canceled and output_path and os.path.exists(output_path):
                try: os.remove(output_path)
                except: pass
            for p in [core_path, intro_path]:
                if p and os.path.exists(p):
                    try: os.remove(p)
                    except: pass