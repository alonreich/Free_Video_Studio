import os
import sys
import subprocess
from fractions import Fraction

class MediaProber:
    def __init__(self, bin_dir, input_path):
        self.bin_dir = bin_dir
        self.input_path = input_path
        self.ffprobe_path = os.path.join(self.bin_dir, 'ffprobe.exe')

    def _run_command(self, args):
        """Internal helper to run ffprobe commands safely with correct window flags."""
        try:
            full_cmd = [self.ffprobe_path, "-v", "error", "-of", "default=nw=1:nk=1"] + args + [self.input_path]
            creationflags = 0
            if sys.platform == "win32":
                creationflags = subprocess.CREATE_NO_WINDOW
            result = subprocess.run(
                full_cmd, 
                capture_output=True, 
                text=True, 
                check=True,
                creationflags=creationflags
            )
            return (result.stdout or "").strip()
        except Exception:
            return None

    def run_probe(self, args):
        """Run probe and return a float value (converted to kbps if applicable)."""
        val_str = self._run_command(args)
        if val_str:
            try:
                val = float(val_str)
                if val > 0:
                    return max(8, int(round(val / 1000.0)))
            except ValueError:
                pass
        return None

    def get_audio_bitrate(self):
        kbps = self.run_probe(["-select_streams", "a:0", "-show_entries", "stream=bit_rate"])
        if kbps and 8 <= kbps <= 1536:
            return kbps
        kbps = self.run_probe(["-show_entries", "format=bit_rate"])
        if kbps and 8 <= kbps <= 1536:
            return kbps
        return None

    def get_sample_rate(self):
        val_str = self._run_command(["-select_streams", "a:0", "-show_entries", "stream=sample_rate"])
        if val_str:
            try:
                return int(val_str)
            except ValueError:
                pass
        return 48000

    def get_video_bitrate(self):
        kbps = self.run_probe(["-select_streams", "v:0", "-show_entries", "stream=bit_rate"])
        if kbps: return kbps
        kbps = self.run_probe(["-show_entries", "format=bit_rate"])
        return kbps or 25000

    def get_duration(self):
        val_str = self._run_command(["-show_entries", "format=duration"])
        try:
            return float(val_str)
        except:
            return 0.0

    def get_video_fps_expr(self, fallback: str = "60000/1001"):
        """
        Returns a stable ffmpeg fps expression string (e.g. "60000/1001").
        We prefer broadcast-friendly rationals to avoid micro-jitter from 59.94<->60 drift.
        """
        raw = self._run_command([
            "-select_streams", "v:0",
            "-show_entries", "stream=avg_frame_rate"
        ])
        if not raw:
            return fallback
        try:
            frac = Fraction(str(raw).strip())
            if frac.denominator == 0:
                return fallback
            fps = float(frac)
            if fps <= 1.0:
                return fallback
            if fps > 60.0:
                fps = 60.0
                return "60"
            if abs(fps - 60.0) < 0.001: return "60"
            if abs(fps - 59.94) < 0.01: return "60000/1001"
            if abs(fps - 30.0) < 0.001: return "30"
            if abs(fps - 29.97) < 0.01: return "30000/1001"
            if abs(fps - 24.0) < 0.001: return "24"
            if abs(fps - 23.976) < 0.01: return "24000/1001"
            if abs(fps - round(fps)) < 0.001:
                return str(int(round(fps)))
            return str(frac)
        except Exception:
            return fallback

def calculate_video_bitrate(input_path, duration, audio_kbps, target_mb, keep_highest_res, logger=None, res_str="1920x1080", fps_expr="60", quality_level=2, prober=None):
    """
    [FIX #11] Calculates video bitrate to hit target MB accurately.
    'Maximum' quality now matches original source bitrate with a small buffer for assembly.
    """
    if keep_highest_res and prober:
        orig_br = prober.get_video_bitrate()
        return int(orig_br * 1.25)
    if target_mb is None:
        target_mb = 45.0
    mb_for_bits = float(target_mb)
    mb_in_bits = mb_for_bits * 8 * 1024 * 1024 * 1.0
    target_size_bits = mb_in_bits * 0.95
    audio_bits = audio_kbps * 1024 * (duration)
    video_bits = target_size_bits - audio_bits
    if duration <= 0:
        return 6000
    calculated_kbps = int(video_bits / (1024 * duration))
    return max(300, calculated_kbps)
