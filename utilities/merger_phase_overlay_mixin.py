import math
import shutil
import subprocess
import sys
import time
from collections import deque
import psutil
from PyQt5.QtCore import Qt, QTimer, QRect, QThread, pyqtSignal, QPoint
from PyQt5.QtGui import QRegion, QIcon, QColor, QPainter, QPen, QBrush, QFont
from PyQt5.QtWidgets import QWidget, QPlainTextEdit
_ENABLE_SAFE_GPU_STATS = shutil.which("nvidia-smi") is not None

class MergerGpuWorker(QThread):
    stats_updated = pyqtSignal(int)

    def __init__(self):
        super().__init__()
        self._running = True

    def stop(self):
        self._running = False
        self.wait()

    def run(self):
        while self._running:
            gpu = 0
            try:
                if _ENABLE_SAFE_GPU_STATS:
                    flags = subprocess.CREATE_NO_WINDOW if sys.platform == "win32" else 0
                    r = subprocess.run(
                        ["nvidia-smi", "--query-gpu=utilization.gpu,utilization.encoder", "--format=csv,noheader,nounits", "-i", "0"],
                        capture_output=True, text=True, timeout=1.0, creationflags=flags
                    )
                    if r.returncode == 0:
                        row = (r.stdout or "0,0").strip().splitlines()[0].split(",")
                        gpu_core = int(row[0].strip() or 0)
                        gpu_enc = int(row[1].strip() or 0)
                        gpu = max(0, min(100, max(gpu_core, gpu_enc)))
            except Exception: pass
            self.stats_updated.emit(gpu)
            for _ in range(10):
                if not self._running: break
                time.sleep(0.1)

class MergerPhaseOverlayMixin:
    def _append_live_log(self, line: str) -> None:
        if not getattr(self, "live_log", None): return
        if " | " in line: line = line.split(" | ")[-1].strip()
        if not hasattr(self, "_log_buffer"): self._log_buffer = []
        self._log_buffer.append(line)

    def _flush_logs(self):
        if hasattr(self, "_log_buffer") and self._log_buffer and getattr(self, "live_log", None):
            chunk = "\n".join(self._log_buffer)
            self._log_buffer.clear()
            self.live_log.appendPlainText(chunk)
            self.live_log.verticalScrollBar().setValue(self.live_log.verticalScrollBar().maximum())

    def _ensure_overlay_widgets(self) -> None:
        if getattr(self, "_overlay", None): return
        self._overlay = QWidget(self)
        self._overlay.setWindowFlags(Qt.FramelessWindowHint)
        self._overlay.setAttribute(Qt.WA_NoSystemBackground, True)
        self._overlay.setAttribute(Qt.WA_TransparentForMouseEvents, True)
        self._overlay.hide()
        self._graph = QWidget(self._overlay)
        self._graph.setAttribute(Qt.WA_NoSystemBackground, True)
        self._graph.setAttribute(Qt.WA_TransparentForMouseEvents, True)
        self.live_log = QPlainTextEdit(self._overlay)
        self.live_log.setReadOnly(True)
        self.live_log.setMaximumBlockCount(5000)
        self.live_log.setStyleSheet("QPlainTextEdit { color:#25e825; background:#0b141d; border:1px solid #1f3545; border-radius:8px; font-family: Consolas, monospace; font-size: 12px; }")
        for nm in ("_cpu_hist", "_gpu_hist", "_mem_hist", "_iops_hist"): setattr(self, nm, deque(maxlen=200))
        self._log_flush_timer = QTimer(self); self._log_flush_timer.setInterval(250)
        self._log_flush_timer.timeout.connect(self._flush_logs); self._log_flush_timer.start()
        self._gpu_worker = MergerGpuWorker(); self._gpu_worker.stats_updated.connect(lambda v: setattr(self, "_last_gpu_val", v))
        self._stats_timer = QTimer(self); self._stats_timer.setInterval(1000)
        self._stats_timer.timeout.connect(self._sample_perf_counters_safe)
        self._overlay.installEventFilter(self)
