from PyQt5.QtCore import Qt, QRect, QPoint, pyqtSignal
from PyQt5.QtGui import QPainter, QColor, QFont, QFontMetrics, QPen, QCursor, QPainterPath, QLinearGradient, QBrush
from PyQt5.QtWidgets import QSlider, QStyleOptionSlider, QStyle, QToolTip, QApplication

class TrimmedSlider(QSlider):
    trim_times_changed = pyqtSignal(float, float)
    music_trim_changed = pyqtSignal(float, float)

    def __init__(self, parent=None):
        print("DEBUG: Timeline __init__ CALLED. If you see this, the object exists.")
        super().__init__(Qt.Horizontal, parent)
        self.music_v_offset = -6
        self.trimmed_start = None
        self.trimmed_end = None
        self.music_start_sec = None
        self.music_end_sec = None
        self._duration_ms = 0
        self.setMouseTracking(True)
        self.setMinimumHeight(40)
        self.setStyleSheet("""
            QSlider::groove:horizontal {
                background: #4a667a;
                height: 4px; 
                border-radius: 2px;
                margin-top: 10px;
                margin-bottom: 10px;
            }
            QSlider::handle:horizontal {
                background: transparent;
                border: none;
                width: 26px;
                margin: -10px 0;
            }
            QSlider::sub-page:horizontal { background: transparent; }
            QSlider::add-page:horizontal { background: transparent; }
        """)
        self.sliderPressed.connect(self._on_pressed)
        self.sliderReleased.connect(self._on_released)
        self._is_pressed = False
        self._show_trim = True
        self._show_music = False
        self._dragging_handle = None
        self._hovering_handle = None
        self._dragging_music_handle = None
        self._hovering_music_handle = None
        self._music_drag_offset_sec = 0

    def set_duration_ms(self, ms: int):
        new_ms = max(0, int(ms))
        if self._duration_ms != new_ms:
            print(f"DEBUG: TrimmedSlider duration updated: {self._duration_ms} -> {new_ms} ms")
            self._duration_ms = new_ms
            self.update()

    def set_music_visible(self, visible: bool):
        self._show_music = visible
        if not visible:
            self.reset_music_times()
        self.update()

    def set_music_times(self, start_sec: float, end_sec: float):
        self.music_start_sec = start_sec
        self.music_end_sec = end_sec
        self.update()

    def reset_music_times(self):
        if self.music_start_sec is not None or self.music_end_sec is not None:
            self.music_start_sec = None
            self.music_end_sec = None
            self.update()

    def get_playhead_center_x(self):
        playhead_rect = self._get_playhead_rect()
        if playhead_rect.isValid():
            return playhead_rect.center().x()
        return -1

    def enable_trim_overlays(self, enabled: bool):
        self._show_trim = bool(enabled)
        self.update()

    def _get_groove_rect(self):
        opt = QStyleOptionSlider()
        self.initStyleOption(opt)
        groove = self.style().subControlRect(QStyle.CC_Slider, opt, QStyle.SC_SliderGroove, self)
        if groove.width() <= 0:
            h = 4
            top = (self.height() - h) // 2
            groove = QRect(8, top, self.width() - 16, h)
        return QRect(groove.left(), groove.center().y() - 2, groove.width(), 4)

    def _map_pos_to_value(self, x_pos):
        groove = self._get_groove_rect()
        if groove.width() <= 0:
            return self.minimum()
        span = max(1, groove.width() - 1)
        pos = x_pos - groove.left()
        pos = max(0, min(pos, span))
        return QStyle.sliderValueFromPosition(self.minimum(), self.maximum(), pos, span)

    def _map_value_to_pos(self, value):
        groove = self._get_groove_rect()
        minv, maxv = self.minimum(), self.maximum()
        if maxv <= minv:
            return groove.left()
        span = max(1, groove.width() - 1)
        ratio = (value - minv) / float(maxv - minv)
        ratio = max(0.0, min(1.0, ratio))
        return int(groove.left() + ratio * span)

    def _get_handle_rect(self, handle_type, time_sec=None):
        if self._duration_ms <= 0: return QRect()
        if time_sec is None:
            time_sec = self.trimmed_start if handle_type == 'start' else self.trimmed_end
        if time_sec is None: return QRect()
        x = self._map_value_to_pos(time_sec * 1000)
        groove = self._get_groove_rect()
        trim_handle_width = 8
        trim_rect_h = groove.height() + 26
        trim_rect_y = groove.center().y() - trim_rect_h // 2
        return QRect(x - (trim_handle_width//2), trim_rect_y, trim_handle_width, trim_rect_h)

    def _get_music_handle_rect(self, handle_type):
        time_sec = self.music_start_sec if handle_type == 'start' else self.music_end_sec
        if time_sec is None or not self._show_music: return QRect()
        x = self._map_value_to_pos(time_sec * 1000)
        handle_size = 30
        y_center = self._get_groove_rect().center().y() + self.music_v_offset
        y_pos = y_center - (handle_size / 2)
        return QRect(x - handle_size // 2, int(y_pos), handle_size, handle_size)

    def _get_playhead_rect(self):
        cx = self._map_value_to_pos(self.value())
        groove = self._get_groove_rect()
        playhead_width = 12
        playhead_height = groove.height() + 26
        playhead_y = groove.center().y() - playhead_height // 2
        return QRect(cx - playhead_width // 2, playhead_y, playhead_width, playhead_height)

    def mousePressEvent(self, e):
        if e.button() == Qt.LeftButton:
            if self._show_music:
                start_music_rect = self._get_music_handle_rect('start')
                end_music_rect = self._get_music_handle_rect('end')
                music_line_rect = self._get_music_line_rect()
                if start_music_rect.contains(e.pos()):
                    self._dragging_music_handle = 'start'
                    self.update()
                    return
                if end_music_rect.contains(e.pos()):
                    self._dragging_music_handle = 'end'
                    self.update()
                    return
                if music_line_rect.contains(e.pos()):
                    self._dragging_music_handle = 'body'
                    click_time_sec = (self._map_pos_to_value(e.pos().x()) / self.maximum()) * (self._duration_ms / 1000.0)
                    self._music_drag_offset_sec = self.music_start_sec - click_time_sec
                    self.update()
                    return
            playhead_rect = self._get_playhead_rect()
            if playhead_rect.contains(e.pos()):
                self._dragging_handle = 'playhead'
                self.update()
                return
            if self._show_trim:
                start_handle_rect = self._get_handle_rect('start')
                end_handle_rect = self._get_handle_rect('end')
                if start_handle_rect.contains(e.pos()):
                    self._dragging_handle = 'start'
                    self.update()
                    return
                elif end_handle_rect.contains(e.pos()):
                    self._dragging_handle = 'end'
                    self.update()
                    return
        if e.button() == Qt.LeftButton and not self._dragging_handle and not self._dragging_music_handle:
            val = self._map_pos_to_value(e.pos().x())
            self.setSliderPosition(val)
            self.sliderMoved.emit(val)
        super().mousePressEvent(e)

    def mouseReleaseEvent(self, e):
        if e.button() == Qt.LeftButton:
            self._dragging_handle = None
            self._dragging_music_handle = None
            self.update()
        super().mouseReleaseEvent(e)

    def _fmt(self, ms: int) -> str:
        s = max(0, ms // 1000)
        h, s = divmod(s, 3600)
        m, s = divmod(s, 60)
        return f"{h:d}:{m:02d}:{s:02d}" if h else f"{m:d}:{s:02d}"

    def mouseMoveEvent(self, e):
        if self._dragging_music_handle:
            new_val = self._map_pos_to_value(e.pos().x())
            new_time_sec = (new_val / self.maximum()) * (self._duration_ms / 1000.0) if self.maximum() > 0 else 0
            video_trim_start = self.trimmed_start if self.trimmed_start is not None else 0.0
            video_trim_end = self.trimmed_end if self.trimmed_end is not None else (self._duration_ms / 1000.0)
            if self._dragging_music_handle == 'body':
                duration = self.music_end_sec - self.music_start_sec
                new_start_sec = new_time_sec + self._music_drag_offset_sec
                new_start_sec = max(video_trim_start, new_start_sec)
                if new_start_sec + duration > video_trim_end:
                    new_start_sec = video_trim_end - duration
                self.music_start_sec = new_start_sec
                self.music_end_sec = self.music_start_sec + duration
            elif self._dragging_music_handle == 'start':
                new_start_sec = new_time_sec
                new_start_sec = max(video_trim_start, new_start_sec)
                self.music_start_sec = min(new_start_sec, self.music_end_sec - 0.1)
            elif self._dragging_music_handle == 'end':
                new_end_sec = new_time_sec
                new_end_sec = min(video_trim_end, new_end_sec)
                self.music_end_sec = max(self.music_start_sec + 0.1, new_end_sec)
            self.music_trim_changed.emit(self.music_start_sec, self.music_end_sec)
            self.update()
            return
        if self._dragging_handle == 'playhead':
            new_val = self._map_pos_to_value(e.pos().x())
            if new_val != self.sliderPosition():
                self.setSliderPosition(new_val)
                self.sliderMoved.emit(new_val)
                self.update()
            return
        elif self._dragging_handle in ('start', 'end'):
            new_val = self._map_pos_to_value(e.pos().x())
            new_time_sec = (new_val / self.maximum()) * (self._duration_ms / 1000.0) if self.maximum() > 0 else 0
            new_start = self.trimmed_start
            new_end = self.trimmed_end
            if self._dragging_handle == 'start':
                new_start = min(new_time_sec, (self.trimmed_end or 0) - 0.01)
                new_start = max(0.0, new_start)
            elif self._dragging_handle == 'end':
                new_end = max(new_time_sec, (self.trimmed_start or 0) + 0.01)
                new_end = min(self._duration_ms / 1000.0, new_end)
            if new_start != self.trimmed_start or new_end != self.trimmed_end:
                self.trimmed_start = new_start
                self.trimmed_end = new_end
                self.trim_times_changed.emit(self.trimmed_start, self.trimmed_end)
            self.update()
            return
        new_hover_handle = None
        new_hover_music_handle = None
        start_music_rect = self._get_music_handle_rect('start')
        end_music_rect = self._get_music_handle_rect('end')
        music_line_rect = self._get_music_line_rect()
        if self._show_music:
            if start_music_rect.contains(e.pos()):
                new_hover_music_handle = 'start'
            elif end_music_rect.contains(e.pos()):
                new_hover_music_handle = 'end'
            elif music_line_rect.contains(e.pos()):
                new_hover_music_handle = 'body'
        if not new_hover_music_handle:
            playhead_rect = self._get_playhead_rect()
            if playhead_rect.contains(e.pos()):
                new_hover_handle = 'playhead'
            elif self._show_trim:
                start_handle_rect = self._get_handle_rect('start')
                end_handle_rect = self._get_handle_rect('end')
                if start_handle_rect.contains(e.pos()):
                    new_hover_handle = 'start'
                elif end_handle_rect.contains(e.pos()):
                    new_hover_handle = 'end'
        if self._hovering_handle != new_hover_handle or self._hovering_music_handle != new_hover_music_handle:
            self._hovering_handle = new_hover_handle
            self._hovering_music_handle = new_hover_music_handle
            self.setCursor(QCursor(Qt.PointingHandCursor) if self._hovering_handle or self._hovering_music_handle else QCursor(Qt.ArrowCursor))
            self.update()
        if self._is_pressed and not self._dragging_handle and not self._dragging_music_handle:
            val = self._map_pos_to_value(e.pos().x())
            if val != self.sliderPosition():
                self.setSliderPosition(val)
                self.sliderMoved.emit(val)
        super().mouseMoveEvent(e)

    def _on_pressed(self):
        if not self._dragging_handle and not self._dragging_music_handle:
            self._is_pressed = True

    def _on_released(self):
        self._is_pressed = False
        self._dragging_handle = None
        self._dragging_music_handle = None

    def set_trim_times(self, start, end):
        self.trimmed_start = start
        self.trimmed_end = end
        self.update()
        self.trim_times_changed.emit(start, end)

    def _get_music_line_rect(self):
        if self.music_start_sec is None or self.music_end_sec is None or not self._show_music:
            return QRect()
        start_x = self._map_value_to_pos(self.music_start_sec * 1000)
        end_x = self._map_value_to_pos(self.music_end_sec * 1000)
        line_height = 12
        y_center = self._get_groove_rect().center().y() + self.music_v_offset
        y_pos = y_center - (line_height / 2)
        return QRect(start_x, int(y_pos), end_x - start_x, line_height)

    def paintEvent(self, event):
        p = QPainter(self)
        try:
            p.setRenderHint(QPainter.Antialiasing)
            p.fillRect(self.rect(), Qt.transparent)
            groove_rect = self._get_groove_rect()
            p.setPen(Qt.NoPen)
            p.setBrush(QColor("#3d3d3d"))
            p.drawRoundedRect(groove_rect, 2, 2)
            if self.trimmed_start is not None and self.trimmed_end is not None and self._duration_ms > 0:
                fill_color = QColor("#59B1D5")
                fill_color.setAlpha(150)
                p.setBrush(fill_color)
                kept_left = self._map_value_to_pos(self.trimmed_start * 1000)
                kept_right = self._map_value_to_pos(self.trimmed_end * 1000)
                if kept_left > kept_right:
                    kept_left, kept_right = kept_right, kept_left
                kept_rect = QRect(kept_left, groove_rect.y(), kept_right - kept_left, groove_rect.height())
                p.drawRect(kept_rect)
            if self._show_music and self.music_start_sec is not None and self.music_end_sec is not None:
                music_color = QColor(255, 105, 180, 77)
                p.setBrush(music_color)
                p.setPen(Qt.NoPen)
                music_rect = self._get_music_line_rect()
                p.drawRect(music_rect)
            f = QFont(self.font())
            f.setPointSize(max(10, f.pointSize()))
            p.setFont(f)
            fm = QFontMetrics(f)
            if self._duration_ms > 0 and groove_rect.width() > 10:
                major_tick_pixels = 120 
                num_major_ticks = max(1, int(round(groove_rect.width() / major_tick_pixels)))
                for i in range(num_major_ticks + 1):
                    ratio = i / float(num_major_ticks)
                    ms = self._duration_ms * ratio
                    x = groove_rect.left() + int(ratio * max(1, groove_rect.width() - 1))
                    is_obscured = False
                    start_handle_rect = self._get_handle_rect('start')
                    end_handle_rect = self._get_handle_rect('end')
                    if start_handle_rect.isValid() and (abs(x - start_handle_rect.center().x()) < (start_handle_rect.width() / 2 + 5) or \
                       abs(x - end_handle_rect.center().x()) < (end_handle_rect.width() / 2 + 5)):
                        is_obscured = True
                    playhead_rect = self._get_playhead_rect()
                    if playhead_rect.isValid() and abs(x - playhead_rect.center().x()) < (playhead_rect.width() / 2 + 5):
                        is_obscured = True
                    if not is_obscured:
                        p.setPen(QColor(180, 180, 180))
                        p.drawLine(x, groove_rect.bottom() + 1, x, groove_rect.bottom() + 6)
                        time_str = self._fmt(int(ms))
                        text_width = fm.horizontalAdvance(time_str)
                        p.drawText(x - text_width // 2, groove_rect.bottom() + 18, time_str)
            else:
                p.setPen(QColor(150, 150, 150))
                p.drawText(groove_rect.left(), groove_rect.bottom() + 18, "0:00")
                p.drawText(groove_rect.right() - 20, groove_rect.bottom() + 18, "0:00")
            for handle_type in ['start', 'end']:
                handle_rect = self._get_handle_rect(handle_type)
                if not handle_rect.isValid(): continue
                color = QColor(0, 0, 0, 150)
                if self._hovering_handle == handle_type or self._dragging_handle == handle_type:
                    color = QColor(230, 126, 34, 200)
                p.setPen(Qt.NoPen)
                p.setBrush(color)
                p.drawRoundedRect(handle_rect, 4, 4)
            playhead_rect = self._get_playhead_rect()
            if playhead_rect.isValid():
                knob_w = 12
                knob_h = 36
                cx = playhead_rect.center().x()
                cy = groove_rect.center().y()
                knob_rect = QRect(cx - knob_w // 2, cy - knob_h // 2, knob_w, knob_h)
                border_color = QColor("#1f2a36")
                if self._hovering_handle == 'playhead' or self._dragging_handle == 'playhead':
                    border_color = QColor("#90A4AE")
                p.setPen(QPen(border_color, 1))
                g = QLinearGradient(knob_rect.left(), knob_rect.top(), knob_rect.left(), knob_rect.bottom())
                c1 = QColor("#546E7A")
                c2 = QColor("#90A4AE")
                if self._hovering_handle == 'playhead' or self._dragging_handle == 'playhead':
                    c1 = c1.lighter(120)
                    c2 = c2.lighter(120)
                g.setColorAt(0.0, c1)
                g.setColorAt(0.4, c1)
                g.setColorAt(0.5, c2)
                g.setColorAt(0.6, c1)
                g.setColorAt(1.0, c1)
                p.setBrush(QBrush(g))
                p.drawRoundedRect(knob_rect, 4, 4)
            if self._show_music:
                for handle_type in ['start', 'end']:
                    handle_rect = self._get_music_handle_rect(handle_type)
                    if not handle_rect.isValid(): continue
                    color = QColor(255, 105, 180, 220)
                    if self._hovering_music_handle == handle_type or self._dragging_music_handle == handle_type:
                        color = QColor(255, 20, 147, 255)
                    p.setPen(color.darker(120))
                    p.setBrush(color)
                    path = QPainterPath()
                    path.addEllipse(handle_rect.x() + handle_rect.width() * 0.1, handle_rect.y() + handle_rect.height() * 0.5, handle_rect.width() * 0.5, handle_rect.height() * 0.5)
                    path.addRect(handle_rect.x() + handle_rect.width() * 0.5, handle_rect.y(), handle_rect.width() * 0.1, handle_rect.height() * 0.8)
                    p.drawPath(path)
        finally:
            if p.isActive():
                p.end()

    def map_value_to_pixel(self, value):
        style = QApplication.style()
        style_option = QStyleOptionSlider()
        self.initStyleOption(style_option)
        return style.sliderPositionFromValue(self.minimum(), self.maximum(), value, self.width())