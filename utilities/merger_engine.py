import os
import queue
import re
import subprocess
import threading
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

    def __init__(self, ffmpeg_path, cmd_base, output_path, total_duration_sec=0, use_gpu=False, target_v_bitrate=0, target_a_bitrate=0, target_a_rate=48000, quality_level=4):
        super().__init__()
        self.ffmpeg_path = ffmpeg_path
        self.cmd_base = cmd_base
        self.output_path = output_path
        self.total_duration = max(1.0, float(total_duration_sec))
        self.use_gpu = use_gpu
        self.target_v_bitrate = target_v_bitrate
        self.target_a_bitrate = target_a_bitrate
        self.target_a_rate = target_a_rate
        self.quality_level = quality_level
        self.logger = _get_logger()
        self._process = None
        self._is_cancelled = False
        self._last_time_str = "00:00:00"

    def _detect_gpu_encoder(self):
        """
        Detects available GPU encoders (NVENC, AMF, QSV).
        Falls back to libx264 if none found.
        Adjusts quality based on self.quality_level (0=20% to 4:100%).
        """
        quality_multipliers = {0: 0.20, 1: 0.40, 2: 0.60, 3: 0.80, 4: 1.0}
        mult = quality_multipliers.get(self.quality_level, 1.0)
        crf_map = {4: 22, 3: 26, 2: 30, 1: 34, 0: 40}
        crf_val = crf_map.get(self.quality_level, 26)
        v_bitrate_args = []
        if self.target_v_bitrate > 0:
            effective_bitrate = int(self.target_v_bitrate * mult)
            v_bitrate_args = ["-b:v", f"{effective_bitrate}", "-maxrate:v", f"{int(effective_bitrate * 1.5)}", "-bufsize:v", f"{int(effective_bitrate * 2)}"]
        if not self.use_gpu:
            base = ["-c:v", "libx264", "-preset", "medium"]
            if not v_bitrate_args:
                base.extend(["-crf", str(crf_val)])
            else:
                base.extend(v_bitrate_args)
            return base
        try:
            cmd = [self.ffmpeg_path, "-hide_banner", "-encoders"]
            flags = subprocess.CREATE_NO_WINDOW if os.name == 'nt' else 0
            res = subprocess.run(cmd, capture_output=True, text=True, creationflags=flags, timeout=5)
            out = res.stdout
            if re.search(r"\s+h264_nvenc\s+", out):
                self.logger.info(f"GPU: NVIDIA NVENC detected. Quality Level: {self.quality_level}")
                base = ["-c:v", "h264_nvenc", "-preset", "p4"]
                if not v_bitrate_args: base.extend(["-cq", str(crf_val)])
                else: base.extend(v_bitrate_args)
                return base
            elif re.search(r"\s+h264_amf\s+", out):
                self.logger.info(f"GPU: AMD AMF detected. Quality Level: {self.quality_level}")
                base = ["-c:v", "h264_amf", "-quality", "balanced"]
                if not v_bitrate_args: base.extend(["-rc", "cqp", "-qp_i", str(crf_val), "-qp_p", str(crf_val)])
                else: base.extend(v_bitrate_args)
                return base
            elif re.search(r"\s+h264_qsv\s+", out):
                self.logger.info(f"GPU: Intel QSV detected. Quality Level: {self.quality_level}")
                base = ["-c:v", "h264_qsv"]
                if not v_bitrate_args: base.extend(["-global_quality", str(crf_val)])
                else: base.extend(v_bitrate_args)
                return base
        except Exception as e:
            self.logger.warning(f"GPU Probe failed: {e}")
        base = ["-c:v", "libx264", "-preset", "medium"]
        if not v_bitrate_args: base.extend(["-crf", str(crf_val)])
        else: base.extend(v_bitrate_args)
        return base

    def run(self):
        self._is_cancelled = False
        cmd = list(self.cmd_base)
        a_bitrate = f"{self.target_a_bitrate}" if self.target_a_bitrate > 0 else "128k"
        a_rate = f"{self.target_a_rate}" if self.target_a_rate > 0 else "48000"
        cmd.extend(["-c:a", "aac", "-ar", a_rate, "-b:a", a_bitrate])
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
                match = re.search(r'time=\s*(\d+):(\d+):(\d+(?:\.\d+)?)', line)
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
