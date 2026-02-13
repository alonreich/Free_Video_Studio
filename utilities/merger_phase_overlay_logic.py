import math
import psutil
import time
from PyQt5.QtCore import Qt, QRect, QTimer
from PyQt5.QtGui import QRegion, QIcon, QColor, QPainter, QPen, QBrush, QFont
from PyQt5.QtWidgets import QWidget

class MergerPhaseOverlayLogic:
    def _resize_overlay(self) -> None:
        """Called by the main resizeEvent to resize/mask the overlay."""
        try:
            if getattr(self, "_overlay", None) and self._overlay.isVisible():
                parent = self._overlay.parentWidget()
                if parent:
                    self._overlay.setGeometry(parent.rect())
                else:
                    self._overlay.setGeometry(self.rect())
                self._update_overlay_mask()
        except Exception:
            pass

    def _update_overlay_mask(self):
        """Positions graph/log and raises interaction widgets above the overlay."""
        try:
            if not getattr(self, "_overlay", None) or not self._overlay.isVisible():
                return
            parent = self._overlay.parentWidget()
            main_rect = parent.rect() if parent else self.rect()
            self._overlay.setGeometry(main_rect)
            self._overlay.raise_()
            mask_region = QRegion(main_rect)
            for w_name in ["btn_merge", "btn_cancel_merge", "progress_bar"]:
                w = getattr(self, w_name, None)
                if w and w.isVisible():
                    tl = w.mapTo(self.centralWidget() if hasattr(self, 'centralWidget') else self, QPoint(0,0))
                    w_rect = QRect(tl, w.size())
                    mask_region = mask_region.subtracted(QRegion(w_rect))
                    w.raise_()
            self._overlay.setMask(mask_region)
            margin_x = 40
            margin_y = 40
            avail_w = main_rect.width() - (2 * margin_x)
            avail_h = main_rect.height() - (2 * margin_y)
            if avail_w < 100 or avail_h < 100: return
            graph_h = 240 
            self._graph.setGeometry(margin_x, margin_y, avail_w, graph_h)
            self.live_log.setGeometry(margin_x, margin_y + graph_h + 20, avail_w, avail_h - graph_h - 20)
        except Exception:
            pass

    def _show_processing_overlay(self) -> None:
        """Shows the overlay and starts stats/pulse timers."""
        self._ensure_overlay_widgets()
        try:
            for nm in ("_cpu_hist", "_gpu_hist", "_mem_hist", "_iops_hist"):
                if hasattr(self, nm):
                    getattr(self, nm).clear()
            parent = self._overlay.parentWidget()
            self._overlay.setGeometry(parent.rect() if parent else self.rect())
            self._overlay.show()
            self._overlay.raise_()
            self._update_overlay_mask()
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
            if hasattr(self, "btn_merge") and hasattr(self, "_original_merge_btn_style"):
                self.btn_merge.setStyleSheet(self._original_merge_btn_style)
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
            g1, g2 = (72, 235, 90), (10, 80, 16)
            r = int(g1[0] * k + g2[0] * (1 - k))
            g = int(g1[1] * k + g2[1] * (1 - k))
            b = int(g1[2] * k + g2[2] * (1 - k))
            self.btn_cancel_merge.setStyleSheet(f"""
                QPushButton {{
                    background-color: rgb({r},{g},{b});
                    color: black;
                    font-weight: bold;
                    font-size: 14px;
                    border-radius: 15px;
                    margin-bottom: 6px;
                }}
                QPushButton:hover {{ background-color: #c8f7c5; }}
            """)
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
            if hasattr(self, "_graph"):
                self._graph.update()
