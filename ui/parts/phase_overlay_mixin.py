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

class GpuWorker(QThread):
    """Background worker to poll GPU stats without freezing the Main GUI."""
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
                        gpu_enc  = int(row[1].strip() or 0)
                        gpu = max(0, min(100, max(gpu_core, gpu_enc)))
            except Exception:
                pass
            self.stats_updated.emit(gpu)
            for _ in range(20): 
                if not self._running: break
                time.sleep(0.1)

class PhaseOverlayMixin:
    def _append_live_log(self, line: str) -> None:
        """Buffers log lines and flushes them in batches to save CPU."""
        if not getattr(self, "live_log", None):
            return
        if " | " in line:
            parts = line.split(" | ")
            line = parts[-1].strip()
        if not hasattr(self, "_log_buffer"):
            self._log_buffer = []
        self._log_buffer.append(line)

    def _flush_logs(self):
        """Called by timer to dump buffered logs to UI."""
        if hasattr(self, "_log_buffer") and self._log_buffer and getattr(self, "live_log", None):
            chunk = "\n".join(self._log_buffer)
            self._log_buffer.clear()
            self.live_log.appendPlainText(chunk)
            self.live_log.verticalScrollBar().setValue(
                self.live_log.verticalScrollBar().maximum()
            )

    def _ensure_overlay_widgets(self) -> None:
        """Creates the (hidden) overlay, graph, and log widgets."""
        if getattr(self, "_overlay", None):
            return
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
        self._log_buffer = []
        self._log_flush_timer = QTimer(self)
        self._log_flush_timer.setInterval(250)
        self._log_flush_timer.timeout.connect(self._flush_logs)
        self._log_flush_timer.start()
        self._last_gpu_val = 0
        self._gpu_worker = GpuWorker()
        self._gpu_worker.stats_updated.connect(self._on_gpu_update)
        self._stats_timer = QTimer(self)
        self._stats_timer.setInterval(2000)
        self._stats_timer.timeout.connect(self._sample_perf_counters_safe)
        self._overlay.installEventFilter(self)

    def _on_gpu_update(self, val):
        self._last_gpu_val = val

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
            for w_name in ["process_button", "cancel_button", "progress_bar"]:
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
            for nm in ("_cpu_hist", "_gpu_hist", "_mem_hist", "_iops_hist"):
                if hasattr(self, nm):
                    getattr(self, nm).clear()
            self._overlay.setGeometry(self.rect())
            self._overlay.show()
            self._overlay.raise_()
            QTimer.singleShot(0, self._update_overlay_mask)
            self._sample_perf_counters_safe()
            self._stats_timer.start()
            if hasattr(self, "_gpu_worker") and not self._gpu_worker.isRunning():
                self._gpu_worker.start()
            if not getattr(self, "_color_pulse_timer", None):
                self._color_pulse_timer = QTimer(self)
                self._color_pulse_timer.setInterval(100)
                self._color_pulse_timer.timeout.connect(self._pulse_button_color)
            self._color_pulse_timer.start()
        except Exception:
            pass

    def _hide_processing_overlay(self) -> None:
        """Hides overlay, stops timers, and restores button style."""
        try:
            if getattr(self, "_stats_timer", None):
                self._stats_timer.stop()
            if hasattr(self, "_gpu_worker") and self._gpu_worker.isRunning():
                self._gpu_worker.stop()
        except Exception:
            pass
        try:
            if getattr(self, "_color_pulse_timer", None):
                self._color_pulse_timer.stop()
            if hasattr(self, "process_button") and hasattr(self, "_original_process_btn_style"):
                self.process_button.setText("Process Video")
                self.process_button.setStyleSheet(self._original_process_btn_style)
                self.process_button.setIcon(QIcon())
        except Exception:
            pass
        try:
            if getattr(self, "_overlay", None):
                self._overlay.hide()
        except Exception:
            pass

    def _pulse_button_color(self):
        try:
            if not getattr(self, "is_processing", False):
                if getattr(self, "_color_pulse_timer", None):
                    self._color_pulse_timer.stop()
                return
            self._pulse_phase = (getattr(self, "_pulse_phase", 0) + 1) % 20
            t = self._pulse_phase / 20.0
            k = (math.sin(4 * math.pi * t) + 1) / 2
            g1 = (72, 235, 90)
            g2 = (10,  80, 16)
            r = int(g1[0] * k + g2[0] * (1 - k))
            g = int(g1[1] * k + g2[1] * (1 - k))
            b = int(g1[2] * k + g2[2] * (1 - k))
            current_text = self.process_button.text()
            current_icon = self.process_button.icon()
            self.process_button.setStyleSheet(f"""
                QPushButton {{
                    background-color: rgb({r},{g},{b});
                    color: black;
                    font-weight: bold;
                    font-size: 14px;
                    border-radius: 15px;
                    margin-bottom: 6px;
                }}
                QPushButton:hover {{ background-color: #c8f7c5;
                }}
            """)
            self.process_button.setText(current_text)
            self.process_button.setIcon(current_icon)
        except Exception:
            pass

    def _sample_perf_counters_safe(self):
        """Gathers CPU/GPU/etc stats and updates the graph data."""
        try:
            cpu = int(psutil.cpu_percent(interval=None))
        except Exception:
            cpu = 0
        gpu = getattr(self, "_last_gpu_val", 0)
        try:
            mem = int(psutil.virtual_memory().percent)
        except Exception:
            mem = 0
        try:
            now = time.time()
            cur = psutil.disk_io_counters()
            cur_ops = int(getattr(cur, "read_count", 0)) + int(getattr(cur, "write_count", 0))
            prev = getattr(self, "_iops_prev", None)
            if prev is None:
                iops = 0.0
            else:
                dt = max(1e-3, now - prev["ts"])
                iops = max(0.0, (cur_ops - prev["ops"]) / dt)
            self._iops_prev = {"ts": now, "ops": cur_ops}
            dyn = max(1.0, float(getattr(self, "_iops_dyn_max", 1.0)))
            if iops > dyn * 0.98:
                dyn = iops * 1.25
            self._iops_dyn_max = dyn
            iops_pct = int(max(0, min(100, round(100.0 * iops / dyn))))
        except Exception:
            iops_pct = 0
        self._cpu_hist.append(cpu)
        self._gpu_hist.append(gpu)
        self._iops_hist.append(iops_pct)
        self._mem_hist.append(mem)
        if getattr(self, "_overlay", None) and self._overlay.isVisible():
            self._overlay.update()