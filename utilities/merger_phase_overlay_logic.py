import math
import psutil
import time
from PyQt5.QtCore import Qt, QRect
from PyQt5.QtGui import QRegion, QIcon, QColor, QPainter, QPen, QBrush, QFont

class MergerPhaseOverlayLogic:
    def _resize_overlay(self) -> None:
        try:
            if getattr(self, "_overlay", None) and self._overlay.isVisible():
                self._overlay.setGeometry(self.rect())
                self._update_overlay_mask()
        except Exception: pass

    def _update_overlay_mask(self):
        try:
            if not getattr(self, "_overlay", None) or not self._overlay.isVisible(): return
            main_rect = self.rect()
            self._overlay.setGeometry(main_rect)
            self._overlay.raise_()
            mask_region = QRegion(main_rect)
            for w_name in ["btn_merge", "btn_cancel_merge", "progress_bar"]:
                w = getattr(self, w_name, None)
                if w and w.isVisible():
                    tl = w.mapToGlobal(w.rect().topLeft())
                    local_tl = self._overlay.mapFromGlobal(tl)
                    mask_region = mask_region.subtracted(QRegion(QRect(local_tl, w.size())))
                    w.raise_()
            self._overlay.setMask(mask_region)
            margin_x, margin_y = 40, 40
            avail_w, avail_h = main_rect.width() - (2 * margin_x), main_rect.height() - (2 * margin_y)
            if avail_w < 100 or avail_h < 100: return
            graph_h = 240
            self._graph.setGeometry(margin_x, margin_y, avail_w, graph_h)
            self.live_log.setGeometry(margin_x, margin_y + graph_h + 20, avail_w, avail_h - graph_h - 20)
        except Exception: pass

    def _show_processing_overlay(self) -> None:
        self._ensure_overlay_widgets()
        try:
            for nm in ("_cpu_hist", "_gpu_hist", "_mem_hist", "_iops_hist"): getattr(self, nm).clear()
            self._overlay.setGeometry(self.rect()); self._overlay.show(); self._overlay.raise_()
            self._update_overlay_mask(); self._sample_perf_counters_safe(); self._stats_timer.start()
            if not self._gpu_worker.isRunning(): self._gpu_worker.start()
            if not hasattr(self, "_color_pulse_timer"):
                self._color_pulse_timer = QTimer(self); self._color_pulse_timer.setInterval(100)
                self._color_pulse_timer.timeout.connect(self._pulse_button_color)
            self._color_pulse_timer.start()
        except Exception: pass

    def _hide_processing_overlay(self) -> None:
        try:
            self._stats_timer.stop()
            if self._gpu_worker.isRunning(): self._gpu_worker.stop()
            if hasattr(self, "_color_pulse_timer"): self._color_pulse_timer.stop()
            if hasattr(self, "btn_merge"):
                self.btn_merge.setStyleSheet(self._original_merge_btn_style)
            if getattr(self, "_overlay", None): self._overlay.hide()
        except Exception: pass

    def _pulse_button_color(self):
        try:
            if not self.is_processing:
                if hasattr(self, "_color_pulse_timer"): self._color_pulse_timer.stop()
                return
            self._pulse_phase = (getattr(self, "_pulse_phase", 0) + 1) % 20
            t = self._pulse_phase / 20.0
            k = (math.sin(4 * math.pi * t) + 1) / 2
            g1, g2 = (72, 235, 90), (10, 80, 16)
            r, g, b = [int(g1[i]*k + g2[i]*(1-k)) for i in range(3)]
            self.btn_cancel_merge.setStyleSheet(f"QPushButton {{ background-color: rgb({r},{g},{b}); color: black; font-weight: bold; font-size: 14px; border-radius: 15px; margin-bottom: 6px; }}")
        except Exception: pass

    def _sample_perf_counters_safe(self):
        try:
            cpu = int(psutil.cpu_percent(interval=None))
            gpu = getattr(self, "_last_gpu_val", 0)
            mem = int(psutil.virtual_memory().percent)
            now = time.time(); cur = psutil.disk_io_counters()
            cur_ops = int(getattr(cur, "read_count", 0)) + int(getattr(cur, "write_count", 0))
            prev = getattr(self, "_iops_prev", None)
            dt = max(1e-3, now - prev["ts"]) if prev else 1.0
            iops = max(0.0, (cur_ops - prev["ops"]) / dt) if prev else 0.0
            self._iops_prev = {"ts": now, "ops": cur_ops}
            dyn = max(1.0, float(getattr(self, "_iops_dyn_max", 1.0)))
            if iops > dyn * 0.98: dyn = iops * 1.25
            self._iops_dyn_max = dyn
            iops_pct = int(max(0, min(100, round(100.0 * iops / dyn))))
            self._cpu_hist.append(cpu); self._gpu_hist.append(gpu); self._iops_hist.append(iops_pct); self._mem_hist.append(mem)
            if getattr(self, "_overlay", None) and self._overlay.isVisible(): self._graph.update()
        except Exception: pass
