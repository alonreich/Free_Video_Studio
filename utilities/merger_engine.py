import os
import subprocess
import re
import shutil
from pathlib import Path
from PyQt5.QtCore import QThread, pyqtSignal
from utilities.merger_utils import _get_logger, kill_process_tree

class MergerEngine(QThread):
    """
    Enhanced FFmpeg Engine.
    - Supports GPU Encoding (NVENC/AMF/QSV) detection.
    - Handles Audio Resampling to prevent mix failures.
    - Non-blocking I/O.
    """
    progress = pyqtSignal(int, str) # percent, time_str
    finished = pyqtSignal(bool, str) # success, msg
    log_line = pyqtSignal(str)

    def __init__(self, ffmpeg_path, cmd_base, output_path, total_duration_sec=0, use_gpu=False):
        super().__init__()
        self.ffmpeg_path = ffmpeg_path
        self.cmd_base = cmd_base # Base command BEFORE encoding flags
        self.output_path = output_path
        self.total_duration = max(1.0, float(total_duration_sec))
        self.use_gpu = use_gpu
        self.logger = _get_logger()
        self._process = None
        self._is_cancelled = False

    def _detect_gpu_encoder(self):
        """
        Detects available GPU encoders (NVENC, AMF, QSV).
        Falls back to libx264 if none found.
        """
        if not self.use_gpu:
            return ["-c:v", "libx264", "-preset", "medium", "-crf", "23"]
            
        try:
            # Probe encoders
            cmd = [self.ffmpeg_path, "-hide_banner", "-encoders"]
            flags = subprocess.CREATE_NO_WINDOW if os.name == 'nt' else 0
            res = subprocess.run(cmd, capture_output=True, text=True, creationflags=flags, timeout=5)
            out = res.stdout
            
            if "h264_nvenc" in out:
                self.logger.info("GPU: NVIDIA NVENC detected.")
                return ["-c:v", "h264_nvenc", "-preset", "p4", "-cq", "23"]
            elif "h264_amf" in out:
                self.logger.info("GPU: AMD AMF detected.")
                return ["-c:v", "h264_amf", "-quality", "balanced"]
            elif "h264_qsv" in out:
                self.logger.info("GPU: Intel QSV detected.")
                return ["-c:v", "h264_qsv", "-global_quality", "23"]
        except Exception as e:
            self.logger.warning(f"GPU Probe failed: {e}")

        return ["-c:v", "libx264", "-preset", "medium", "-crf", "23"]

    def run(self):
        self._is_cancelled = False
        
        # Finalize Command
        cmd = list(self.cmd_base)
        
        # Audio Standardization (Fixes #3 - 44.1k Downgrade -> 48k Standard)
        # Fixes #70 - Sample Rate Mismatch
        cmd.extend(["-c:a", "aac", "-ar", "48000", "-b:a", "192k"])
        
        # Video Encoding (Fixes #92 - GPU Support, Fix #4 - Invisible GPU)
        cmd.extend(self._detect_gpu_encoder())
        
        # Output
        cmd.append(str(self.output_path))
        
        self.logger.info(f"ENGINE: Executing: {' '.join(cmd)}")
        
        startupinfo = None
        if os.name == 'nt':
            startupinfo = subprocess.STARTUPINFO()
            startupinfo.dwFlags |= subprocess.STARTF_USESHOWWINDOW

        try:
            self._process = subprocess.Popen(
                cmd,
                stdout=subprocess.PIPE,
                stderr=subprocess.STDOUT,
                stdin=subprocess.DEVNULL,
                universal_newlines=True,
                encoding='utf-8',
                errors='replace',
                startupinfo=startupinfo,
                creationflags=subprocess.CREATE_NO_WINDOW if os.name == 'nt' else 0
            )
        except Exception as e:
            self.finished.emit(False, f"Failed to start FFmpeg: {e}")
            return

        # Output parsing loop
        # Fixes #21 (Deadlock) and #16 (Truncation)
        log_buffer = []
        while True:
            if self._is_cancelled:
                break
            
            line = self._process.stdout.readline()
            if not line and self._process.poll() is not None:
                break
            
            if line:
                line = line.strip()
                self.log_line.emit(line)
                self._parse_progress(line)
                if "Error" in line or "Failed" in line:
                    log_buffer.append(line)
                    if len(log_buffer) > 10: log_buffer.pop(0)

        # Cleanup
        if self._is_cancelled:
            self._kill_process()
            self.finished.emit(False, "Cancelled by user.")
            return

        rc = self._process.poll()
        if rc == 0:
            if os.path.exists(self.output_path) and os.path.getsize(self.output_path) > 0:
                self.finished.emit(True, self.output_path)
            else:
                self.finished.emit(False, "Render complete but output file is empty.")
        else:
            err_msg = "\n".join(log_buffer) or f"Exit Code {rc}"
            self.finished.emit(False, f"Encoding Failed:\n{err_msg}")

    def _parse_progress(self, line):
        if "time=" in line:
            try:
                match = re.search(r'time=(\d{2}):(\d{2}):(\d{2}\.\d{2})', line)
                if match:
                    h, m, s = map(float, match.groups())
                    current_sec = h*3600 + m*60 + s
                    pct = int((current_sec / self.total_duration) * 100)
                    pct = max(0, min(99, pct))
                    self.progress.emit(pct, f"{int(h):02}:{int(m):02}:{int(s):02}")
            except Exception:
                pass

    def cancel(self):
        self._is_cancelled = True
        self._kill_process()

    def _kill_process(self):
        if self._process:
            kill_process_tree(self._process.pid)