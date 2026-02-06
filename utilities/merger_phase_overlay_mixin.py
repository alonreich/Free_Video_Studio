import math
import shutil
import subprocess
import sys
import time
from collections import deque
import psutil
from PyQt5.QtCore import Qt, QTimer, QRect, QThread, pyqtSignal
from PyQt5.QtGui import QRegion, QIcon
from PyQt5.QtWidgets import QWidget, QPlainTextEdit
_ENABLE_SAFE_GPU_STATS = shutil.which("nvidia-smi") is not None

class StatsWorker(QThread):
    stats_ready = pyqtSignal(int, int, int, int)

    def __init__(self, parent=None):
        super().__init__(parent)
        self.running = True
        self._iops_prev = None
        self._iops_dyn_max = 1.0

    def stop(self):
        self.running = False
        self.quit()
        if not self.wait(1000):
            self.terminate()
            self.wait()

    def run(self):
        while self.running:
            try:
                try:
                    cpu = int(psutil.cpu_percent(interval=0.5))
                except Exception:
                    cpu = 0
                gpu = 0
                try:
                    if _ENABLE_SAFE_GPU_STATS:
                        r = subprocess.run(
                            ["nvidia-smi",
                            "--query-gpu=utilization.gpu,utilization.encoder",
                            "--format=csv,noheader,nounits", "-i", "0"],
                            capture_output=True, text=True, timeout=0.6,
                            creationflags=(subprocess.CREATE_NO_WINDOW if sys.platform == "win32" else 0)
                        )
                        row = (r.stdout or "0,0").strip().splitlines()[0].split(",")
                        gpu_core = int(row[0].strip() or 0)
                        gpu_enc  = int(row[1].strip() or 0)
                        gpu = max(0, min(100, max(gpu_core, gpu_enc)))
                except Exception:
                    gpu = 0
                try:
                    mem = int(psutil.virtual_memory().percent)
                except Exception:
                    mem = 0
                try:
                    now = time.time()
                    cur = psutil.disk_io_counters()
                    cur_ops = int(getattr(cur, "read_count", 0)) + int(getattr(cur, "write_count", 0))
                    if self._iops_prev is None:
                        iops = 0.0
                    else:
                        dt = max(1e-3, now - self._iops_prev["ts"])
                        iops = max(0.0, (cur_ops - self._iops_prev["ops"]) / dt)
                    self._iops_prev = {"ts": now, "ops": cur_ops}
                    dyn = max(1.0, float(self._iops_dyn_max))
                    if iops > dyn * 0.98:
                        dyn = iops * 1.25
                    self._iops_dyn_max = dyn
                    iops_pct = int(max(0, min(100, round(100.0 * iops / dyn))))
                except Exception:
                    iops_pct = 0
                self.stats_ready.emit(cpu, gpu, mem, iops_pct)
            except Exception:
                pass
            for _ in range(15):
                if not self.running: break
                time.sleep(0.1)

