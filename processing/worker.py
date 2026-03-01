import os
import tempfile
import uuid
import shutil
from typing import Tuple, Dict, Any, Optional, List
from PyQt5.QtCore import QThread
from .config_data import VideoConfig
from .system_utils import create_subprocess, monitor_ffmpeg_progress, kill_process_tree, check_disk_space, check_filter_option
from .text_ops import TextWrapper, apply_bidi_formatting
from .media_utils import MediaProber, calculate_video_bitrate
from .filter_builder import FilterBuilder
from .encoders import EncoderManager
from .step_intro import IntroProcessor
from .step_concat import ConcatProcessor
from .processing_utils import ProgressScaler, generate_text_overlay_png

class ProcessThread(QThread):
    def __init__(self, input_path, start_time_ms, end_time_ms, original_resolution, is_mobile_format, speed_factor,
                 script_dir, progress_update_signal, status_update_signal, finished_signal, logger,
                 is_boss_hp=False, show_teammates_overlay=False, quality_level: int = 2,
                 bg_music_path=None, bg_music_volume=None, bg_music_offset_ms=0, original_total_duration_ms=0,
                 disable_fades=False, intro_still_sec: float = 0.0, intro_from_midpoint: bool = False, intro_abs_time_ms: int = None,
                 portrait_text: str = None, music_config=None, speed_segments=None, hardware_strategy: str = "CPU"):
        super().__init__()
        self.input_path = input_path
        self.start_time_ms = int(start_time_ms)
        self.end_time_ms = int(end_time_ms)
        self.original_resolution = original_resolution
        self.is_mobile_format = is_mobile_format
        self.speed_factor = float(speed_factor)
        self.logger = logger
        self.speed_segments = speed_segments or []
        self.script_dir = script_dir
        self.base_dir = os.path.abspath(os.path.join(self.script_dir, os.pardir))
        self.bin_dir = os.path.join(self.base_dir, 'binaries')
        self.progress_update_signal = progress_update_signal
        self.status_update_signal = status_update_signal
        self.finished_signal = finished_signal
        self.bg_music_path = bg_music_path if (bg_music_path and os.path.isfile(bg_music_path)) else None
        self.bg_music_volume = float(bg_music_volume) if bg_music_volume is not None else 0.0
        self.bg_music_offset_ms = int(bg_music_offset_ms or 0)
        self.portrait_text = portrait_text
        self.is_boss_hp = is_boss_hp
        self.show_teammates_overlay = bool(show_teammates_overlay)
        self.disable_fades = bool(disable_fades)
        self.intro_still_sec = float(intro_still_sec or 0.0)
        self.intro_abs_time_ms = intro_abs_time_ms
        self.intro_from_midpoint = intro_from_midpoint
        self.original_total_duration_ms = int(original_total_duration_ms or 0)
        self.hardware_strategy = hardware_strategy
        self.music_config = music_config or {}
        self.config = VideoConfig(self.base_dir)
        self.keep_highest_res, self.target_mb, self.quality_level = self.config.get_quality_settings(quality_level)
        self.filter_builder = FilterBuilder(self.logger)
        self.encoder_mgr = EncoderManager(self.logger, hardware_strategy=self.hardware_strategy)
        self.prober = MediaProber(self.bin_dir, self.input_path)
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
            scaler_core = ProgressScaler(self.progress_update_signal, 0, 80)
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
            if self.bg_music_path:
                if not music_cfg.get('path'): music_cfg['path'] = self.bg_music_path
                if 'volume' not in music_cfg: music_cfg['volume'] = self.bg_music_volume
                if 'file_offset_sec' not in music_cfg: music_cfg['file_offset_sec'] = self.bg_music_offset_ms / 1000.0
            audio_chains = self.filter_builder.build_audio_chain(
                music_config=music_cfg,
                video_start_time=self.start_time_ms/1000.0, video_end_time=self.end_time_ms/1000.0,
                speed_factor=self.speed_factor, disable_fades=self.disable_fades,
                vfade_in_d=0.5 if not self.disable_fades else 0, audio_filter_cmd="", sample_rate=48000
            )
            core_path = os.path.normpath(os.path.join(self.temp_job_dir, "core.mp4"))

            def run_ffmpeg(use_cuda):
                vcodec, rc_label = self.encoder_mgr.get_codec_flags('h264_nvenc' if use_cuda else 'libx264', video_bitrate_kbps, self.duration_corrected_sec, target_fps_expr)
                v_label = "[0:v]"
                attempt_core_filters = []
                if self.speed_segments:
                    g_str, g_v, g_a, g_dur, t_map = self.filter_builder.build_granular_speed_chain(
                        self.input_path, 
                        (self.end_time_ms - self.start_time_ms),
                        self.speed_segments,
                        self.speed_factor,
                        source_cut_start_ms=self.start_time_ms,
                        input_v_label=f"{v_label}fps={target_fps_expr},",
                        input_a_label="[0:a]",
                        target_fps=target_fps_expr
                    )
                    granular_filter_str = g_str
                    granular_video_label = g_v
                    granular_audio_label = g_a
                    time_mapper = t_map
                    v_stabilized_pad = granular_video_label
                    a_prepared_pad = granular_audio_label
                    attempt_core_filters.append(granular_filter_str)
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
                if self.bg_music_path: ffmpeg_inputs += ['-i', self.bg_music_path]
                txt_input_pad = None
                if text_png_path:
                    txt_idx = 2 if self.bg_music_path else 1
                    ffmpeg_inputs += ['-i', text_png_path]
                    txt_input_pad = f"[{txt_idx}:v]"
                if self.is_mobile_format:
                    v_filter, out_v_pad = self.filter_builder.build_mobile_filter(
                        self.config.get_mobile_coordinates(self.logger), self.original_resolution, self.is_boss_hp, self.show_teammates_overlay,
                        use_nvidia=use_cuda, target_fps=target_fps_expr, input_pad=v_stabilized_pad, txt_input_label=txt_input_pad
                    )
                    v_finalize = v_filter
                else:
                    target_w, target_h = 1920, 1080
                    if txt_input_pad:
                        v_finalize = f"{v_stabilized_pad}scale={target_w}:{target_h}:force_original_aspect_ratio=decrease,pad={target_w}:{target_h}:(ow-iw)/2:(oh-ih)/2,format=nv12,setsar=1[v_scaled_sw];[v_scaled_sw]{txt_input_pad}overlay=x=0:y=0:eof_action=repeat[v_out_sw]"
                    else:
                        v_finalize = f"{v_stabilized_pad}scale={target_w}:{target_h}:force_original_aspect_ratio=decrease,pad={target_w}:{target_h}:(ow-iw)/2:(oh-ih)/2,format=nv12,setsar=1[v_out_sw]"
                    out_v_pad = "[v_out_sw]"
                music_cfg = self.music_config if hasattr(self, 'music_config') else {}
                t_map_func = time_mapper if self.speed_segments else None
                final_audio_chains = self.filter_builder.build_audio_chain(
                    music_config=music_cfg,
                    video_start_time=self.start_time_ms/1000.0, video_end_time=self.end_time_ms/1000.0,
                    speed_factor=self.speed_factor, disable_fades=self.disable_fades,
                    vfade_in_d=0.5 if not self.disable_fades else 0, 
                    audio_filter_cmd="anull",
                    time_mapper=t_map_func,
                    sample_rate=48000
                )
                final_audio_parts = []
                for part in final_audio_chains:
                    if part.startswith("[0:a]"):
                        part = part.replace("[0:a]", a_prepared_pad).replace("anull,", "")
                    final_audio_parts.append(part)
                filter_parts = attempt_core_filters + [v_finalize] + final_audio_parts
                complex_filter = ";".join([p for p in filter_parts if p.strip()])
                complex_filter = ";".join([p for p in filter_parts if p.strip()])
                script_path = os.path.join(self.temp_job_dir, "f.txt")
                with open(script_path, "w", encoding='utf-8') as f: f.write(complex_filter)
                self.logger.info(f"FILTER_SCRIPT_CONTENT: {complex_filter}")
                safe_script_path = script_path.replace("\\", "/")
                hw_args_pre = []
                input_threads = ['-threads', '0']
                if use_cuda:
                    hw_args_pre = ['-hwaccel', 'cuda']
                    input_threads = ['-threads', '0'] 
                cmd = [os.path.join(self.bin_dir, 'ffmpeg.exe'), '-y', '-progress', 'pipe:1'] + hw_args_pre + input_threads + ffmpeg_inputs + [
                    '-filter_complex_script', safe_script_path, '-map', out_v_pad, '-map', '[acore]',
                    '-r', str(target_fps_expr),
                    '-fps_mode', 'cfr',
                    '-c:a', 'aac', '-b:a', f'{audio_kbps}k', '-t', f"{self.duration_corrected_sec:.3f}"
                ] + vcodec + [core_path]
                self.logger.info(f"TITAN_CMD_JOINED: {' '.join(cmd)}")
                self.current_process = create_subprocess(cmd, self.logger)
                monitor_ffmpeg_progress(self.current_process, self.duration_corrected_sec, scaler_core, self._monitor_disk_space, self.logger)
                self.current_process.wait()
                return self.current_process.returncode == 0 and os.path.exists(core_path) and os.path.getsize(core_path) > 0
            self._emit_status("Rendering Titan-Locked Pipeline...")
            success = run_ffmpeg(use_cuda=True)
            if not success and not self.is_canceled:
                self.logger.warning("Titan GPU failed. Falling back to CPU Safety Mode...")
                success = run_ffmpeg(use_cuda=False)
            if success and not self.is_canceled:
                intro_path = None
                if self.intro_still_sec > 0:
                    self._emit_status("Generating Intro Sequence...")
                    scaler_intro = ProgressScaler(self.progress_update_signal, 80, 10)
                    intro_proc = IntroProcessor(os.path.join(self.bin_dir, 'ffmpeg.exe'), self.logger, self.encoder_mgr, self.temp_job_dir)
                    intro_abs = self.intro_abs_time_ms / 1000.0 if self.intro_abs_time_ms is not None else (self.start_time_ms / 1000.0)
                    intro_path = intro_proc.create_intro(
                        self.input_path, intro_abs, self.intro_still_sec, self.is_mobile_format,
                        audio_kbps, video_bitrate_kbps, scaler_intro, lambda: self.is_canceled,
                        fps_expr=target_fps_expr, preferred_encoder='h264_nvenc' if self.hardware_strategy == "NVIDIA" else None,
                        original_res_str=self.original_resolution
                    )
                self._emit_status("Finalizing Video Assembly...")
                scaler_concat = ProgressScaler(self.progress_update_signal, 90, 10)
                concat_proc = ConcatProcessor(os.path.join(self.bin_dir, 'ffmpeg.exe'), self.logger, self.base_dir, self.temp_job_dir)
                output_final = concat_proc.run_concat(
                    intro_path, core_path, scaler_concat,
                    video_bitrate_kbps=video_bitrate_kbps,
                    cancellation_check=lambda: self.is_canceled,
                    fps_expr=target_fps_expr,
                    preferred_encoder='h264_nvenc' if self.hardware_strategy == "NVIDIA" else None,
                    force_reencode=True,
                    audio_kbps=audio_kbps,
                    is_mobile=self.is_mobile_format
                )
                if output_final and os.path.exists(output_final):
                    self.progress_update_signal.emit(100)
                    self.finished_signal.emit(True, output_final)
                else:
                    self.finished_signal.emit(False, "Final assembly failed.")
            else:
                self.finished_signal.emit(False, "Render failed.")
        except Exception as e:
            if self.logger: self.logger.exception(f"FATAL: {e}")
            self.finished_signal.emit(False, str(e))
        finally:
            if hasattr(self, 'temp_job_dir'): shutil.rmtree(self.temp_job_dir, ignore_errors=True)
