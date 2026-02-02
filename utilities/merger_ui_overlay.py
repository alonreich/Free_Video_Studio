from PyQt5.QtCore import Qt, QTimer
from PyQt5.QtWidgets import QWidget
import math

class MergerUIOverlayMixin:
    def _ensure_processing_overlay(self):
        if hasattr(self.parent, "_overlay"):
            return
        self.parent._overlay = QWidget(self.parent)
        self.parent._overlay.setWindowFlags(Qt.SubWindow | Qt.FramelessWindowHint)
        self.parent._overlay.setAttribute(Qt.WA_NoSystemBackground, True)
        self.parent._overlay.setAttribute(Qt.WA_TransparentForMouseEvents, False)
        self.parent._overlay.setStyleSheet("background: rgba(0,0,0,180);")
        self.parent._overlay.hide()

    def _show_processing_overlay(self):
        self._ensure_processing_overlay()
        self.parent._overlay.setGeometry(self.parent.rect())
        self.parent._overlay.show()
        self.parent._overlay.raise_()
        if not hasattr(self.parent, "_pulse_timer"):
            self.parent._pulse_timer = QTimer(self.parent)
            self.parent._pulse_timer.setInterval(50)
            self.parent._pulse_timer.timeout.connect(self._pulse_merge_btn)
        self.parent._pulse_phase = 0
        self.parent._pulse_timer.start()

    def _hide_processing_overlay(self):
        if hasattr(self.parent, "_pulse_timer"):
            self.parent._pulse_timer.stop()
        if hasattr(self.parent, "_overlay"):
            self.parent._overlay.hide()
        self.parent.btn_merge.setText("Merge Videos")
        self.parent.btn_merge.setStyleSheet("")
        self.parent.btn_merge.setObjectName("mergeButton")

    def _pulse_merge_btn(self):
        self.parent._pulse_phase = (getattr(self.parent, "_pulse_phase", 0) + 1)
        t = self.parent._pulse_phase / 20.0
        k = (math.sin(4 * math.pi * t) + 1) / 2
        r = int(39 * k + 30 * (1 - k))
        g = int(174 * k + 100 * (1 - k))
        b = int(96 * k + 60 * (1 - k))
        self.parent.btn_merge.setStyleSheet(
            f"""
            QPushButton#mergeButton {{
                background-color: rgb({r},{g},{b});
                color: white;
                border: 2px solid #2ecc71;
            }}
            """
        )
