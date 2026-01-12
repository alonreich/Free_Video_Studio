import os
import sys
import subprocess

class MediaProber:

    def __init__(self, bin_dir, input_path):
        self.bin_dir = bin_dir
        self.input_path = input_path
        self.ffprobe_path = os.path.join(self.bin_dir, 'ffprobe.exe')

    def run_probe(self, args):
        try:
            base_cmd = [self.ffprobe_path, "-v", "error", "-of", "default=nw=1:nk=1"]
            full_cmd = base_cmd + args + [self.input_path]
            creationflags = 0
            if sys.platform == "win32":
                creationflags = subprocess.CREATE_NO_WINDOW
            r = subprocess.run(
                full_cmd, 
                capture_output=True, 
                text=True, 
                check=True,
                creationflags=creationflags
            )
            raw_val = (r.stdout or "0").strip()
            val = float(raw_val or 0)
            if val > 0:
                return max(8, int(round(val / 1000.0)))
            return None
        except Exception:
            return None

    def get_audio_bitrate(self):
        args_stream = ["-select_streams", "a:0", "-show_entries", "stream=bit_rate"]
        kbps = self.run_probe(args_stream)
        if kbps:
            return kbps
        args_format = ["-show_entries", "format=bit_rate"]
        kbps = self.run_probe(args_format)
        return kbps

def calculate_video_bitrate(input_path, duration, audio_kbps, target_mb, keep_highest_res):
    target_size_bits = 0
    is_max_quality = False
    if keep_highest_res:
        try:
            src_bytes = os.path.getsize(input_path)
            target_size_bits = max(1, src_bytes) * 8 
            is_max_quality = True
        except Exception:
            target_mb = 52.0
    if not is_max_quality:
        if target_mb is None:
            target_mb = 52.0
        mb_in_bits = target_mb * 8 * 1024 * 1024
        target_size_bits = mb_in_bits
    audio_bits = audio_kbps * 1024 * duration
    video_bits = target_size_bits - audio_bits
    if video_bits <= 0:
        if is_max_quality:
            return 300
        return None
    calculated_kbps = int(video_bits / (1024 * duration))
    if is_max_quality:
        return max(300, calculated_kbps)
    return calculated_kbps