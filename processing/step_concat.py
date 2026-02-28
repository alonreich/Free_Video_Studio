import os
import atexit
from .system_utils import create_subprocess
from .encoders import EncoderManager

class ConcatProcessor:
    def __init__(self, ffmpeg_path, logger, base_dir, temp_dir):
        self.ffmpeg_path = ffmpeg_path
        self.logger = logger
        self.base_dir = base_dir
        self.temp_dir = temp_dir
        self.encoder_mgr = EncoderManager(self.logger)
        self.current_process = None
        self._temp_files = []
        atexit.register(self._cleanup_temp_files)

    def _cleanup_temp_files(self):
        """Remove any remaining temporary files."""
        for path in self._temp_files:
            try:
                if os.path.exists(path):
                    os.remove(path)
                    self.logger.debug(f"Cleaned up temp file: {path}")
            except Exception as e:
                self.logger.debug(f"Failed to remove temp file {path}: {e}")
        self._temp_files.clear()

    def _register_temp_file(self, path):
        """Register a temporary file for automatic cleanup."""
        self._temp_files.append(path)

    def run_concat(self, intro_path, core_path, progress_signal, video_bitrate_kbps=None, cancellation_check=None, fps_expr="60000/1001", preferred_encoder=None, force_reencode=False, audio_kbps=320, audio_sample_rate=48000):
        files_to_concat = []
        if intro_path and os.path.exists(intro_path): 
            files_to_concat.append(intro_path)
        if core_path and os.path.exists(core_path): 
            files_to_concat.append(core_path)
        if not files_to_concat:
            self.logger.error("ConcatProcessor: No valid input files (Intro/Core missing). Aborting.")
            progress_signal.emit(100)
            return None
        output_dir = os.path.join(self.base_dir, '!!!_Output_Video_Files_!!!')
        os.makedirs(output_dir, exist_ok=True)
        if not os.access(output_dir, os.W_OK):
            self.logger.error(f"Permission denied: Cannot write to {output_dir}")
            raise PermissionError(f"Write permission denied for output directory: {output_dir}")
        i = 1
        while True:
            out_name = f"Fortnite-Video-{i}.mp4"
            output_path = os.path.join(output_dir, out_name)
            if not os.path.exists(output_path): 
                break
            i += 1
        concat_list = os.path.join(self.temp_dir, f"concat-{os.getpid()}.txt")
        with open(concat_list, "w", encoding="utf-8") as f:
            for fc in files_to_concat:
                safe_path = fc.replace('\\', '/').replace("'", "'\\''")
                f.write(f"file '{safe_path}'\n")
        self._register_temp_file(concat_list)
        use_reencode = force_reencode
        preferred_encoder = preferred_encoder or os.environ.get("VIDEO_HW_ENCODER", "h264_nvenc")
        if os.environ.get("VIDEO_FORCE_CPU") == "1":
            preferred_encoder = "libx264"

        def _fps_expr_to_float(expr, default=60.0):
            try:
                if isinstance(expr, str) and '/' in expr:
                    n, d = expr.split('/', 1)
                    d_f = float(d)
                    if d_f <= 0.0:
                        return float(default)
                    return float(n) / d_f
                return float(expr)
            except Exception:
                return float(default)
        video_track_timescale = "120000" if _fps_expr_to_float(fps_expr, 60.0) >= 100.0 else "60000"

        def _video_codec_args(enc: str):
            target_kbps = video_bitrate_kbps if video_bitrate_kbps else 12000
            vcodec, _ = self.encoder_mgr.get_codec_flags(enc, target_kbps, 5.0, fps_expr=str(fps_expr))
            return vcodec

        def _build_concat_cmd(enc: str):
            if use_reencode:
                cmd = [self.ffmpeg_path, "-y", "-progress", "pipe:1"]
                cmd.extend(["-fflags", "+genpts"])
                for fp in files_to_concat:
                    cmd.extend(["-i", fp])
                if len(files_to_concat) > 1:
                    filter_parts = []
                    for i in range(len(files_to_concat)):
                        filter_parts.append(f"[{i}:v]setpts=PTS-STARTPTS[v{i}]")
                        filter_parts.append(f"[{i}:a]aresample=async=1:first_pts=0:min_comp=0.001,asetpts=PTS-STARTPTS[a{i}]")
                    concat_in = "".join([f"[v{i}][a{i}]" for i in range(len(files_to_concat))])
                    filter_parts.append(f"{concat_in}concat=n={len(files_to_concat)}:v=1:a=1[vout][aout]")
                    filter_parts.append(f"[vout]setpts=PTS-STARTPTS[vout2]")
                    cmd.extend([
                        "-filter_complex", ";".join(filter_parts),
                        "-map", "[vout2]",
                        "-map", "[aout]",
                    ])
                else:
                    cmd.extend(["-map", "0:v:0", "-map", "0:a:0?"])
                cmd.extend([
                    "-fps_mode", "cfr",
                    "-video_track_timescale", str(video_track_timescale),
                ])
                cmd.extend(_video_codec_args(enc))
                cmd.extend([
                    "-c:a", "aac",
                    "-b:a", f"{int(max(256, min(int(audio_kbps), 512)))}k",
                    "-ar", str(int(audio_sample_rate) if audio_sample_rate else 48000),
                    "-ac", "2",
                ])
            else:
                cmd = [
                    self.ffmpeg_path, "-y",
                    "-progress", "pipe:1",
                    "-fflags", "+genpts+igndts",
                    "-avoid_negative_ts", "make_zero",
                    "-f", "concat",
                    "-safe", "0",
                    "-i", concat_list,
                ]
                cmd.extend([
                    "-map", "0:v:0",
                    "-map", "0:a:0",
                    "-c:v", "copy",
                    "-c:a", "aac",
                    "-b:a", f"{int(max(192, min(int(audio_kbps), 512)))}k",
                    "-ar", str(int(audio_sample_rate) if audio_sample_rate else 48000),
                    "-ac", "2",
                    "-af", "aresample=async=1:first_pts=0:min_comp=0.001",
                    "-fps_mode", "cfr",
                    "-video_track_timescale", str(video_track_timescale),
                ])
            cmd.extend([
                "-movflags", "+faststart",
                output_path
            ])
            return cmd

        def _run_once(enc: str):
            if os.path.exists(output_path):
                try:
                    os.remove(output_path)
                except Exception:
                    pass
            concat_cmd = _build_concat_cmd(enc)
            self.logger.info(f"STEP 3/3 CONCAT (encoder={enc}, reencode={use_reencode})")
            self.current_process = create_subprocess(concat_cmd, self.logger)
            fake_prog = 0.0
            while True:
                if cancellation_check and cancellation_check():
                    self.logger.info("Concat cancelled by user.")
                    try:
                        self.current_process.terminate()
                    except:
                        pass
                    return False
                line = self.current_process.stdout.readline()
                if not line:
                    if self.current_process.poll() is not None:
                        break
                    continue
                s = line.strip()
                if s:
                    if "=" not in s:
                        self.logger.info(s)
                fake_prog += (99.0 - fake_prog) * 0.05
                progress_signal.emit(int(fake_prog))
            self.current_process.wait()
            return self.current_process.returncode == 0
        success = _run_once(preferred_encoder)
        if not success and preferred_encoder != "libx264":
            self.logger.warning(f"Concat with {preferred_encoder} failed. Retrying with libx264 for stability.")
            success = _run_once("libx264")
        try:
            if success:
                progress_signal.emit(100)
                return output_path
            error_msg = "Concat failed."
            if not os.path.exists(intro_path) if intro_path else False:
                error_msg = "Intro file disappeared before concat."
            elif not os.path.exists(core_path):
                error_msg = "Core video file disappeared before concat."
            else:
                self.logger.error(f"FFmpeg Concat returned error code {self.current_process.returncode if self.current_process else 'unknown'}")
                error_msg = "FFmpeg Concat failed. Potential timestamp/fps mismatch between Intro and Core."
            self.logger.error(f"DIAGNOSTICS: {error_msg}")
            return None
        finally:
            try:
                if os.path.exists(concat_list):
                    os.remove(concat_list)
                    self.logger.debug(f"Removed concat list: {concat_list}")
                    if concat_list in self._temp_files:
                        self._temp_files.remove(concat_list)
            except Exception as e:
                self.logger.debug(f"Failed to remove concat list {concat_list}: {e}")
