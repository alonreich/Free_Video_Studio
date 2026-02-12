import os
import subprocess
import re
from PyQt5.QtCore import QThread, pyqtSignal
from utilities.merger_utils import _get_logger, kill_process_tree
import os
import subprocess
import re
import threading
import queue
from PyQt5.QtCore import QThread, pyqtSignal
from utilities.merger_utils import _get_logger, kill_process_tree

class MergerEngine(QThread):
    """
    Enhanced FFmpeg Engine.
    - Supports GPU Encoding (NVENC/AMF/QSV) detection.
    - Handles Audio Resampling to prevent mix failures.
    - Non-blocking I/O.
    """
    progress = pyqtSignal(int, str)
    finished = pyqtSignal(bool, str)
    log_line = pyqtSignal(str)

    def __init__(self, ffmpeg_path, cmd_base, output_path, total_duration_sec=0, use_gpu=False):
        super().__init__()
        self.ffmpeg_path = ffmpeg_path
        self.cmd_base = cmd_base
        self.output_path = output_path
        self.total_duration = max(1.0, float(total_duration_sec))
        self.use_gpu = use_gpu
        self.logger = _get_logger()
        self._process = None
        self._is_cancelled = False
        self._last_time_str = "00:00:00"

    def _detect_gpu_encoder(self):
        """
        Detects available GPU encoders (NVENC, AMF, QSV).
        Falls back to libx264 if none found.
        """
        if not self.use_gpu:
            return ["-c:v", "libx264", "-preset", "medium", "-crf", "26"]
        try:
            cmd = [self.ffmpeg_path, "-hide_banner", "-encoders"]
            flags = subprocess.CREATE_NO_WINDOW if os.name == 'nt' else 0
            res = subprocess.run(cmd, capture_output=True, text=True, creationflags=flags, timeout=5)
            out = res.stdout
            if "h264_nvenc" in out:
                self.logger.info("GPU: NVIDIA NVENC detected.")
                return ["-c:v", "h264_nvenc", "-preset", "p4", "-cq", "26"]
            elif "h264_amf" in out:
                self.logger.info("GPU: AMD AMF detected.")
                return ["-c:v", "h264_amf", "-quality", "balanced", "-rc", "cqp", "-qp_i", "26", "-qp_p", "26"]
            elif "h264_qsv" in out:
                self.logger.info("GPU: Intel QSV detected.")
                return ["-c:v", "h264_qsv", "-global_quality", "26"]
        except Exception as e:
            self.logger.warning(f"GPU Probe failed: {e}")
        return ["-c:v", "libx264", "-preset", "medium", "-crf", "26"]

    def run(self):
        self._is_cancelled = False
        cmd = list(self.cmd_base)
        cmd.extend(["-c:a", "aac", "-ar", "48000", "-b:a", "128k"])
        cmd.extend(self._detect_gpu_encoder())
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
        log_queue = queue.Queue()

        def _reader_thread(proc, q):
            for line in iter(proc.stdout.readline, ''):
                q.put(line)
            proc.stdout.close()
        t = threading.Thread(target=_reader_thread, args=(self._process, log_queue))
        t.daemon = True
        t.start()
        log_buffer = []
        while True:
            if self._is_cancelled:
                break
            try:
                line = log_queue.get(timeout=0.1)
                line = line.strip()
                self.log_line.emit(line)
                self._parse_progress(line)
                log_buffer.append(line)
                if len(log_buffer) > 80:
                    log_buffer.pop(0)
            except queue.Empty:
                if not t.is_alive():
                    break
        if self._is_cancelled:
            self._kill_process()
            self.finished.emit(False, "Cancelled by user.")
            return
        self._process.wait()
        rc = self._process.returncode
        if rc == 0:
            if os.path.exists(self.output_path) and os.path.getsize(self.output_path) > 0:
                self.finished.emit(True, self.output_path)
            else:
                self.finished.emit(False, "Render complete but output file is empty.")
        else:
            important = [
                l for l in log_buffer
                if re.search(r"error|failed|invalid|unable|cannot|no such", l, re.IGNORECASE)
            ]
            err_msg = "\n".join(important[-12:] if important else log_buffer[-12:]) or f"Exit Code {rc}"
            self.finished.emit(False, f"Encoding Failed:\n{err_msg}")

    def _parse_progress(self, line):
        if "time=" in line:
            try:
                match = re.search(r'time=(\d{2}):(\d{2}):(\d{2}\.\d{2})', line)
                if match:
                    h, m, s = map(float, match.groups())
                    current_sec = h*3600 + m*60 + s
                    pct = int((current_sec / self.total_duration) * 100)
                    pct = max(0, min(100, pct))
                    self._last_time_str = f"{int(h):02}:{int(m):02}:{int(s):02}"
                    self.progress.emit(pct, self._last_time_str)
            except (ValueError, TypeError, ZeroDivisionError, AttributeError) as e:
                self.logger.debug(f"Progress parse failed: {e}")

    def cancel(self):
        self._is_cancelled = True
        self._kill_process()

    def _kill_process(self):
        if self._process:
            kill_process_tree(self._process.pid)