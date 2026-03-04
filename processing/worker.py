import os
import tempfile
import uuid
import shutil
import subprocess
import time
from typing import Tuple, Dict, Any, Optional, List
from PyQt5.QtCore import QThread, pyqtSignal
from .processing_models import ProcessingJob, ProcessingResult
from .system_utils import create_subprocess, kill_process_tree, check_disk_space
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
                 is_mobile_format, speed_factor, base_dir,
                 progress_signal, status_signal, finished_signal,
                 logger=None, is_boss_hp=False, show_teammates_overlay=False,
                 quality_level=2, bg_music_path=None, bg_music_volume=0.8,
                 bg_music_offset_ms=0, original_total_duration_ms=0,
                 disable_fades=False, intro_still_sec=0,
                 intro_from_midpoint=False, intro_abs_time_ms=None,
                 portrait_text=None, music_config=None, speed_segments=None,
                 hardware_strategy='CPU', music_tracks=None):
        super().__init__()
        self.input_path = input_path
        self.start_time_ms = start_time_ms
        self.end_time_ms = end_time_ms
        self.original_resolution = original_resolution
        self.is_mobile_format = is_mobile_format
        self.speed_factor = speed_factor
        self.base_dir = base_dir
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
        self.speed_segments = speed_segments
        self.hardware_strategy = hardware_strategy
        self.music_config = music_config or {}
        self.music_tracks = music_tracks or [] # [FIX]
        self.config = VideoConfig(self.base_dir)
        self.keep_highest_res, self.target_mb, self.quality_level = self.config.get_quality_settings(quality_level)
        self.filter_builder = FilterBuilder(self.logger)
        self.encoder_mgr = EncoderManager(self.logger, hardware_strategy=self.hardware_strategy)
        self.prober = MediaProber(os.path.join(self.base_dir, 'binaries'), self.input_path)
        self.current_process = None
        self.is_canceled = False
        self.duration_corrected_sec = (self.end_time_ms - self.start_time_ms) / 1000.0 / self.speed_factor
        self._output_dir = os.path.join(self.base_dir, "!!!_Output_Video_Files_!!!")

    def cancel(self):
        self.is_canceled = True
        if self.current_process: kill_process_tree(self.current_process.pid, self.logger)

    def _monitor_disk_space(self):
        if not check_disk_space(self._output_dir, 0.2):
            self.cancel(); return True
        return self.is_canceled

    def _emit_status(self, msg):
        if hasattr(self.status_update_signal, "emit"): self.status_update_signal.emit(msg)

    def run(self):
        try:
            self.job_id = str(uuid.uuid4())[:8]
            self.temp_job_dir = os.path.join(tempfile.gettempdir(), f"fvs_job_{self.job_id}")
            os.makedirs(self.temp_job_dir, exist_ok=True)
            scaler_core = ProgressScaler(self.progress_update_signal, 0, 50)
            source_audio_kbps = self.prober.get_audio_bitrate() or 192
            audio_kbps = max(192, int(source_audio_kbps))
            target_fps_expr = self.prober.get_video_fps_expr()
            video_bitrate_kbps = calculate_video_bitrate(
                self.input_path, self.duration_corrected_sec, audio_kbps, 
                self.target_mb, self.keep_highest_res, self.logger, 
                self.original_resolution, target_fps_expr, self.quality_level,
                prober=self.prober
            )
            text_png_path = None
            if self.portrait_text:
                text_png_path = os.path.join(self.temp_job_dir, "portrait_text.png")
                tw, th = (1080, 1920) if self.is_mobile_format else (1920, 1080)
                generate_text_overlay_png(self.portrait_text, tw, th, self.config.base_font_size, self.config.line_spacing, text_png_path, self.config, self.logger)
            
            music_cfg = self.music_config if hasattr(self, 'music_config') else {}
            if self.bg_music_path and not self.music_tracks:
                 self.music_tracks = [(self.bg_music_path, self.bg_music_offset_ms/1000.0, self.duration_corrected_sec)]
            
            audio_chains, final_a_label = self.filter_builder.build_audio_chain(
                music_config=music_cfg,
                video_start_time=self.start_time_ms/1000.0, video_end_time=self.end_time_ms/1000.0,
                speed_factor=self.speed_factor, disable_fades=self.disable_fades,
                vfade_in_d=0.5 if not self.disable_fades else 0, audio_filter_cmd="", sample_rate=48000,
                music_tracks=self.music_tracks # [FIX]
            )
            core_path = os.path.normpath(os.path.join(self.temp_job_dir, "core.mp4"))

            def run_ffmpeg(use_cuda):
                vcodec, rc_label = self.encoder_mgr.get_codec_flags('h264_nvenc' if use_cuda else 'libx264', video_bitrate_kbps, self.duration_corrected_sec, target_fps_expr)
                v_label = "[0:v]"
                attempt_core_filters = []
                working_duration_sec = self.duration_corrected_sec
                t_map = None
                if self.speed_segments:
                    g_str, g_v, g_a, g_dur, t_map = self.filter_builder.build_granular_speed_chain(
                        self.input_path, 
                        (self.end_time_ms - self.start_time_ms),
                        self.speed_segments,
                        self.speed_factor,
                        source_cut_start_ms=self.start_time_ms,
                        input_v_label=v_label,
                        input_a_label="[0:a]",
                        target_fps=target_fps_expr
                    )
                    attempt_core_filters.append(g_str)
                    v_stabilized_pad = g_v
                    a_prepared_pad = g_a
                    working_duration_sec = g_dur
                else:
                    sync_chain = f"{v_label}fps={target_fps_expr},setpts=(PTS-STARTPTS)/{self.speed_factor}[v_stabilized]"
                    v_stabilized_pad = "[v_stabilized]"
                    a_prepared_pad = "[a_prepared_base]"
                    audio_speed_filters = []
                    tmp_s = self.speed_factor
                    while tmp_s < 0.5: audio_speed_filters.append("atempo=0.5"); tmp_s /= 0.5
                    while tmp_s > 2.0: audio_speed_filters.append("atempo=2.0"); tmp_s /= 2.0
                    audio_speed_filters.append(f"atempo={tmp_s:.4f}")
                    a_sync = f"[0:a]asetpts=PTS-STARTPTS,{','.join(audio_speed_filters)},aresample=48000:async=1[a_prepared_base]"
                    attempt_core_filters.append(sync_chain)
                    attempt_core_filters.append(a_sync)
                
                ffmpeg_inputs = ['-ss', f"{self.start_time_ms/1000.0:.3f}", '-i', self.input_path]
                
                # [FIX] Multiple background music inputs
                for track_path, _, _ in self.music_tracks:
                    ffmpeg_inputs += ['-i', track_path]
                
                txt_input_pad = None
                if text_png_path:
                    txt_idx = 1 + len(self.music_tracks)
                    ffmpeg_inputs += ['-i', text_png_path]
                    txt_input_pad = f"[{txt_idx}:v]"
                
                if self.is_mobile_format:
                    coords = self.config.get_mobile_coordinates(self.logger)
                    v_mobile, v_mobile_out = self.filter_builder.build_mobile_filter_chain(
                        v_stabilized_pad, coords, self.is_boss_hp, self.show_teammates_overlay, txt_input_pad
                    )
                    attempt_core_filters.append(v_mobile)
                    v_final_pad = v_mobile_out
                else:
                    v_final_pad = v_stabilized_pad
                
                attempt_core_filters.extend(audio_chains)
                
                full_filter_str = ";".join(attempt_core_filters)
                ffmpeg_cmd = [os.path.join(self.base_dir, 'binaries', 'ffmpeg.exe'), '-y', '-hide_banner'] + ffmpeg_inputs + [
                    '-filter_complex', full_filter_str,
                    '-map', v_final_pad, '-map', final_a_label,
                    '-c:v', vcodec] + rc_label + [
                    '-c:a', 'aac', '-b:a', f"{audio_kbps}k",
                    '-t', f"{working_duration_sec:.3f}",
                    core_path
                ]
                self.logger.info(f"FFMPEG CMD: {' '.join(ffmpeg_cmd)}")
                self.current_process = create_subprocess(ffmpeg_cmd)
                while self.current_process.poll() is None:
                    if self._monitor_disk_space(): return False
                    time.sleep(0.5)
                return self.current_process.returncode == 0

            self._emit_status("Encoding core video...")
            success = run_ffmpeg(use_cuda=(self.hardware_strategy != 'CPU'))
            if not success:
                self.finished_signal.emit(False, "FFmpeg core encoding failed.")
                return

            shutil.move(core_path, self.input_path + ".output.mp4")
            self.progress_update_signal.emit(100)
            self.finished_signal.emit(True, self.input_path + ".output.mp4")
        except Exception as e:
            if self.logger: self.logger.exception(f"FATAL: {e}")
            self.finished_signal.emit(False, str(e))
        finally:
            if hasattr(self, 'temp_job_dir'): shutil.rmtree(self.temp_job_dir, ignore_errors=True)