class MergerPhaseOverlayMixin:
    def _append_live_log(self, line: str) -> None:
        """Appends a log line to the overlay's live_log widget."""
        try:
            if not getattr(self, "live_log", None):
                return
            if " | " in line:
                parts = line.split(" | ")
                line = parts[-1].strip()
            self.live_log.appendPlainText(line)
            self.live_log.verticalScrollBar().setValue(
                self.live_log.verticalScrollBar().maximum()
            )
        except Exception:
            pass

    def _ensure_overlay_widgets(self) -> None:
        """Creates the (hidden) overlay, graph, and log widgets."""
        if getattr(self, "_overlay", None):
            return
        self._overlay = QWidget(self)
        self._overlay.setWindowFlags(Qt.FramelessWindowHint)
        self._overlay.setStyleSheet("background-color: rgba(0, 0, 0, 180);")
        self._overlay.hide()
        self._graph = QWidget(self._overlay)
        self._graph.setAttribute(Qt.WA_NoSystemBackground, True)
        self.live_log = QPlainTextEdit(self._overlay)
        self.live_log.setReadOnly(True)
        self.live_log.setMaximumBlockCount(5000)
        self.live_log.setStyleSheet("""
            QPlainTextEdit {
                color:#25e825; background:#0b141d; border:1px solid #1f3545; border-radius:8px;
                font-family: Consolas, monospace; font-size: 12px;
            }
        """)

        from collections import deque
        for nm in ("_cpu_hist", "_gpu_hist", "_mem_hist", "_iops_hist"):
            if not hasattr(self, nm):
                setattr(self, nm, deque(maxlen=400))
        self._stats_worker = None
        self._overlay.installEventFilter(self)

    def _resize_overlay(self) -> None:
        """Called by the main resizeEvent to resize/mask the overlay."""
        try:
            if getattr(self, "_overlay", None) and self._overlay.isVisible():
                self._overlay.setGeometry(self.rect())
                self._update_overlay_mask()
        except Exception:
            pass

    def _update_overlay_mask(self):
        """Positions graph/log and raises interaction widgets above the overlay."""
        try:
            if not getattr(self, "_overlay", None) or not self._overlay.isVisible():
                return
            main_rect = self.rect()
            self._overlay.setGeometry(main_rect)
            self._overlay.raise_()
            mask_region = QRegion(main_rect)
            for w_name in ["btn_merge", "btn_cancel_merge"]:
                w = getattr(self, w_name, None)
                if w and w.isVisible():
                    tl = w.mapToGlobal(w.rect().topLeft())
                    local_tl = self._overlay.mapFromGlobal(tl)
                    w_rect = QRect(local_tl, w.size())
                    mask_region = mask_region.subtracted(QRegion(w_rect))
                    w.raise_()
            self._overlay.setMask(mask_region)
            margin_x = 40
            margin_y = 60
            spacing = 20
            avail_w = main_rect.width() - (2 * margin_x)
            avail_h = main_rect.height() - (2 * margin_y)
            if avail_w < 100 or avail_h < 100: 
                return
            graph_h = int(avail_h * 0.65)
            log_h = avail_h - graph_h - spacing
            self._graph.setGeometry(margin_x, margin_y, avail_w, graph_h)
            self.live_log.setGeometry(margin_x, margin_y + graph_h + spacing, avail_w, log_h)
        except Exception:
            pass

    def _show_processing_overlay(self) -> None:
        """Shows the overlay and starts stats/pulse timers."""
        self._ensure_overlay_widgets()
        try:
            self._overlay.setGeometry(self.rect())
            self._overlay.show()
            self._overlay.raise_()
            QTimer.singleShot(0, self._update_overlay_mask)
            if self._stats_worker is None:
                self._stats_worker = StatsWorker(self)
                self._stats_worker.stats_ready.connect(self._on_stats_ready)
                self._stats_worker.start()
            if hasattr(self, "_color_pulse_timer"):
                self._color_pulse_timer.start()
        except Exception:
            pass

    def _hide_processing_overlay(self) -> None:
        """Hides overlay, stops timers, and restores button style."""
        try:
            if self._stats_worker:
                self._stats_worker.stop()
                self._stats_worker = None
            if hasattr(self, "_color_pulse_timer"):
                self._color_pulse_timer.stop()
        except Exception:
            pass
        try:
            if getattr(self, "_overlay", None):
                self._overlay.hide()
        except Exception:
            pass

    def _on_stats_ready(self, cpu, gpu, mem, iops):
        """Callback for stats worker."""
        self._cpu_hist.append(cpu)
        self._gpu_hist.append(gpu)
        self._mem_hist.append(mem)
        self._iops_hist.append(iops)
        if getattr(self, "_graph", None) and self._graph.isVisible():
            self._graph.update()

    def _sample_perf_counters_safe(self):
        pass
