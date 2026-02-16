from PyQt5.QtCore import Qt, QRect, QPoint, pyqtSignal, QSize
from PyQt5.QtGui import QPainter, QColor, QFont, QFontMetrics, QPen, QCursor, QPainterPath, QLinearGradient, QBrush
from PyQt5.QtWidgets import QSlider, QStyleOptionSlider, QStyle, QToolTip, QApplication

class TrimmedSlider(QSlider):
    trim_times_changed = pyqtSignal(int, int)
    music_trim_changed = pyqtSignal(int, int)

    def __init__(self, parent=None):
        super().__init__(Qt.Horizontal, parent)
        self.music_v_offset = -6
        self.trimmed_start_ms = 0
        self.trimmed_end_ms = 0
        self.music_start_ms = 0
        self.music_end_ms = 0
        self._duration_ms = 0
        self.setMouseTracking(True)
        self.setMinimumHeight(40)
        self.sliderPressed.connect(self._on_pressed)
        self.sliderReleased.connect(self._on_released)
        self._is_pressed = False
        self._show_trim = True
        self._show_music = False
        self._dragging_handle = None
        self._hovering_handle = None
        self._dragging_music_handle = None
        self._hovering_music_handle = None
        self._music_drag_offset_ms = 0
        self._cached_font = None
        self._cached_font_metrics = None
        self._cached_tick_info = None
        self._last_paint_size = QSize()

    def set_duration_ms(self, ms: int):
        new_ms = max(0, int(ms))
        if self._duration_ms != new_ms:
            self._duration_ms = new_ms
            self.update()

    def set_music_visible(self, visible: bool):
        self._show_music = visible
        if not visible:
            self.reset_music_times()
        self.update()

    def set_music_times(self, start_ms: int, end_ms: int):
        self.music_start_ms = start_ms
        self.music_end_ms = end_ms
        self.update()

    def reset_music_times(self):
        if self.music_start_ms != 0 or self.music_end_ms != 0:
            self.music_start_ms = 0
            self.music_end_ms = 0
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

    def _get_handle_rect(self, handle_type, time_ms=None):
        if self._duration_ms <= 0: return QRect()
        if time_ms is None:
            time_ms = self.trimmed_start_ms if handle_type == 'start' else self.trimmed_end_ms
        x = self._map_value_to_pos(time_ms)
        groove = self._get_groove_rect()
        trim_handle_width = 8
        trim_rect_h = groove.height() + 26
        trim_rect_y = groove.center().y() - trim_rect_h // 2
        return QRect(x - (trim_handle_width//2), trim_rect_y, trim_handle_width, trim_rect_h)

    def _get_music_handle_rect(self, handle_type):
        time_ms = self.music_start_ms if handle_type == 'start' else self.music_end_ms
        if time_ms <= 0 or not self._show_music: return QRect()
        x = self._map_value_to_pos(time_ms)
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
                    click_time_ms = self._map_pos_to_value(e.pos().x())
                    self._music_drag_offset_ms = self.music_start_ms - click_time_ms
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
            new_val_ms = self._map_pos_to_value(e.pos().x())
            video_trim_start_ms = self.trimmed_start_ms
            video_trim_end_ms = self.trimmed_end_ms if self.trimmed_end_ms > 0 else self._duration_ms
            if self._dragging_music_handle == 'body':
                duration_ms = self.music_end_ms - self.music_start_ms
                new_start_ms = new_val_ms + self._music_drag_offset_ms
                new_start_ms = max(video_trim_start_ms, new_start_ms)
                if new_start_ms + duration_ms > video_trim_end_ms:
                    new_start_ms = video_trim_end_ms - duration_ms
                self.music_start_ms = new_start_ms
                self.music_end_ms = self.music_start_ms + duration_ms
            elif self._dragging_music_handle == 'start':
                new_start_ms = new_val_ms
                new_start_ms = max(video_trim_start_ms, new_start_ms)
                self.music_start_ms = min(new_start_ms, self.music_end_ms - 100)
            elif self._dragging_music_handle == 'end':
                new_end_ms = new_val_ms
                new_end_ms = min(video_trim_end_ms, new_end_ms)
                self.music_end_ms = max(self.music_start_ms + 100, new_end_ms)
            self.music_trim_changed.emit(self.music_start_ms, self.music_end_ms)
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
            new_time_ms = self._map_pos_to_value(e.pos().x())
            new_start = self.trimmed_start_ms
            new_end = self.trimmed_end_ms
            if self._dragging_handle == 'start':
                new_start = min(new_time_ms, self.trimmed_end_ms - 10)
                new_start = max(0, new_start)
            elif self._dragging_handle == 'end':
                new_end = max(new_time_ms, self.trimmed_start_ms + 10)
                new_end = min(self._duration_ms, new_end)
            if new_start != self.trimmed_start_ms or new_end != self.trimmed_end_ms:
                self.trimmed_start_ms = new_start
                self.trimmed_end_ms = new_end
                self.trim_times_changed.emit(self.trimmed_start_ms, self.trimmed_end_ms)
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

    def set_trim_times(self, start_ms, end_ms):
        self.trimmed_start_ms = start_ms
        self.trimmed_end_ms = end_ms
        self.update()
        self.trim_times_changed.emit(start_ms, end_ms)

    def _get_music_line_rect(self):
        if self.music_end_ms <= 0 or not self._show_music:
            return QRect()
        start_x = self._map_value_to_pos(self.music_start_ms)
        end_x = self._map_value_to_pos(self.music_end_ms)
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
            if self.trimmed_start_ms >= 0 and self.trimmed_end_ms > 0 and self._duration_ms > 0:
                fill_color = QColor("#59B1D5")
                fill_color.setAlpha(150)
                p.setBrush(fill_color)
                kept_left = self._map_value_to_pos(self.trimmed_start_ms)
                kept_right = self._map_value_to_pos(self.trimmed_end_ms)
                if kept_left > kept_right: kept_left, kept_right = kept_right, kept_left
                kept_rect = QRect(kept_left, groove_rect.y(), kept_right - kept_left, groove_rect.height())
                p.drawRect(kept_rect)
            if self._show_music and self.music_start_ms >= 0 and self.music_end_ms > 0:
                music_color = QColor(255, 105, 180, 77)
                p.setBrush(music_color)
                p.setPen(Qt.NoPen)
                music_rect = self._get_music_line_rect()
                p.drawRect(music_rect)
            f = QFont(self.font())
            f.setPointSize(max(10, f.pointSize()))
            p.setFont(f)
            fm = QFontMetrics(f)
            is_wizard = (self.property("is_wizard_slider") is True)
            if is_wizard and self._duration_ms > 0 and groove_rect.width() > 10:
                duration_sec = self._duration_ms / 1000.0
                sub_interval = 15
                if duration_sec > 600: sub_interval = 30
                if duration_sec > 1200: sub_interval = 60
                for sec in range(0, int(duration_sec) + 1, sub_interval):
                    ratio = sec / duration_sec
                    x = groove_rect.left() + int(ratio * (groove_rect.width() - 1))
                    is_minute = (sec % 60 == 0)
                    p.setPen(QPen(QColor("#7DD3FC") if is_minute else QColor("#666666"), 1.5 if is_minute else 1))
                    tick_len = 8 if is_minute else 4
                    p.drawLine(x, groove_rect.bottom() + 1, x, groove_rect.bottom() + 1 + tick_len)
                    show_label = is_minute or (duration_sec < 900)
                    if show_label:
                        time_str = self._fmt(sec * 1000)
                        text_width = fm.horizontalAdvance(time_str)
                        p.setPen(QColor("#FFFFFF" if is_minute else "#AAAAAA"))
                        p.drawText(x - text_width // 2, groove_rect.bottom() + 18, time_str)
            elif not is_wizard and self._duration_ms > 0 and groove_rect.width() > 10:
                major_tick_pixels = 120 
                num_major_ticks = max(1, int(round(groove_rect.width() / major_tick_pixels)))
                for i in range(num_major_ticks + 1):
                    ratio = i / float(num_major_ticks)
                    ms = self._duration_ms * ratio
                    x = groove_rect.left() + int(ratio * max(1, groove_rect.width() - 1))
                    is_obscured = False
                    start_handle_rect = self._get_handle_rect('start')
                    end_handle_rect = self._get_handle_rect('end')
                    if start_handle_rect.isValid() and (abs(x - start_handle_rect.center().x()) < (start_handle_rect.width() / 2 + 5) or 
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
                knob_w = 15
                knob_h = 40
                cx = playhead_rect.center().x()
                cy = groove_rect.center().y()
                knob_rect = QRect(cx - knob_w // 2, cy - knob_h // 2, knob_w, knob_h)
                g = QLinearGradient(knob_rect.left(), knob_rect.top(), knob_rect.left(), knob_rect.bottom())
                c1 = QColor("#5a5a5a")
                c2 = QColor("#9a9a9a")
                if self._hovering_handle == 'playhead' or self._dragging_handle == 'playhead':
                    c1 = c1.lighter(110); c2 = c2.lighter(110)
                    p.setPen(QPen(QColor("#7DD3FC"), 2))
                else:
                    p.setPen(QPen(QColor("#111111"), 1))
                g.setColorAt(0.0, c1)
                g.setColorAt(0.35, c2)
                g.setColorAt(0.38, Qt.black); g.setColorAt(0.42, Qt.black)
                g.setColorAt(0.45, c2)
                g.setColorAt(0.48, Qt.black); g.setColorAt(0.52, Qt.black)
                g.setColorAt(0.55, c2)
                g.setColorAt(0.58, Qt.black); g.setColorAt(0.62, Qt.black)
                g.setColorAt(0.65, c2)
                g.setColorAt(1.0, c1)
                p.setBrush(QBrush(g))
                p.drawRoundedRect(knob_rect, 2, 2)
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
