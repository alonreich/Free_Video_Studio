from PyQt5.QtCore import Qt, QRectF, pyqtSignal, QPropertyAnimation, pyqtProperty, QEasingCurve
from PyQt5.QtGui import QPainter, QColor, QFont, QFontMetrics, QPen, QLinearGradient, QRadialGradient, QPainterPath
from PyQt5.QtWidgets import QWidget
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
        self._anim.setDuration(300)
        self._anim.setEasingCurve(QEasingCurve.OutBack)
        self.setFixedSize(180, 35)
        self.setCursor(Qt.OpenHandCursor)
        self.setEnabled(False)
        self._is_dragging = False
        self._last_mouse_x = 0
        self._drag_velocity = 0
        self._overscroll = 0.08

    def _clamp_rotation(self, val: float) -> float:
        lo = self._range[0] - self._overscroll
        hi = self._range[1] + self._overscroll
        return max(lo, min(hi, float(val)))
    @pyqtProperty(float)
    def rotation(self): return self._rotation
    @rotation.setter
    def rotation(self, val):
        self._rotation = self._clamp_rotation(val)
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
        sensitivity = 0.013
        new_rot = self._rotation - (dx * sensitivity)
        self._rotation = self._clamp_rotation(new_rot)
        self.update()

    def mouseReleaseEvent(self, event):
        if not self._is_dragging: return
        self._is_dragging = False
        self.setCursor(Qt.OpenHandCursor)
        target = int(round(max(self._range[0], min(self._range[1], self._rotation))))
        self.setValue(target)

    def paintEvent(self, event):
        p = QPainter(self)
        p.setRenderHint(QPainter.Antialiasing)
        w, h = self.width(), self.height()
        cx, cy = w / 2, h / 2
        path = QPainterPath()
        curve_depth = 5
        shadow_path = QPainterPath()
        shadow_offset = 2.0
        shadow_path.moveTo(0, curve_depth + shadow_offset)
        shadow_path.quadTo(cx, -curve_depth + shadow_offset, w, curve_depth + shadow_offset)
        shadow_path.lineTo(w, h - curve_depth + shadow_offset)
        shadow_path.quadTo(cx, h + curve_depth + shadow_offset, 0, h - curve_depth + shadow_offset)
        shadow_path.closeSubpath()
        shadow_grad = QLinearGradient(0, 0, 0, h + shadow_offset)
        shadow_grad.setColorAt(0.0, QColor(0, 0, 0, 0))
        shadow_grad.setColorAt(0.55, QColor(0, 0, 0, 18))
        shadow_grad.setColorAt(1.0, QColor(0, 0, 0, 72))
        p.fillPath(shadow_path, shadow_grad)
        path.moveTo(0, curve_depth)
        path.quadTo(cx, -curve_depth, w, curve_depth)
        path.lineTo(w, h - curve_depth)
        path.quadTo(cx, h + curve_depth, 0, h - curve_depth)
        path.closeSubpath()
        bg_grad = QLinearGradient(0, 0, 0, h)
        bg_grad.setColorAt(0.0, QColor("#080c09"))
        bg_grad.setColorAt(0.18, QColor("#132019"))
        bg_grad.setColorAt(0.42, QColor("#2f473b"))
        bg_grad.setColorAt(0.52, QColor("#5e7668"))
        bg_grad.setColorAt(0.78, QColor("#1b2c24"))
        bg_grad.setColorAt(1.0, QColor("#060a07"))
        p.setBrush(bg_grad)
        p.setPen(QPen(QColor("#0a0d10"), 1))
        p.drawPath(path)
        p.save()
        p.setClipPath(path)
        bulge_grad = QLinearGradient(0, 0, 0, h)
        bulge_grad.setColorAt(0.00, QColor(0, 0, 0, 96))
        bulge_grad.setColorAt(0.22, QColor(22, 42, 33, 50))
        bulge_grad.setColorAt(0.48, QColor(162, 190, 173, 56))
        bulge_grad.setColorAt(0.70, QColor(16, 33, 27, 44))
        bulge_grad.setColorAt(1.00, QColor(0, 0, 0, 108))
        p.fillRect(self.rect(), bulge_grad)
        hotspot = QRadialGradient(cx, h * 0.5, w * 0.58, cx, h * 0.5)
        hotspot.setColorAt(0.0, QColor(184, 210, 193, 34))
        hotspot.setColorAt(0.55, QColor(78, 114, 94, 14))
        hotspot.setColorAt(1.0, QColor(255, 255, 255, 0))
        p.fillRect(self.rect(), hotspot)
        depth_vignette = QLinearGradient(0, 0, w, 0)
        depth_vignette.setColorAt(0.00, QColor(0, 0, 0, 40))
        depth_vignette.setColorAt(0.20, QColor(0, 0, 0, 0))
        depth_vignette.setColorAt(0.80, QColor(0, 0, 0, 0))
        depth_vignette.setColorAt(1.00, QColor(0, 0, 0, 40))
        p.fillRect(self.rect(), depth_vignette)
        p.restore()
        inner_rect = QRectF(2, 2, w - 4, h - 4)
        inner_path = QPainterPath()
        inner_path.addRoundedRect(inner_rect, 10, 10)
        p.setPen(QPen(QColor(190, 212, 198, 24), 1))
        p.drawPath(inner_path)
        gloss_path = QPainterPath()
        gloss_path.moveTo(5, curve_depth + 2)
        gloss_path.quadTo(cx, 0, w-5, curve_depth + 2)
        p.setPen(QPen(QColor(196, 220, 204, 60), 1))
        p.drawPath(gloss_path)
        rim_shadow_path = QPainterPath()
        rim_shadow_path.moveTo(5, h - curve_depth - 2)
        rim_shadow_path.quadTo(cx, h + 1, w - 5, h - curve_depth - 2)
        p.setPen(QPen(QColor(0, 0, 0, 120), 2))
        p.drawPath(rim_shadow_path)
        center_band = QRectF(4, h * 0.35, w - 8, h * 0.30)
        band_grad = QLinearGradient(0, center_band.top(), 0, center_band.bottom())
        band_grad.setColorAt(0.0, QColor(148, 176, 159, 34))
        band_grad.setColorAt(0.55, QColor(86, 120, 101, 14))
        band_grad.setColorAt(1.0, QColor(0, 0, 0, 52))
        p.fillRect(center_band, band_grad)
        for i in range(5):
            angle = (i - self._rotation) * (math.pi / 4)
            if abs(angle) > math.pi / 1.75: continue
            opacity = math.cos(angle)
            if opacity < 0: continue
            x_pos = cx + math.sin(angle) * (w * 0.62)
            y_shift = (1.0 - opacity) * 3
            scale = 0.68 + (0.34 * opacity)
            f = QFont("Segoe UI", int(10 * scale), QFont.DemiBold)
            p.setFont(f)
            fm = QFontMetrics(f)
            txt = self._labels[i]
            tw, th = fm.horizontalAdvance(txt), fm.height()
            color = QColor("#2f9fff" if i == self._value else "#c6ccd1")
            if not self.isEnabled():
                color = QColor("#8a949d")
            alpha = int(255 * (opacity ** 2.15))
            color.setAlpha(alpha)
            if i == self._value and self.isEnabled():
                glow = QColor(124, 196, 255, min(120, alpha))
                p.setPen(QPen(glow, 2))
                p.drawText(int(x_pos - tw/2), int(cy + th/3 + y_shift), txt)
            p.setPen(color)
            p.drawText(int(x_pos - tw/2), int(cy + th/3 + y_shift), txt)
        if self.isEnabled():
            p.setPen(QPen(QColor("#2f9fff"), 2))
            p.drawLine(int(cx), 2, int(cx), 7)
            p.drawLine(int(cx), h-7, int(cx), h-2)
        side_fade = QLinearGradient(0, 0, w, 0)
        side_fade.setColorAt(0.0, QColor(10, 12, 15, 80))
        side_fade.setColorAt(0.12, QColor(10, 12, 15, 0))
        side_fade.setColorAt(0.88, QColor(10, 12, 15, 0))
        side_fade.setColorAt(1.0, QColor(10, 12, 15, 80))
        p.fillRect(self.rect(), side_fade)
