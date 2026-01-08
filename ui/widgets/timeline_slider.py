from PyQt5.QtCore import Qt, QPoint
from PyQt5.QtGui import QPainter, QFontMetrics, QPen, QColor
from PyQt5.QtWidgets import QSlider, QToolTip

class TimelineSlider(QSlider):

    def __init__(self, orientation=Qt.Horizontal, parent=None):
        super().__init__(orientation, parent)
        self._duration_ms = 0
        self.setMouseTracking(True)

    def set_duration_ms(self, ms: int):
        self._duration_ms = max(0, int(ms))
        self.update()

    def _fmt(self, ms: int) -> str:
        s = max(0, ms // 1000)
        h, s = divmod(s, 3600)
        m, s = divmod(s, 60)
        if h:
            return f"{h:d}:{m:02d}:{s:02d}"
        return f"{m:d}:{s:02d}"

    def mouseMoveEvent(self, e):
        super().mouseMoveEvent(e)
        if self._duration_ms <= 0 or self.maximum() <= 0:
            return
        x = e.pos().x()
        val = self.minimum() + (self.maximum() - self.minimum()) * max(0, min(x, self.width())) / max(1, self.width())
        ms = int((val / max(1, self.maximum())) * self._duration_ms)
        QToolTip.showText(self.mapToGlobal(e.pos()), self._fmt(ms), self)

    def paintEvent(self, e):
        super().paintEvent(e)
        if self._duration_ms <= 0 or self.maximum() <= 0:
            return
        p = QPainter(self)
        try:
            fm = QFontMetrics(self.font())
            w = self.width()
            h = self.height()
            groove_y = max(8, h - (fm.ascent() + 14))
            dur_s = self._duration_ms / 1000.0
            if dur_s >= 3600:
                major, minor = 600, 120
            elif dur_s >= 1200:
                major, minor = 300, 60
            elif dur_s >= 300:
                major, minor = 60, 10
            elif dur_s >= 60:
                major, minor = 10, 5
            else:
                major, minor = 5, 1
            maxv = self.maximum()

            def x_for(sec):
                val = (sec * 1000) / self._duration_ms * maxv
                return int(val / max(1, maxv) * w)
            p.setPen(QPen(QColor(200, 210, 220, 170), 1))
            sec = 0
            while sec <= int(dur_s):
                x = x_for(sec)
                p.drawLine(x, groove_y, x, groove_y + 4)
                sec += minor
            p.setPen(QPen(QColor(235, 242, 250), 2))
            sec = 0
            last_label_right = -9999
            while sec <= int(dur_s):
                x = x_for(sec)
                p.drawLine(x, groove_y, x, groove_y + 8)
                label = self._fmt(sec * 1000)
                tw = fm.width(label)
                tx = x - tw // 2
                if tx > last_label_right + 6 and 0 <= tx <= w - tw:
                    p.drawText(QPoint(tx, groove_y + 10 + fm.ascent()), label)
                    last_label_right = tx + tw
                sec += major
        finally:
            if p.isActive():
                p.end()