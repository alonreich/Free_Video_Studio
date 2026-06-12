import os, sys, time, threading, logging, subprocess, traceback
from PyQt5.QtCore import *
from PyQt5.QtGui import *
from PyQt5.QtWidgets import *

class MainWindowUiHelpersAMixin:
    def set_style(self):
        try:
            from ui.styles import UIStyles
            self.setStyleSheet(UIStyles.GLOBAL_STYLE)
        except Exception as e:
            print(f"Style Load Error: {e}")

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
        from PyQt5.QtCore import QPropertyAnimation, QAbstractAnimation
        if not hasattr(self, '_hint_opacity_effect'):
            self._hint_opacity_effect = QGraphicsOpacityEffect(self.hint_group_container)
            self._hint_opacity_effect.setOpacity(1.0)
            self.hint_group_container.setGraphicsEffect(self._hint_opacity_effect)
        if not hasattr(self, '_hint_anim'):
            self._hint_anim = QPropertyAnimation(self._hint_opacity_effect, b"opacity")
            self._hint_anim.setDuration(1500)
            self._hint_anim.setStartValue(1.0)
            self._hint_anim.setEndValue(0.2)
            self._hint_anim.setLoopCount(-1)
            self._hint_anim.setDirection(QAbstractAnimation.Backward)
        timer_active = None
        if getattr(self, '_upload_hint_active', False) and (not callable(timer_active) or not timer_active()):
            if self._hint_anim.state() != QAbstractAnimation.Running:
                self._hint_anim.start()
        else:
            self._hint_anim.stop()
            self._hint_opacity_effect.setOpacity(1.0)
