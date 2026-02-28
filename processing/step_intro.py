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

    def create_intro(self, input_path, intro_abs_time, intro_still_sec, is_mobile, audio_kbps, video_bitrate_kbps, progress_signal, is_canceled_func, sample_rate=48000, fps_expr="60000/1001", preferred_encoder=None):
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
            if is_mobile:
                if is_nvidia and use_hw:
                    try:
                        iw, ih = 1920, 1080
                        tw, th = 1080, 1920
                        tar = tw / th
                        sar = iw / ih
                        if sar > tar:
                            ws, hs = int(th * sar), th
                        else:
                            ws, hs = tw, int(tw / sar)
                        ws = (ws // 2) * 2; hs = (hs // 2) * 2
                        xo = (tw - ws) // 2; yo = (th - hs) // 2
                        intro_filter = (
                            f"color=c=black:s=1080x1920,format=nv12,hwupload_cuda[intro_bg];"
                            f"[0:v]scale_cuda=w={ws}:h={hs}[intro_scaled];"
                            f"[intro_bg][intro_scaled]overlay_cuda=x={xo}:y={yo},"
                            f"select='eq(n\\,0)',format=cuda,setsar=1,"
                            f"loop=loop={loop_frames}:size=1:start=0,setpts=N/({fps_expr})/TB,fps={fps_expr},hwdownload,format=nv12[vintro];"
                            f"anullsrc=r={sample_rate}:cl=stereo,atrim=duration={still_len:.3f},asetpts=PTS-STARTPTS[aintro]"
                        )
                    except:
                        intro_filter = f"[0:v]scale=1080:1920:force_original_aspect_ratio=increase:flags=bilinear,crop=1080:1920,{base_intro}"
                else:
                    intro_filter = f"[0:v]scale=1080:1920:force_original_aspect_ratio=increase:flags=bilinear,crop=1080:1920,{base_intro}"
            else:
                intro_filter = f"[0:v]{base_intro}"
            cmd = [
                self.ffmpeg_path, "-y",
                "-progress", "pipe:1",
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
            output_lines = []
            while True:
                if is_canceled_func and is_canceled_func():
                    try:
                        proc.terminate()
                    except Exception:
                        pass
                    return False, False
                line = proc.stdout.readline()
                if not line:
                    if proc.poll() is not None: break
                    continue
                output_lines.append(line)
                if progress_signal: progress_signal.emit(50)
            proc.wait()
            success = (proc.returncode == 0)
            full_out = "".join(output_lines)
            is_cuda_err = any(x in full_out for x in ("CUDA_ERROR", "cuvid", "failed setup"))
            return success, is_cuda_err
        success, _ = run_intro_cmd(use_hw=False)
        if not success:
            self.logger.warning("Intro CPU decode failed. Retrying with hardware decode...")
            success, _ = run_intro_cmd(use_hw=True)
        return intro_path if success else None