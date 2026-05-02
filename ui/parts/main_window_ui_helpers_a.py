import os, sys, time, threading, logging, subprocess, traceback
from PyQt5.QtCore import *
from PyQt5.QtGui import *
from PyQt5.QtWidgets import *

class MainWindowUiHelpersAMixin:
    def set_style(self):
        self.setStyleSheet('''
            QWidget {
                background-color: #2c3e50;
                color: #ecf0f1;
                font-family: "Helvetica Neue", Arial, sans-serif;
            }
            QLabel {
                font-size: 12px;
                padding: 5px;
            }
            QFrame#dropArea {
                border: 3px dashed #266b89;
                border-radius: 10px;
                background-color: #34495e;
                padding: 20px;
            }
            QSpinBox, QDoubleSpinBox {
                background-color: #4a667a;
                border: 1px solid #266b89;
                border-radius: 5px;
                padding: 10px;
                color: #ecf0f1;
                font-size: 13px;
            }
            QPushButton {
                background-color: #266b89;
                color: #ffffff;
                border-style: solid;
                border-top: 1px solid rgba(255, 255, 255, 0.2);
                border-left: 1px solid rgba(255, 255, 255, 0.2);
                border-bottom: 1px solid rgba(0, 0, 0, 0.6);
                border-right: 1px solid rgba(0, 0, 0, 0.6);
                padding: 10px 18px;
                border-radius: 8px;
                font-weight: bold;
            }
            QPushButton:hover:!disabled {
                border: 2px solid #7DD3FC;
            }
            QPushButton:pressed:!disabled {
                border-top: 1px solid rgba(0, 0, 0, 0.7);
                border-left: 1px solid rgba(0, 0, 0, 0.7);
                border-bottom: 1px solid rgba(255, 255, 255, 0.1);
                border-right: 1px solid rgba(255, 255, 255, 0.1);
                padding-top: 11px;
                padding-left: 19px;
                padding-bottom: 9px;
                padding-right: 17px;
            }
            QPushButton#DoneButton { background-color: #e74c3c; }
            QProgressBar { border: 1px solid #266b89; border-radius: 5px; text-align: center; height: 18px; }
            QProgressBar::chunk { background-color: #2ecc71; }
            QToolTip {
                font-family: Arial;
                font-size: 13pt;
                font-weight: normal;
                border: 1px solid #ecf0f1;
                background-color: #34495e;
                color: #ecf0f1;
                padding: 5px;
            }
        ''')

    def refresh_ui_styles(self):
        try:
            self.set_style()
            self.style().unpolish(self)
            self.style().polish(self)
            for widget in self.findChildren(QWidget):
                widget.style().unpolish(widget)
                widget.style().polish(widget)
            self.update()
        except Exception:
            pass

    def _init_upload_hint_blink(self):
        if not hasattr(self, 'hint_group_container') or self.hint_group_container is None:
            return

        from PyQt5.QtWidgets import QGraphicsOpacityEffect
        import math
        if not hasattr(self, '_hint_opacity_effect'):
            self._hint_opacity_effect = QGraphicsOpacityEffect(self.hint_group_container)
            self._hint_opacity_effect.setOpacity(1.0)
            self.hint_group_container.setGraphicsEffect(self._hint_opacity_effect)
        if not hasattr(self, '_hint_pulse_timer'):
            self._hint_pulse_timer = QTimer(self)
            self._hint_pulse_timer.setInterval(20) 
            self._hint_pulse_start_time = time.time()

            def _do_pulse():
                try:
                    elapsed = (time.time() - self._hint_pulse_start_time) % 4.0
                    norm_val = (math.cos((elapsed / 4.0) * 2 * math.pi) + 1) / 2.0
                    opacity = 0.01 + (norm_val * 0.99)
                    self._hint_opacity_effect.setOpacity(opacity)
                except: pass
            self._hint_pulse_timer.timeout.connect(_do_pulse)
        if not self._hint_pulse_timer.isActive():
            self._hint_pulse_timer.start()
            self._hint_pulse_start_time = time.time()