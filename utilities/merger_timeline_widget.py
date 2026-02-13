import os
import sys
from PyQt5.QtWidgets import QWidget
from PyQt5.QtCore import Qt, QRect, QPoint, pyqtSignal, QRectF
from PyQt5.QtGui import QPainter, QColor, QFont, QPen, QBrush, QPixmap

class MergerTimelineWidget(QWidget):
    clicked_pos = pyqtSignal(float)

    def __init__(self, parent=None):
        super().__init__(parent)
        self.total_duration = 1.0
        self.video_segments = []
        self.music_segments = []
        self.current_time = 0.0
        self.setMinimumHeight(100)
        self.setCursor(Qt.PointingHandCursor)
        self.setMouseTracking(True)

    def set_data(self, total_dur, videos, music):
        self.total_duration = max(0.1, total_dur)
        self.video_segments = videos
        self.music_segments = music
        self.update()

    def set_current_time(self, t):
        self.current_time = max(0.0, min(self.total_duration, t))
        self.update()

    def paintEvent(self, event):
        p = QPainter(self)
        p.setRenderHint(QPainter.Antialiasing)
        p.setRenderHint(QPainter.SmoothPixmapTransform)
        w = float(self.width())
        h = float(self.height())
        lane_h = 50.0
        v_y = 0.0
        m_y = lane_h
        p.fillRect(self.rect(), QColor(15, 25, 35))
        current_x = 0.0
        separator_color = QColor("#7DD3FC")
        for i, seg in enumerate(self.video_segments):
            dur = seg.get("duration", 0)
            seg_w = (dur / self.total_duration) * w
            rect_f = QRectF(current_x, v_y, seg_w, lane_h)
            p.save()
            p.setClipRect(rect_f)
            p.fillRect(rect_f, QColor(45, 65, 85))
            thumbs = seg.get("thumbs", [])
            if thumbs:
                num_thumbs = len(thumbs)
                t_render_w = seg_w / float(num_thumbs)
                for t_idx, thumb in enumerate(thumbs):
                    t_pos_x = current_x + (t_idx * t_render_w)
                    p.drawPixmap(QRectF(t_pos_x, v_y, t_render_w + 0.5, lane_h), thumb, QRectF(thumb.rect()))
            p.restore()
            if i < len(self.video_segments) - 1:
                p.setPen(QPen(separator_color, 3))
                sep_x = int(current_x + seg_w)
                p.drawLine(sep_x, int(v_y), sep_x, int(v_y + lane_h))
            current_x += seg_w
        current_x = 0.0
        for i, seg in enumerate(self.music_segments):
            dur = seg.get("duration", 0)
            seg_w = (dur / self.total_duration) * w
            rect_f = QRectF(current_x, m_y, seg_w, lane_h)
            p.fillRect(rect_f, QColor(10, 20, 10))
            wave = seg.get("wave")
            if wave and not wave.isNull():
                p.drawPixmap(rect_f, wave, QRectF(wave.rect()))
                p.fillRect(rect_f, QColor(0, 150, 0, 40)) 
            p.setPen(QPen(QColor(0, 200, 0, 80), 1))
            cl_y = int(m_y + lane_h/2)
            p.drawLine(int(current_x), cl_y, int(current_x + seg_w), cl_y)
            if i < len(self.music_segments) - 1:
                p.setPen(QPen(separator_color, 3))
                sep_x = int(current_x + seg_w)
                p.drawLine(sep_x, int(m_y), sep_x, int(m_y + lane_h))
            current_x += seg_w
        caret_x = (self.current_time / self.total_duration) * w
        p.setPen(QPen(QColor(52, 152, 219, 100), 6))
        p.drawLine(int(caret_x), 0, int(caret_x), int(h))
        p.setPen(QPen(QColor(255, 255, 255), 2))
        p.drawLine(int(caret_x), 0, int(caret_x), int(h))
        p.setBrush(QColor(52, 152, 219))
        p.setPen(QPen(Qt.white, 1))
        handle_poly = [QPoint(int(caret_x) - 10, 0), QPoint(int(caret_x) + 10, 0), QPoint(int(caret_x), 15)]
        p.drawPolygon(*handle_poly)

    def mousePressEvent(self, event):
        if event.button() == Qt.LeftButton:
            pct = event.pos().x() / float(self.width())
            self.clicked_pos.emit(max(0.0, min(1.0, pct)))

    def mouseMoveEvent(self, event):
        if event.buttons() & Qt.LeftButton:
            pct = event.pos().x() / float(self.width())
            self.clicked_pos.emit(max(0.0, min(1.0, pct)))
