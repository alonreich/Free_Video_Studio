import os
import time
from .system_utils import create_subprocess

class IntroProcessor:
    def __init__(self, ffmpeg_path, logger, encoder_mgr, temp_dir):
        self.ffmpeg_path = ffmpeg_path
        self.logger = logger
        self.encoder_mgr = encoder_mgr
        self.temp_dir = temp_dir
        self.current_process = None

    def create_intro(self, input_path, intro_abs_time, intro_still_sec, is_mobile, audio_kbps, video_bitrate_kbps, progress_signal, is_canceled_func, sample_rate=48000, fps_expr="60000/1001", preferred_encoder=None, target_width=None, original_res_str="1920x1080"):
        intro_path = os.path.join(self.temp_dir, f"intro-{os.getpid()}-{int(time.time())}.mp4")
        still_len = max(0.01, float(intro_still_sec))
        fps_num = 60000.0
        fps_den = 1001.0
        try:
            if isinstance(fps_expr, str) and "/" in fps_expr:
                n, d = fps_expr.split('/', 1)
                fps_num = float(n)
                fps_den = float(d)
            elif fps_expr:
                fps_num = float(fps_expr)
                fps_den = 1.0
        except Exception:
            fps_num, fps_den = 60000.0, 1001.0
        fps_val = max(1.0, fps_num / fps_den)
        loop_frames = max(1, int(round(still_len * fps_val)))
        target_w, target_h = (1080, 1920) if is_mobile else (1920, 1080)
        base_intro = (
            f"select='eq(n\\,0)',format=nv12,setsar=1,"
            f"loop=loop={loop_frames}:size=1:start=0,setpts=N/({fps_expr})/TB,fps={fps_expr}[vintro];"
            f"anullsrc=r={sample_rate}:cl=stereo,atrim=duration={still_len:.3f},asetpts=PTS-STARTPTS[aintro]"
        )
        
        def run_intro_cmd(use_hw):
            enc_name = preferred_encoder or self.encoder_mgr.get_initial_encoder()
            vcodec_intro, _ = self.encoder_mgr.get_codec_flags(enc_name, video_bitrate_kbps, 5.0, fps_expr=str(fps_expr))
            hw_flags = []
            is_nvidia = (enc_name == 'h264_nvenc')
            if use_hw:
                if is_nvidia:
                    hw_flags = ['-hwaccel', 'cuda']
                elif enc_name in ('h264_amf', 'h264_qsv'):
                    hw_flags = ['-hwaccel', 'd3d11va']
            try:
                if "x" in original_res_str:
                    iw_str, ih_str = original_res_str.lower().split("x")
                    src_w, src_h = int(iw_str), int(ih_str)
                else:
                    src_w, src_h = 1920, 1080
            except:
                src_w, src_h = 1920, 1080
            if is_nvidia and use_hw:
                try:
                    tar = target_w / target_h
                    sar = src_w / src_h
                    if sar > tar:
                        ws, hs = int(target_h * sar), target_h
                    else:
                        ws, hs = target_w, int(target_w / sar)
                    ws = (ws // 2) * 2; hs = (hs // 2) * 2
                    xo = (target_w - ws) // 2; yo = (target_h - hs) // 2
                    intro_filter = (
                        f"[0:v]select='eq(n\\,0)',setsar=1,"
                        f"loop=loop={loop_frames}:size=1:start=0,setpts=N/({fps_expr})/TB,fps={fps_expr},format=nv12,hwupload_cuda[intro_scaled_hw];"
                        f"color=c=black:s={target_w}x{target_h}:r={fps_expr},format=nv12,hwupload_cuda[intro_bg];"
                        f"[intro_scaled_hw]scale_cuda=w={ws}:h={hs}[intro_scaled_resized];"
                        f"[intro_bg][intro_scaled_resized]overlay_cuda=x={xo}:y={yo},hwdownload,format=nv12,setsar=1[vintro];"
                        f"anullsrc=r={sample_rate}:cl=stereo,atrim=duration={still_len:.3f},asetpts=PTS-STARTPTS[aintro]"
                    )
                except:
                    intro_filter = f"[0:v]scale={target_w}:{target_h}:force_original_aspect_ratio=increase,crop={target_w}:{target_h},{base_intro}"
            else:
                intro_filter = f"[0:v]scale={target_w}:{target_h}:force_original_aspect_ratio=increase,crop={target_w}:{target_h},{base_intro}"
            cmd = [
                self.ffmpeg_path, "-y",
                "-progress", "pipe:1",
                "-threads", "0",
            ] + hw_flags + [
                "-fflags", "+genpts+igndts",
                "-ss", f"{intro_abs_time:.6f}", 
                "-i", input_path, 
                "-t", "0.2"
            ] + vcodec_intro + [
                "-fps_mode", "cfr",
                "-pix_fmt", "nv12", 
                "-movflags", "+faststart",
                "-c:a", "aac", "-b:a", f'{audio_kbps}k', '-ar', str(int(sample_rate) if sample_rate else 48000), '-ac', '2',
                "-filter_complex", intro_filter,
                "-map", "[vintro]", "-map", "[aintro]", 
                "-shortest", 
                intro_path
            ]
            self.logger.info(f"STEP 2/3 INTRO (Hardware: {use_hw})")
            proc = create_subprocess(cmd, self.logger)
            self.current_process = proc

            from .system_utils import monitor_ffmpeg_progress
            monitor_ffmpeg_progress(proc, still_len, progress_signal, is_canceled_func, self.logger)
            proc.wait()
            return proc.returncode == 0, False
        enc_name = preferred_encoder or self.encoder_mgr.get_initial_encoder()
        is_hw_preferred = enc_name in ('h264_nvenc', 'h264_amf', 'h264_qsv')
        if is_hw_preferred:
            success, is_cuda_err = run_intro_cmd(use_hw=True)
            if not success:
                self.logger.warning("Intro hardware processing failed. Retrying with CPU fallback...")
                success, _ = run_intro_cmd(use_hw=False)
        else:
            success, _ = run_intro_cmd(use_hw=False)
        return intro_path if success else None
