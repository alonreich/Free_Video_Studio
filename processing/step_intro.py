import os
import time
from .system_utils import create_subprocess, monitor_ffmpeg_progress

class IntroProcessor:

    def __init__(self, ffmpeg_path, logger, encoder_mgr, temp_dir):
        self.ffmpeg_path = ffmpeg_path
        self.logger = logger
        self.encoder_mgr = encoder_mgr
        self.temp_dir = temp_dir
        self.current_process = None

    def create_intro(self, input_path, intro_abs_time, intro_still_sec, is_mobile, audio_kbps, video_bitrate_kbps, progress_signal, is_canceled_func):
        intro_path = os.path.join(self.temp_dir, f"intro-{os.getpid()}-{int(time.time())}.mp4")
        still_len = max(0.01, float(intro_still_sec))
        loop_frames = max(1, int(round(still_len * 60)))
        base_intro = (
            f"select='eq(n\\,0)',format=yuv420p,setsar=1,"
            f"loop=loop={loop_frames}:size=1:start=0,setpts=N/60/TB,fps=60[vintro];"
            f"anullsrc=r=48000:cl=stereo,atrim=duration={still_len:.3f},asetpts=PTS-STARTPTS[aintro]"
        )
        if is_mobile:
            intro_filter = f"[0:v]scale=1080:1920:force_original_aspect_ratio=increase,crop=1080:1920,{base_intro}"
        else:
            intro_filter = f"[0:v]{base_intro}"
        vcodec_intro = self.encoder_mgr.get_intro_codec_flags(video_bitrate_kbps)
        intro_cmd = [
            self.ffmpeg_path, "-y", "-hwaccel", "auto",
            "-ss", f"{intro_abs_time:.6f}", "-i", input_path, "-t", "0.2"
        ] + vcodec_intro + [
                "-pix_fmt", "yuv420p", "-movflags", "+faststart",
            "-c:a", "aac", "-b:a", f'{audio_kbps}k', '-ar', '48000',
            "-filter_complex", intro_filter,
            "-map", "[vintro]", "-map", "[aintro]", "-shortest", intro_path
        ]
        self.logger.info("STEP 2/3 INTRO")
        self.current_process = create_subprocess(intro_cmd)
        monitor_ffmpeg_progress(
            self.current_process, 
            still_len, 
            progress_signal, 
            is_canceled_func, 
            self.logger
        )
        self.current_process.wait()
        if self.current_process.returncode == 0:
            return intro_path
        return None