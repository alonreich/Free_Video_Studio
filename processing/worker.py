import os
import tempfile
import time
from PyQt5.QtCore import QThread
from .config_data import VideoConfig
from .system_utils import create_subprocess, monitor_ffmpeg_progress, kill_process_tree
from .text_ops import TextWrapper, fix_hebrew_text, apply_bidi_formatting
from .media_utils import MediaProber, calculate_video_bitrate
from .filter_builder import FilterBuilder
from .encoders import EncoderManager
from .step_intro import IntroProcessor
from .step_concat import ConcatProcessor

class ProcessThread(QThread):
    def __init__(self, input_path, start_time, end_time, original_resolution, is_mobile_format, speed_factor,
                script_dir, progress_update_signal, status_update_signal, finished_signal, logger,
                is_boss_hp=False, show_teammates_overlay=False, quality_level: int = 2,
                bg_music_path=None, bg_music_volume=None, bg_music_offset=0.0, original_total_duration=0.0,
                disable_fades=False, intro_still_sec: float = 0.0, intro_from_midpoint: bool = False, intro_abs_time: float = None,
                portrait_text: str = None):
        super().__init__()
        self.input_path = input_path
        self.start_time = float(start_time)
        self.end_time = float(end_time)
        self.duration = self.end_time - self.start_time
        self.original_resolution = original_resolution
        self.is_mobile_format = is_mobile_format
        self.speed_factor = float(speed_factor)
        self.script_dir = script_dir
        self.base_dir = os.path.abspath(os.path.join(self.script_dir, os.pardir))
        self.bin_dir = os.path.join(self.base_dir, 'binaries')
        self.progress_update_signal = progress_update_signal
        self.status_update_signal = status_update_signal
        self.finished_signal = finished_signal
        self.logger = logger
        self.bg_music_path = bg_music_path if (bg_music_path and os.path.isfile(bg_music_path)) else None
        self.bg_music_volume = float(bg_music_volume) if bg_music_volume is not None else None
        self.bg_music_offset = float(bg_music_offset or 0.0)
        self.portrait_text = portrait_text
        self.is_boss_hp = is_boss_hp
        self.show_teammates_overlay = bool(show_teammates_overlay)
        self.disable_fades = bool(disable_fades)
        self.intro_from_midpoint = bool(intro_from_midpoint)
        self.intro_still_sec = float(intro_still_sec or 0.0)
        self.intro_abs_time = float(intro_abs_time) if intro_abs_time is not None else None
        self.original_total_duration = float(original_total_duration or 0.0)
        self.config = VideoConfig(self.base_dir)
        self.keep_highest_res, self.target_mb, self.quality_level = self.config.get_quality_settings(quality_level)
        self.text_wrapper = TextWrapper(self.config)
        self.filter_builder = FilterBuilder(self.config, self.logger)
        self.encoder_mgr = EncoderManager(self.logger)
        self.prober = MediaProber(self.bin_dir, self.input_path)
        self.current_process = None
        self.is_canceled = False
        self.start_time_corrected = self.start_time / self.speed_factor if self.speed_factor != 1.0 else self.start_time
        user_duration = self.duration / self.speed_factor if self.speed_factor != 1.0 else self.duration
        self.duration_corrected = max(0.0, user_duration)

    def cancel(self):
        self.logger.info("Cancellation requested.")
        self.is_canceled = True
        if self.current_process:
            kill_process_tree(self.current_process.pid, self.logger)

    def run(self):
        temp_dir = tempfile.gettempdir()
        temp_log_path = os.path.join(temp_dir, f"ffmpeg2pass-{os.getpid()}-{int(time.time())}.log")
        core_path = None
        intro_path = None
        output_path = None
        ffmpeg_path = os.path.join(self.bin_dir, 'ffmpeg.exe')
        try:
            if self.is_canceled: return
            FADE_DUR = self.config.fade_duration
            EPS = self.config.epsilon
            in_ss = self.start_time
            in_t = self.duration
            vfade_in_d = 0.0
            vfade_out_d = 0.0
            vfade_out_st = 0.0
            if not self.disable_fades:
                if self.start_time < FADE_DUR - EPS:
                    in_ss = self.start_time
                    vfade_in_d = 0.0
                else:
                    in_ss = self.start_time - FADE_DUR
                    vfade_in_d = FADE_DUR
                if self.original_total_duration > 0 and (self.end_time > self.original_total_duration - FADE_DUR):
                    adj_end = self.original_total_duration
                    vfade_out_d = 0.0
                else:
                    adj_end = self.end_time + FADE_DUR
                    vfade_out_d = FADE_DUR
                in_t = max(0.0, adj_end - in_ss)
                output_clip_dur_pre_speed = in_t
                if vfade_out_d > 0:
                    vfade_out_st = max(0.0, output_clip_dur_pre_speed - vfade_out_d)
                else:
                    vfade_out_st = output_clip_dur_pre_speed
            output_clip_dur_pre_speed = in_t
            if self.speed_factor != 1.0:
                in_t_speed_adjusted = in_t / self.speed_factor
                vfade_in_d /= self.speed_factor
                vfade_out_d /= self.speed_factor
                vfade_out_st /= self.speed_factor
            else:
                in_t_speed_adjusted = in_t
            self.duration_corrected = max(0.0, in_t_speed_adjusted)
            if self.is_mobile_format and self.portrait_text:
                fix_hebrew_text(self.portrait_text)
            audio_kbps = 256
            probed_kbps = self.prober.get_audio_bitrate()
            if probed_kbps:
                audio_kbps = probed_kbps
                self.status_update_signal.emit(f"Audio bitrate: preserving source ~{audio_kbps} kbps.")
            intro_len_size = max(0.0, self.intro_still_sec) if self.intro_still_sec > 0 else 0.0
            eff_dur = self.duration_corrected + intro_len_size
            video_bitrate_kbps = calculate_video_bitrate(
                self.input_path, eff_dur, audio_kbps, self.target_mb, self.keep_highest_res
            )
            if video_bitrate_kbps is None:
                if not self.keep_highest_res:
                    self.finished_signal.emit(False, "Video duration is too short for target size.")
                    return
                video_bitrate_kbps = 2500
            video_filter_cmd = ""
            if self.is_mobile_format:
                video_filter_cmd = self.filter_builder.build_mobile_filter(
                    self.original_resolution, self.is_boss_hp, self.show_teammates_overlay
                )
                if self.portrait_text:
                    size, lines = self.text_wrapper.fit_and_wrap(self.portrait_text)
                    txt_wrapped = "\n".join(lines)
                    final_text_content = apply_bidi_formatting(txt_wrapped)
                    textfile_path = os.path.join(temp_dir, f"drawtext-{os.getpid()}-{int(time.time())}.txt")
                    with open(textfile_path, "w", encoding="utf-8") as tf:
                        tf.write(final_text_content)
                    video_filter_cmd = self.filter_builder.add_drawtext_filter(video_filter_cmd, textfile_path, size)
                    self.status_update_signal.emit("Applying Canvas Trick with Text.")
            else:
                if self.keep_highest_res:
                     target_res = "scale=iw:ih"
                else:
                    if self.quality_level >= 2:
                        target_res = "scale='min(1920,iw)':-2"
                        if video_bitrate_kbps < 800: target_res = "scale='min(1280,iw)':-2"
                    elif self.quality_level == 1:
                        target_res = "scale='min(1280,iw)':-2"
                    else:
                        target_res = "scale='min(960,iw)':-2"
                video_filter_cmd = f"fps=60,{target_res}"
            if self.speed_factor != 1.0:
                video_filter_cmd += f",setpts=PTS/{self.speed_factor}"
            audio_speed_cmd = ""
            if self.speed_factor != 1.0:
                s = self.speed_factor
                if 0.5 <= s <= 2.0:
                    audio_speed_cmd = f"atempo={s:.3f}"
                else:
                    audio_speed_cmd = f"rubberband=tempo={s:.3f}:pitch=1:formant=1"
            has_bg = bool(self.bg_music_path)
            audio_chains = self.filter_builder.build_audio_chain(
                has_bg, self.bg_music_volume, self.bg_music_offset, 
                self.duration_corrected, self.disable_fades, audio_speed_cmd,
                vfade_in_d, vfade_out_d, vfade_out_st
            )
            core_filters = []
            vcore_str = f"[0:v]{video_filter_cmd}," if video_filter_cmd else "[0:v]"
            if not self.disable_fades:
                 vcore_str += f"fade=t=in:st=0:d={vfade_in_d:.3f},fade=t=out:st={vfade_out_st:.3f}:d={vfade_out_d:.3f},"
            core_filters.append(
                f"{vcore_str}format=yuv420p,trim=duration={self.duration_corrected:.6f},setpts=PTS-STARTPTS,setsar=1,fps=60[vcore]"
            )
            core_filters.extend(audio_chains)
            core_path = os.path.join(temp_dir, f"core-{os.getpid()}-{int(time.time())}.mp4")
            vcodec, rc_label = self.encoder_mgr.get_core_codec_flags(video_bitrate_kbps, eff_dur)
            self.status_update_signal.emit(f"Processing video ({rc_label}).")
            cmd = [
                ffmpeg_path, '-y', '-hwaccel', 'auto',
                '-progress', 'pipe:1',
                '-ss', f"{in_ss:.3f}", '-t', f"{in_t:.3f}",
                '-i', self.input_path,
            ]
            if has_bg:
                cmd += ['-i', self.bg_music_path]
            cmd += vcodec + [
                '-pix_fmt', 'yuv420p', '-movflags', '+faststart',
                '-c:a', 'aac', '-b:a', f'{audio_kbps}k', '-ar', '48000',
                '-filter_complex', ';'.join(core_filters),
                '-map', '[vcore]', '-map', '[acore]', '-shortest',
                core_path
            ]
            self.logger.info(f"STEP 1/3 CORE")
            self.current_process = create_subprocess(cmd, self.logger)
            monitor_ffmpeg_progress(
                self.current_process, self.duration_corrected, 
                self.progress_update_signal, lambda: self.is_canceled, self.logger
            )
            self.current_process.wait()
            if self.is_canceled: return
            if self.current_process.returncode != 0:
                self.logger.warning("Hardware failed. Retrying with fallback...")
                success = False
                for enc in self.encoder_mgr.get_fallback_list():
                    self.status_update_signal.emit(f"Hardware failure. Trying {enc}...")
                    cmd_retry = [ffmpeg_path, '-y', '-ss', f"{in_ss:.3f}", '-t', f"{in_t:.3f}", '-i', self.input_path]
                    if has_bg:
                        cmd_retry += ['-ss', f"{self.bg_music_offset:.3f}", '-i', self.bg_music_path]
                    if enc == "libx264":
                        cmd_retry += ['-c:v', 'libx264', '-preset', 'medium', '-crf', '23']
                    else:
                        cmd_retry += ['-c:v', enc, '-b:v', f'{video_bitrate_kbps}k']
                    cmd_retry += [
                        '-pix_fmt', 'yuv420p', '-movflags', '+faststart',
                        '-c:a', 'aac', '-b:a', f'{audio_kbps}k', '-ar', '48000',
                        '-filter_complex', ';'.join(core_filters),
                        '-map', '[vcore]', '-map', '[acore]', '-shortest', core_path
                    ]
                    retry_proc = create_subprocess(cmd_retry, self.logger)
                    retry_proc.wait()
                    if retry_proc.returncode == 0:
                        success = True
                        break
                if not success:
                    self.finished_signal.emit(False, "All encoders failed.")
                    return
            if self.intro_still_sec > 0:
                if self.intro_abs_time is None and self.intro_from_midpoint:
                     mid = (self.start_time + self.end_time) / 2.0
                     self.intro_abs_time = float(mid)
                if self.intro_abs_time is not None:
                    intro_proc = IntroProcessor(ffmpeg_path, self.logger, self.encoder_mgr, temp_dir)
                    intro_path = intro_proc.create_intro(
                        self.input_path, self.intro_abs_time, self.intro_still_sec,
                        self.is_mobile_format, audio_kbps, video_bitrate_kbps,
                        self.progress_update_signal, lambda: self.is_canceled
                    )
                    self.current_process = intro_proc.current_process
            concat_proc = ConcatProcessor(ffmpeg_path, self.logger, self.base_dir, temp_dir)
            output_path = concat_proc.run_concat(intro_path, core_path, self.progress_update_signal)
            self.current_process = concat_proc.current_process
            if output_path:
                self.finished_signal.emit(True, output_path)
            else:
                self.finished_signal.emit(False, "Concat failed.")
        except Exception as e:
            if not self.is_canceled:
                self.logger.exception(f"Job FAILURE: {e}")
                self.finished_signal.emit(False, f"Error: {e}")
        finally:
            if self.is_canceled and output_path and os.path.exists(output_path):
                try: os.remove(output_path)
                except: pass
            for p in [core_path, intro_path]:
                if p and os.path.exists(p):
                    try: os.remove(p)
                    except: pass