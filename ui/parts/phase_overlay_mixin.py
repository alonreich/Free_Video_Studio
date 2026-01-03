import math
import shutil
import subprocess
import sys
import time
from collections import deque
import psutil
from PyQt5.QtCore import Qt, QTimer, QRect
from PyQt5.QtGui import QRegion, QIcon
from PyQt5.QtWidgets import QWidget, QPlainTextEdit
_ENABLE_SAFE_GPU_STATS = shutil.which("nvidia-smi") is not None

class PhaseOverlayMixin:

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
        self._overlay.setWindowFlags(Qt.SubWindow | Qt.FramelessWindowHint)
        self._overlay.setAttribute(Qt.WA_NoSystemBackground, True)
        self._overlay.setAttribute(Qt.WA_TransparentForMouseEvents, False)
        self._overlay.hide()
        self._graph = QWidget(self._overlay)
        self._graph.setFixedSize(946, 364)
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
        self._stats_timer = QTimer(self)
        self._stats_timer.setInterval(500)
        self._stats_timer.timeout.connect(self._sample_perf_counters_safe)
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
        """Positions graph/log and punches holes for button/progress bar."""
        try:
            if not getattr(self, "_overlay", None) or not self._overlay.isVisible():
                return
            self._overlay.setGeometry(self.rect())
            if not hasattr(self, "video_frame"):
                return
            vf_topleft_global = self.video_frame.mapToGlobal(self.video_frame.rect().topLeft())
            vf_topleft_local  = self._overlay.mapFromGlobal(vf_topleft_global)
            vf_geom = QRect(vf_topleft_local, self.video_frame.size())
            graph_h_ratio = 0.65
            log_h_ratio   = 0.35
            graph_h = int(vf_geom.height() * graph_h_ratio)
            log_h   = int(vf_geom.height() * log_h_ratio)
            spacing = 18
            if graph_h > 0 and log_h > 0:
                graph_h = int((vf_geom.height() - spacing) * graph_h_ratio)
                log_h   = vf_geom.height() - graph_h - spacing
            else:
                spacing = 0
            self._graph.setGeometry(vf_geom.x(), vf_geom.y(), vf_geom.width(), graph_h)
            self.live_log.setGeometry(vf_geom.x(), vf_geom.y() + graph_h + spacing, vf_geom.width(), log_h)
            r = QRegion(self._overlay.rect())

            def _hole_for_safe(w):
                if not w or not isinstance(w, QWidget) or not w.isVisible():
                    return None
                top_left_global = w.mapToGlobal(w.rect().topLeft())
                top_left_on_overlay = self._overlay.mapFromGlobal(top_left_global)
                gr = QRect(top_left_on_overlay, w.rect().size())
                gr = gr.adjusted(-6, -6, 6, 6)
                if not gr.intersects(self._overlay.rect()):
                    return None
                return QRegion(gr)
            for w in (getattr(self, "process_button", None), getattr(self, "cancel_button", None), getattr(self, "progress_bar", None)):
                q = _hole_for_safe(w)
                if q is not None:
                    r -= q
            self._overlay.setMask(r)
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
            self._sample_perf_counters_safe()
            self._stats_timer.start()
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
                self._overlay.setMask(QRegion())
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
                    font-size: 16px;
                    border-radius: 15px;
                    margin-bottom: 6px;
                }}
                QPushButton:hover {{ background-color: #c8f7c5; }}
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