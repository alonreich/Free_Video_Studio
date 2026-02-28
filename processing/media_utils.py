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
            fps = float(frac)
            if fps <= 1.0:
                return fallback
            if abs(fps - 120.0) <= 3.0 or abs(fps - 119.88) <= 3.0:
                return "120"
            if abs(fps - 60.0) <= 1.0 or abs(fps - 59.94) <= 1.0:
                return "60000/1001"
            if abs(fps - 30.0) <= 0.7 or abs(fps - 29.97) <= 0.7:
                return "30000/1001"
            if abs(fps - 24.0) <= 0.7 or abs(fps - 23.976) <= 0.7:
                return "24000/1001"
            if fps >= 90.0:
                return "120"
            if fps >= 45.0:
                return "60000/1001"
            if fps >= 26.0:
                return "30000/1001"
            if fps >= 20.0:
                return "24000/1001"
            return fallback
        except Exception:
            return fallback

def calculate_video_bitrate(input_path, duration, audio_kbps, target_mb, keep_highest_res, logger=None):
    """
    [FIX #11] Calculates video bitrate to hit target MB. 
    Conservative calculation (using 1024 divisor) ensures we rarely exceed target size.
    """
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
        mb_in_bits = target_mb * 8 * 1024 * 1024 * 0.95
        target_size_bits = mb_in_bits
    audio_bits = audio_kbps * 1000 * duration
    video_bits = target_size_bits - audio_bits
    if duration <= 0: 
        return None
    if video_bits <= 0:
        if logger:
            logger.warning("Target size is too small for audio track; forcing 300kbps video.")
        return 300
    calculated_kbps = int(video_bits / (1000 * duration))
    if calculated_kbps < 300:
        if logger:
            logger.warning(f"Calculated bitrate ({calculated_kbps}k) is very low. Quality will be impacted.")
    return max(300, calculated_kbps)
    