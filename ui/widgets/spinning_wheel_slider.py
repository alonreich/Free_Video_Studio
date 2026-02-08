from PyQt5.QtCore import Qt, QRect, pyqtSignal, QPropertyAnimation, pyqtProperty, QEasingCurve, QPointF, QTimer
from PyQt5.QtGui import QPainter, QColor, QFont, QFontMetrics, QPen, QLinearGradient, QBrush, QPainterPath
from PyQt5.QtWidgets import QWidget, QSizePolicy
import math

class SpinningWheelSlider(QWidget):
    valueChanged = pyqtSignal(int)

    def __init__(self, parent=None):
        super().__init__(parent)
        self._value = 2
        self._range = (0, 4)
        self._labels = ["BAD", "OKAY", "STD", "GOOD", "MAX"]
        self._rotation = 2.0 
        self._anim = QPropertyAnimation(self, b"rotation")
        self._anim.setDuration(400)
        self._anim.setEasingCurve(QEasingCurve.OutBack)
        self.setFixedSize(180, 35)
        self.setCursor(Qt.OpenHandCursor)
        self.setEnabled(False)
        self._is_dragging = False
        self._last_mouse_x = 0
        self._drag_velocity = 0
    @pyqtProperty(float)
    def rotation(self): return self._rotation
    @rotation.setter
    def rotation(self, val):
        self._rotation = val
        self.update()

    def setValue(self, val):
        val = max(self._range[0], min(self._range[1], val))
        if val != self._value:
            self._value = val
            self._anim.stop()
            self._anim.setStartValue(self._rotation)
            self._anim.setEndValue(float(val))
            self._anim.start()
            self.valueChanged.emit(val)

    def value(self): return self._value

    def setRange(self, min_val, max_val): self._range = (min_val, max_val)

    def mousePressEvent(self, event):
        if not self.isEnabled(): return
        self._is_dragging = True
        self._last_mouse_x = event.x()
        self._anim.stop()
        self.setCursor(Qt.ClosedHandCursor)

    def mouseMoveEvent(self, event):
        if not self._is_dragging: return
        dx = event.x() - self._last_mouse_x
        self._last_mouse_x = event.x()
        sensitivity = 0.015
        new_rot = self._rotation - (dx * sensitivity)
        self._rotation = max(-0.5, min(4.5, new_rot))
        self.update()

    def mouseReleaseEvent(self, event):
        if not self._is_dragging: return
        self._is_dragging = False
        self.setCursor(Qt.OpenHandCursor)
        target = int(round(max(0, min(4, self._rotation))))
        self.setValue(target)

    def paintEvent(self, event):
        p = QPainter(self)
        p.setRenderHint(QPainter.Antialiasing)
        w, h = self.width(), self.height()
        cx, cy = w / 2, h / 2
        path = QPainterPath()
        curve_depth = 6
        path.moveTo(0, curve_depth)
        path.quadTo(cx, -curve_depth, w, curve_depth)
        path.lineTo(w, h - curve_depth)
        path.quadTo(cx, h + curve_depth, 0, h - curve_depth)
        path.closeSubpath()
        bg_grad = QLinearGradient(0, 0, 0, h)
        bg_grad.setColorAt(0.0, QColor("#121416"))
        bg_grad.setColorAt(0.15, QColor("#2c3e50"))
        bg_grad.setColorAt(0.42, QColor("#fdfdfd"))
        bg_grad.setColorAt(0.58, QColor("#fdfdfd"))
        bg_grad.setColorAt(0.85, QColor("#2c3e50"))
        bg_grad.setColorAt(1.0, QColor("#121416"))
        p.setBrush(bg_grad)
        p.setPen(QPen(QColor("#000"), 1))
        p.drawPath(path)
        gloss_path = QPainterPath()
        gloss_path.moveTo(5, curve_depth + 2)
        gloss_path.quadTo(cx, 0, w-5, curve_depth + 2)
        p.setPen(QPen(QColor(255, 255, 255, 60), 1))
        p.drawPath(gloss_path)
        for i in range(5):
            angle = (i - self._rotation) * (math.pi / 4)
            if abs(angle) > math.pi / 1.8: continue
            opacity = math.cos(angle)
            if opacity < 0: continue
            x_pos = cx + math.sin(angle) * (w * 0.7)
            y_shift = (1.0 - opacity) * 4
            scale = 0.65 + (0.35 * opacity)
            f = QFont("Segoe UI", int(10 * scale), QFont.Bold)
            p.setFont(f)
            fm = QFontMetrics(f)
            txt = self._labels[i]
            tw, th = fm.horizontalAdvance(txt), fm.height()
            color = QColor("#1b6d26" if i == self._value else "#2c3e50")
            if not self.isEnabled(): color = QColor("#7f8c8d")
            alpha = int(255 * (opacity ** 2.5))
            color.setAlpha(alpha)
            p.setPen(color)
            p.drawText(int(x_pos - tw/2), int(cy + th/3 + y_shift), txt)
        if self.isEnabled():
            p.setPen(QPen(QColor("#1b6d26"), 2))
            p.drawLine(int(cx), 0, int(cx), 5)
            p.drawLine(int(cx), h-5, int(cx), h)