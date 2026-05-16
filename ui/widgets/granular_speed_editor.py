try:
    import mpv
except Exception:
    mpv = None

import sys
import os
import threading
import time as import_time
from PyQt5.QtWidgets import (QDialog, QVBoxLayout, QHBoxLayout, QPushButton,
                             QLabel, QDoubleSpinBox, QWidget, QStyle, QLineEdit,
                             QTextEdit, QPlainTextEdit, QAbstractSpinBox, QComboBox,
                             QMessageBox, QApplication, QSpinBox, QGridLayout)

from PyQt5.QtCore import Qt, QTimer, pyqtSignal, QRect, QPoint, QSize
from PyQt5.QtGui import QPainter, QColor, QBrush, QPen, QLinearGradient, QCursor, QIcon, QPixmap
from ui.widgets.trimmed_slider import TrimmedSlider
from system.utils import MPVSafetyManager
from ui.styles import UIStyles
SEGMENT_GAP_MS = 0
AUTO_START_AFTER_PREVIOUS_MS = 1000
MIN_SEGMENT_MS = 10
PENDING_PREVIEW_MS = 1000
SEEK_THROTTLE_MS = 50
SEEK_DUPLICATE_WINDOW_MS = 80
FREEZE_MIN_SEC = 0.5
FREEZE_MAX_SEC = 30.0
FREEZE_STEP_SEC = 0.5
FREEZE_DEFAULT_SEC = 1.0

class ClickableLabel(QLabel):
    clicked_signal = pyqtSignal()

    def __init__(self, text="", parent=None):
        super().__init__(text, parent)
        self.clicked_callback = None

    def mousePressEvent(self, ev):
        if ev.button() == Qt.LeftButton:
            if self.clicked_callback: self.clicked_callback()
            self.clicked_signal.emit()

class GranularTimelineSlider(TrimmedSlider):
    def __init__(self, parent=None):
        super().__init__(Qt.Horizontal, parent)
        self.segments = []
        self.active_segment_index = -1
        self.m_ants_timer = QTimer(self)
        self.m_ants_timer.setInterval(100)
        self.m_ants_timer.timeout.connect(self._update_ants)
        self.m_ants_offset = 0
        self.pending_start = -1
        self.pending_end = -1
        self.pending_speed = 1.1
        self.setMouseTracking(True)
        self._hovering_handle = None

    def _update_ants(self):
        self.m_ants_offset = (self.m_ants_offset + 2) % 12
        self.update()

    def start_ants(self):
        if not self.m_ants_timer.isActive():
            self.m_ants_timer.start()

    def stop_ants(self):
        self.m_ants_timer.stop()
        self.update()

    def set_segments(self, segments):
        self.segments = segments
        self.update()

    def set_pending_segment(self, start, end, speed):
        self.pending_start = start
        self.pending_end = end
        self.pending_speed = speed
        self.update()

    def mousePressEvent(self, e):
        if e.button() == Qt.LeftButton:
            is_over_handle = self._get_handle_rect('start').contains(e.pos()) or \
                             self._get_handle_rect('end').contains(e.pos()) or \
                             self._get_playhead_rect().contains(e.pos())
            if not is_over_handle:
                click_x = e.pos().x()
                found_seg = -1
                for i, seg in enumerate(self.segments):
                    s_pos = self._map_value_to_pos(seg['start'])
                    e_pos = self._map_value_to_pos(seg['end'])
                    if s_pos > e_pos: s_pos, e_pos = e_pos, s_pos
                    hit_margin = 10 if abs(seg.get('speed', 1.0)) < 0.001 else 4
                    if s_pos - hit_margin <= click_x <= e_pos + hit_margin:
                        found_seg = i
                        break
                if found_seg != -1:
                    self.active_segment_index = found_seg
                    if hasattr(self.parent(), 'edit_segment'):
                        self.parent().edit_segment(found_seg)
                        self.parent().seek_video(self.segments[found_seg]['start'], exact=True)
                    e.accept()
                    return
                else:
                    self.active_segment_index = -1
                    if hasattr(self.parent(), 'on_selection_cleared'):
                        self.parent().on_selection_cleared()
        super().mousePressEvent(e)

    def mouseMoveEvent(self, e):
        if self._dragging_handle == 'start':
            val = self._map_pos_to_value(e.pos().x())
            parent = self.parent()
            if hasattr(parent, "_clamp_selection_start"):
                val = parent._clamp_selection_start(val, self.trimmed_end_ms, self.active_segment_index)
            else:
                low = self.minimum()
                for i, seg in enumerate(self.segments):
                    if i != self.active_segment_index and seg['end'] <= self.trimmed_start_ms:
                        low = max(low, seg['end'] + SEGMENT_GAP_MS)
                val = max(low, min(val, self.trimmed_end_ms - MIN_SEGMENT_MS))
            if val != self.trimmed_start_ms:
                self.trimmed_start_ms = val
                self.trim_times_changed.emit(self.trimmed_start_ms, self.trimmed_end_ms)
                self.update()
            return
        elif self._dragging_handle == 'end':
            val = self._map_pos_to_value(e.pos().x())
            parent = self.parent()
            if hasattr(parent, "_clamp_selection_end"):
                val = parent._clamp_selection_end(val, self.trimmed_start_ms, self.active_segment_index)
            else:
                high = self.maximum()
                for i, seg in enumerate(self.segments):
                    if i != self.active_segment_index and seg['start'] >= self.trimmed_end_ms:
                        high = min(high, seg['start'] - SEGMENT_GAP_MS)
                val = min(high, max(val, self.trimmed_start_ms + MIN_SEGMENT_MS))
            if val != self.trimmed_end_ms:
                self.trimmed_end_ms = val
                self.trim_times_changed.emit(self.trimmed_start_ms, self.trimmed_end_ms)
                self.update()
            return
        super().mouseMoveEvent(e)
        if not self._dragging_handle and not self._hovering_handle:
            val = self._map_pos_to_value(e.pos().x())
            for seg in self.segments:
                if seg['start'] <= val <= seg['end']:
                    self.setCursor(QCursor(Qt.PointingHandCursor))
                    break

    def _get_groove_rect(self):
        h = 4
        top = (self.height() - h) // 2
        return QRect(8, top, max(1, self.width() - 16), h)

    def paintEvent(self, event):
        p = QPainter(self)
        try:
            p.setRenderHint(QPainter.Antialiasing)
            groove_rect = self._get_groove_rect()
            p.setPen(Qt.NoPen); p.setBrush(QColor(40, 40, 40)); p.drawRoundedRect(groove_rect, 2, 2)
            for i, seg in enumerate(self.segments):
                is_active = (i == self.active_segment_index)
                self._draw_segment_on_groove(p, groove_rect, seg['start'], seg['end'], seg['speed'], is_pending=is_active)
            if self.pending_start != -1 and self.pending_end != -1:
                self._draw_segment_on_groove(p, groove_rect, self.pending_start, self.pending_end, self.pending_speed, is_pending=True)
            min_v, max_v = self.minimum(), self.maximum()
            fm = p.fontMetrics()
            if max_v > min_v:
                step = 10000 if (max_v - min_v) > 60000 else 5000 if (max_v - min_v) > 30000 else 1000
                for ms in range(int(min_v // step * step), int(max_v + 1), step):
                    if ms < min_v: continue
                    x = self._map_value_to_pos(ms)
                    is_obscured = False
                    for seg in self.segments:
                        if seg['start'] <= ms <= seg['end']: is_obscured = True; break
                    if not is_obscured:
                        p.setPen(QColor(180, 180, 180))
                        p.drawLine(x, groove_rect.bottom() + 1, x, groove_rect.bottom() + 6)
                        p.drawText(x - fm.horizontalAdvance(self._fmt(int(ms))) // 2, groove_rect.bottom() + 18, self._fmt(int(ms)))
            else:
                p.setPen(QColor(150, 150, 150))
                p.drawText(groove_rect.left(), groove_rect.bottom() + 18, self._fmt(min_v))
                p.drawText(groove_rect.right() - 20, groove_rect.bottom() + 18, self._fmt(max_v))
            try:
                playhead_rect = self._get_playhead_rect()
                if playhead_rect and playhead_rect.isValid():
                    knob_w, knob_h = 15, 40
                    cx = playhead_rect.center().x()
                    cy = groove_rect.center().y()
                    knob_rect = QRect(cx - knob_w // 2, cy - knob_h // 2, knob_w, knob_h)
                    g = QLinearGradient(knob_rect.left(), knob_rect.top(), knob_rect.left(), knob_rect.bottom())
                    c1, c2 = QColor("#5a5a5a"), QColor("#9a9a9a")
                    if self._hovering_handle == 'playhead' or self._dragging_handle == 'playhead':
                        c1, c2 = c1.lighter(110), c2.lighter(110)
                        p.setPen(QPen(QColor("#7DD3FC"), 2))
                    else: p.setPen(QPen(QColor("#111111"), 1))
                    g.setColorAt(0.0, c1); g.setColorAt(0.35, c2); g.setColorAt(0.38, Qt.black); g.setColorAt(0.42, Qt.black); g.setColorAt(0.45, c2); g.setColorAt(0.48, Qt.black); g.setColorAt(0.52, Qt.black); g.setColorAt(0.55, c2); g.setColorAt(0.58, Qt.black); g.setColorAt(0.62, Qt.black); g.setColorAt(0.65, c2); g.setColorAt(1.0, c1)
                    p.setBrush(QBrush(g)); p.drawRoundedRect(knob_rect, 2, 2)
            except Exception: pass
            for handle_type in ['start', 'end']:
                handle_rect = self._get_handle_rect(handle_type)
                if not handle_rect.isValid(): continue
                color = QColor(0, 0, 0, 150)
                if self._hovering_handle == handle_type or self._dragging_handle == handle_type:
                    color = QColor(230, 126, 34, 200)
                p.setPen(Qt.NoPen); p.setBrush(color); p.drawRoundedRect(handle_rect, 4, 4)
        finally:
            if p.isActive(): p.end()

    def _draw_segment_on_groove(self, p, groove_rect, start_ms, end_ms, speed, is_pending=False):
        if end_ms <= start_ms: return
        is_freeze = abs(speed) < 0.001
        if is_freeze: seg_color = QColor("#9b59b6")
        else: seg_color = QColor("#2ecc71") if speed > 1.101 else QColor("#e74c3c") if speed < 1.099 else QColor("#95a5a6")
        alpha = 180 if is_pending else 120
        seg_color.setAlpha(alpha); p.setBrush(seg_color); p.setPen(Qt.NoPen)
        s_pos, e_pos = self._map_value_to_pos(start_ms), self._map_value_to_pos(end_ms)
        if s_pos > e_pos: s_pos, e_pos = e_pos, s_pos
        h, y = 24, groove_rect.center().y() - 12
        seg_rect = QRect(s_pos, y, max(1, e_pos - s_pos), h); p.drawRect(seg_rect)
        if is_freeze:
            try:
                cam_icon = self.style().standardIcon(QStyle.SP_DialogHelpButton)
                icon_size = min(16, seg_rect.height() - 4)
                icon_rect = QRect(seg_rect.center().x() - icon_size // 2, seg_rect.center().y() - icon_size // 2, icon_size, icon_size)
                cam_icon.paint(p, icon_rect)
            except: pass
        if is_pending:
            p.setBrush(Qt.NoBrush)
            if self.m_ants_timer.isActive():
                pen = QPen(QColor("white"), 2, Qt.DashLine); pen.setDashOffset(self.m_ants_offset); p.setPen(pen)
            else: p.setPen(QPen(QColor("white"), 1, Qt.DashLine))
            p.drawRect(seg_rect)

class GranularSpeedEditor(QDialog):
    BUTTON_BLUE = UIStyles.BUTTON_WIZARD_BLUE
    BUTTON_GREEN = UIStyles.BUTTON_WIZARD_GREEN
    BUTTON_CANCEL = UIStyles.BUTTON_CANCEL
    BUTTON_DANGER = UIStyles.BUTTON_DANGER
    BUTTON_STANDARD = UIStyles.BUTTON_STANDARD
    BUTTON_PRESET = UIStyles.BUTTON_WIZARD_BLUE + "QPushButton { font-size: 9px; padding: 1px 3px; border-radius: 5px; min-width: 0px; } QPushButton:pressed:!disabled { padding: 2px 4px 0px 2px; }"
    status_update_signal = pyqtSignal(str)
    @property
    def freeze_images(self):
        return [seg for seg in self.speed_segments if abs(seg['speed']) < 0.001]

    def _safe_mpv_set(self, prop, value):
        if not getattr(self, "player", None): return False
        lock = self._mpv_lock
        try:
            if not lock.acquire(timeout=self._mpv_lock_timeout): return False
            try:
                if prop == "wid": self.player.wid = value
                elif prop == "pause": self.player.pause = value
                elif prop == "speed": self.player.speed = value
                elif prop == "volume": self.player.volume = value
                elif prop == "mute": self.player.mute = value
                else: self.player.set_property(prop, value)
                return True
            finally: lock.release()
        except: return False

    def _safe_mpv_get(self, prop, default=None):
        if not getattr(self, "player", None): return default
        lock = self._mpv_lock
        try:
            if not lock.acquire(timeout=self._mpv_lock_timeout):
                if prop == 'time-pos':
                    last_pos = getattr(self, "last_position_ms", 0)
                    return (last_pos + self.abs_trim_start) / 1000.0
                return default
            try: return getattr(self.player, prop, default)
            finally: lock.release()
        except: return default

    def _safe_mpv_command(self, *args):
        if not getattr(self, "player", None): return False
        lock = self._mpv_lock
        try:
            if not lock.acquire(timeout=self._mpv_lock_timeout): return False
            try:
                if not args: return False
                return MPVSafetyManager.safe_mpv_command(self.player, args[0], *args[1:], max_attempts=1)
            finally: lock.release()
        except: return False

    def _init_state(self):
        self.selection_modified = False
        self.list_modified = False
        self._suppress_speed_change_flag = False

    def __init__(self, input_file_path, parent=None, initial_segments=None, base_speed=1.1, start_time_ms=0, volume=100):
        super().__init__(None)
        self._mpv_lock = threading.RLock()
        self._init_state()
        self.setWindowTitle("Granular Speed Editor")
        self.setWindowFlags(Qt.Window | Qt.WindowMinimizeButtonHint | Qt.WindowMaximizeButtonHint | Qt.WindowCloseButtonHint)
        self.input_file_path = input_file_path
        self.parent_app = parent
        self.base_speed = max(0.1, min(4.0, float(base_speed)))
        self.abs_trim_start = int(getattr(parent, "trim_start_ms", 0) or 0)
        self.abs_trim_end = int(getattr(parent, "trim_end_ms", 0) or 0)
        clip_dur = max(100, self.abs_trim_end - self.abs_trim_start)
        self.speed_segments = []
        if initial_segments:
            for seg in initial_segments:
                rel_s = seg['start'] - self.abs_trim_start
                rel_e = seg['end'] - self.abs_trim_start
                if rel_e > 0 and rel_s < clip_dur:
                    self.speed_segments.append({
                        'start': max(0, rel_s),
                        'end': min(clip_dur, rel_e),
                        'speed': seg['speed']
                    })
        self.speed_segments.sort(key=lambda item: (item['start'], item['end']))
        self.start_time_ms = max(0, int(start_time_ms) - self.abs_trim_start)
        self.last_position_ms = self.start_time_ms
        self.volume = volume
        self.duration = clip_dur
        self.player = None
        self.timer = QTimer(self)
        self.timer.setInterval(40)
        self._mpv_lock_timeout = 0.20
        self.timer.timeout.connect(self.update_ui)
        self._seek_timer = QTimer(self)
        self._seek_timer.setSingleShot(True)
        self._seek_timer.timeout.connect(self._flush_pending_seek)
        self._pending_seek = None
        self._last_seek_command_ts = 0.0
        self._last_seek_target_ms = None
        self._last_seek_exact = False
        self.is_playing = False
        self._last_rate_update = 0
        self._updating_ui = False
        self._start_manually_set = False
        self._pending_segment_active = False
        self._in_freeze_segment = False
        self._freeze_seg_idx = -1
        self._last_preview_bind_ts = 0
        self.restore_geometry()
        self.init_ui()
        if sys.platform == 'win32': os.environ["LC_NUMERIC"] = "C"
        QTimer.singleShot(100, self.setup_player)

    def restore_geometry(self):
        def_w, def_h = 1500, 800
        if self.parent_app and hasattr(self.parent_app, 'config_manager'):
            geom = self.parent_app.config_manager.config.get('granular_editor_geometry')
            if geom and isinstance(geom, dict):
                w, h = geom.get('w', def_w), geom.get('h', def_h); x, y = geom.get('x', -1), geom.get('y', -1)
                if x == -1 or y == -1: self.resize(w, h); self._center_on_screen()
                else:
                    screen = QApplication.screenAt(QPoint(x, y)) or QApplication.primaryScreen(); avail = screen.availableGeometry()
                    w, h = min(w, avail.width()), min(h, avail.height()); x, y = max(avail.x(), min(x, avail.right() - w)), max(avail.y(), min(y, avail.bottom() - h))
                    self.setGeometry(x, y, w, h)
            else: self.resize(def_w, def_h); self._center_on_screen()
        else: self.resize(def_w, def_h); self._center_on_screen()

    def _center_on_screen(self):
        screen_geo = QApplication.primaryScreen().availableGeometry(); self.move(screen_geo.x() + (screen_geo.width() - self.width()) // 2, screen_geo.y() + (screen_geo.height() - self.height()) // 2)

    def save_geometry(self):
        if self.parent_app and hasattr(self.parent_app, 'config_manager'):
            cfg = dict(getattr(self.parent_app.config_manager, 'config', {}) or {}); cfg['granular_editor_geometry'] = {'x': self.geometry().x(), 'y': self.geometry().y(), 'w': self.geometry().width(), 'h': self.geometry().height()}
            try: self.parent_app.config_manager.save_config(cfg)
            except Exception: pass

    def _update_clear_all_btn_state(self):
        has_segments = len(self.speed_segments) > 0; self.clear_all_btn.setEnabled(has_segments); self.clear_all_btn.setStyleSheet(self.BUTTON_DANGER if has_segments else self.BUTTON_STANDARD); self._update_segment_counter()

    def _update_segment_counter(self):
        count = sum(1 for seg in self.speed_segments if abs(seg['speed'] - self.base_speed) > 0.01)
        self.setWindowTitle(f"Granular Speed Editor    CURRENT DIFFERENT SPEED SEGMENTS: {count}")

    def _set_default_pending_range(self):
        if not hasattr(self, "timeline"):
            return
        self.timeline.active_segment_index = -1
        self._start_manually_set = False
        self._pending_segment_active = True
        self.timeline.set_trim_times(0, int(self.duration))
        self.timeline.set_pending_segment(0, int(self.duration), self.speed_spin.value())
        self.timeline.start_ants()

    def _apply_speed_preset(self, value):
        self.speed_spin.setValue(float(value))

    def _sorted_segments(self, exclude_idx=-1):
        items = []
        for i, seg in enumerate(self.speed_segments):
            if i == exclude_idx:
                continue
            try:
                items.append((i, int(seg['start']), int(seg['end'])))
            except Exception:
                continue
        return sorted(items, key=lambda item: item[1])

    def _gap_for_point(self, point, exclude_idx=-1):
        point = max(0, min(int(self.duration), int(point)))
        low, high = 0, int(self.duration)
        for _i, start, end in self._sorted_segments(exclude_idx):
            if start <= point < end:
                return None
            if end <= point:
                low = max(low, end + SEGMENT_GAP_MS)
            elif start > point:
                high = min(high, start - SEGMENT_GAP_MS)
                break
        return low, high

    def _clamp_selection_start(self, requested_start, current_end, exclude_idx=-1):
        current_end = max(0, min(int(self.duration), int(current_end)))
        low = 0
        for _i, start, end in self._sorted_segments(exclude_idx):
            if end <= current_end:
                low = max(low, end + SEGMENT_GAP_MS)
            elif start < current_end:
                low = max(low, end + SEGMENT_GAP_MS)
            else:
                break
        high = max(low, current_end - MIN_SEGMENT_MS)
        return max(low, min(int(requested_start), high))

    def _clamp_selection_end(self, requested_end, current_start, exclude_idx=-1):
        current_start = max(0, min(int(self.duration), int(current_start)))
        high = int(self.duration)
        for _i, start, end in self._sorted_segments(exclude_idx):
            if end <= current_start:
                continue
            if start >= current_start:
                high = min(high, start - SEGMENT_GAP_MS)
                break
        low = min(high, current_start + MIN_SEGMENT_MS)
        return min(high, max(int(requested_end), low))

    def _auto_range_ending_at(self, end):
        end = max(0, min(int(self.duration), int(end)))
        for _i, start, seg_end in self._sorted_segments():
            if start < end <= seg_end:
                end = start
                break
        start = 0
        for _i, _start, seg_end in sorted(self._sorted_segments(), key=lambda item: item[2]):
            if seg_end + AUTO_START_AFTER_PREVIOUS_MS <= end:
                start = max(start, seg_end + AUTO_START_AFTER_PREVIOUS_MS)
        end = self._clamp_selection_end(end, start)
        return start, end

    def init_ui(self):
        self.setStyleSheet('''QWidget { background-color: #2c3e50; color: #ecf0f1; font-family: "Helvetica Neue", Arial, sans-serif; } QLabel { font-size: 12px; padding: 5px; } QToolTip { border: 1px solid #ecf0f1; background-color: #34495e; color: white; }''')
        main_layout = QVBoxLayout(self); main_layout.setContentsMargins(20, 20, 20, 30); main_layout.setSpacing(15)
        self.video_frame = QWidget(); self.video_frame.setStyleSheet("background-color: black;"); 
        self.video_frame.setAttribute(Qt.WA_DontCreateNativeAncestors); 
        self.video_frame.setAttribute(Qt.WA_NativeWindow); 
        self.video_frame.setAttribute(Qt.WA_OpaquePaintEvent);
        self.video_frame.setAttribute(Qt.WA_NoSystemBackground);
        self.video_frame.setAutoFillBackground(False)
        self.video_frame.setMinimumHeight(320)
        self.video_frame.setFocusPolicy(Qt.NoFocus)
        main_layout.addWidget(self.video_frame, stretch=1)
        try:
            _ = int(self.video_frame.winId())
        except Exception:
            pass
        self.timeline = GranularTimelineSlider(self); self.timeline.setRange(0, int(self.duration)); self.timeline.sliderMoved.connect(self._on_timeline_moved); self.timeline.sliderReleased.connect(self._on_timeline_released); self.timeline.trim_times_changed.connect(self.on_trim_changed); main_layout.addWidget(self.timeline)
        controls_layout = QHBoxLayout(); controls_layout.setSpacing(14)
        self.freeze_btn = QPushButton("📷  FREEZE  📷"); self.freeze_btn.setStyleSheet(self.BUTTON_BLUE); self.freeze_btn.setFixedSize(170, 35); self.freeze_btn.setCursor(Qt.PointingHandCursor); self.freeze_btn.clicked.connect(self.freeze_image); self.freeze_btn.setFocusPolicy(Qt.NoFocus); self.freeze_btn.setToolTip("Replace the segment after the playhead with a frozen still frame.\nOutput length stays the same; the underlying gameplay is discarded."); controls_layout.addWidget(self.freeze_btn)
        self.freeze_sec_spin = QDoubleSpinBox(); self.freeze_sec_spin.setRange(FREEZE_MIN_SEC, FREEZE_MAX_SEC); self.freeze_sec_spin.setDecimals(1); self.freeze_sec_spin.setSingleStep(FREEZE_STEP_SEC); self.freeze_sec_spin.setValue(FREEZE_DEFAULT_SEC); self.freeze_sec_spin.setSuffix(" s"); self.freeze_sec_spin.setFixedHeight(35); self.freeze_sec_spin.setFixedWidth(80); self.freeze_sec_spin.setAlignment(Qt.AlignCenter); self.freeze_sec_spin.setCursor(Qt.PointingHandCursor); self.freeze_sec_spin.setStyleSheet(UIStyles.SPINBOX); self.freeze_sec_spin.setToolTip("Freeze duration in seconds (0.5 – 30).\nClamped automatically to remaining clip time."); controls_layout.addWidget(self.freeze_sec_spin)
        controls_layout.addStretch(1)
        self.start_trim_button = QPushButton("MARK START"); self.start_trim_button.setStyleSheet(self.BUTTON_BLUE); self.start_trim_button.setFixedSize(150, 35); self.start_trim_button.setCursor(Qt.PointingHandCursor); self.start_trim_button.clicked.connect(self.set_start); self.start_trim_button.setFocusPolicy(Qt.NoFocus); controls_layout.addWidget(self.start_trim_button)
        self.play_btn = QPushButton("PLAY"); self.play_btn.setStyleSheet(self.BUTTON_GREEN); self.play_btn.setFixedSize(150, 35); self.play_btn.setCursor(Qt.PointingHandCursor); self.play_btn.setIcon(self.style().standardIcon(QStyle.SP_MediaPlay)); self.play_btn.clicked.connect(self.toggle_play); self.play_btn.setFocusPolicy(Qt.NoFocus); controls_layout.addWidget(self.play_btn)
        self.end_trim_button = QPushButton("MARK END"); self.end_trim_button.setStyleSheet(self.BUTTON_BLUE); self.end_trim_button.setFixedSize(150, 35); self.end_trim_button.setCursor(Qt.PointingHandCursor); self.end_trim_button.clicked.connect(self.set_end); self.end_trim_button.setFocusPolicy(Qt.NoFocus); controls_layout.addWidget(self.end_trim_button)
        controls_layout.addStretch(1); main_layout.addLayout(controls_layout)
        action_row = QGridLayout(); action_row.setContentsMargins(0, 10, 0, 0); action_row.setHorizontalSpacing(0); action_row.setColumnStretch(0, 1); action_row.setColumnStretch(1, 1); action_row.setColumnStretch(2, 1)
        left_actions = QHBoxLayout(); left_actions.setSpacing(8); left_actions.setContentsMargins(0, 0, 0, 0); self.clear_all_btn = QPushButton("CLEAR ALL"); self.clear_all_btn.setStyleSheet(self.BUTTON_STANDARD); self.clear_all_btn.clicked.connect(self.clear_all_segments); self.clear_all_btn.setCursor(Qt.PointingHandCursor); self.clear_all_btn.setFocusPolicy(Qt.NoFocus); self.clear_all_btn.setFixedSize(100, 35); left_actions.addWidget(self.clear_all_btn); self.delete_seg_btn = QPushButton("DELETE SPEED SEGMENT"); self.delete_seg_btn.setStyleSheet(self.BUTTON_STANDARD); self.delete_seg_btn.setCursor(Qt.PointingHandCursor); self.delete_seg_btn.clicked.connect(self.delete_current_selected_segment); self.delete_seg_btn.setFocusPolicy(Qt.NoFocus); self.delete_seg_btn.setFixedSize(180, 35); self.delete_seg_btn.setEnabled(False); left_actions.addWidget(self.delete_seg_btn)
        center_actions = QHBoxLayout(); center_actions.setSpacing(20); center_actions.setContentsMargins(0, 0, 0, 0); self.cancel_btn = QPushButton("CANCEL"); self.cancel_btn.setStyleSheet(self.BUTTON_CANCEL); self.cancel_btn.setFixedSize(105, 35); self.cancel_btn.clicked.connect(self.reject); self.cancel_btn.setCursor(Qt.PointingHandCursor); self.cancel_btn.setFocusPolicy(Qt.NoFocus); center_actions.addWidget(self.cancel_btn); self.save_btn = QPushButton("APPLY"); self.save_btn.setStyleSheet(self.BUTTON_GREEN); self.save_btn.setFixedSize(105, 35); self.save_btn.clicked.connect(self.accept); self.save_btn.setCursor(Qt.PointingHandCursor); self.save_btn.setFocusPolicy(Qt.NoFocus); center_actions.addWidget(self.save_btn)
        speed_container = QVBoxLayout(); speed_container.setSpacing(3); speed_container.setContentsMargins(0, 0, 0, 0)
        lbl_speed = QLabel("Segment Speed"); lbl_speed.setStyleSheet("font-size: 11px; font-weight: bold; text-align: right; padding: 0; margin: 0;"); lbl_speed.setAlignment(Qt.AlignRight | Qt.AlignVCenter)
        self.speed_spin = QDoubleSpinBox(); self.speed_spin.setRange(0.1, 4.0); self.speed_spin.setDecimals(1); self.speed_spin.setSingleStep(0.1); self.speed_spin.setValue(self.base_speed); self.speed_spin.setFixedWidth(80); self.speed_spin.setFixedHeight(35); self.speed_spin.setAlignment(Qt.AlignCenter); self.speed_spin.setCursor(Qt.PointingHandCursor); self.speed_spin.setStyleSheet(UIStyles.SPINBOX); self.speed_spin.valueChanged.connect(self.on_speed_changed)
        speed_container.addWidget(lbl_speed, alignment=Qt.AlignRight); speed_container.addWidget(self.speed_spin, alignment=Qt.AlignRight)
        preset_grid = QGridLayout(); preset_grid.setHorizontalSpacing(4); preset_grid.setVerticalSpacing(4); preset_grid.setContentsMargins(0, 0, 0, 0)
        self.speed_preset_buttons = []
        for row, entries in enumerate(((("0.1x", 0.1), ("0.5x", 0.5), ("1.0x", 1.0), ("1.5x", 1.5)), (("2.0x", 2.0), ("2.5x", 2.5), ("3.0x", 3.0), ("3.5x", 3.5), ("4.0x", 4.0)))):
            for col, (label, value) in enumerate(entries):
                btn = QPushButton(label); btn.setStyleSheet(self.BUTTON_PRESET); btn.setFixedSize(40, 20); btn.setCursor(Qt.PointingHandCursor); btn.setFocusPolicy(Qt.NoFocus); btn.clicked.connect(lambda _=False, v=value: self._apply_speed_preset(v)); preset_grid.addWidget(btn, row, col); self.speed_preset_buttons.append(btn)
        speed_and_btn_layout = QHBoxLayout(); speed_and_btn_layout.setSpacing(10); speed_and_btn_layout.setContentsMargins(0, 0, 0, 0); speed_and_btn_layout.addLayout(preset_grid); speed_and_btn_layout.addLayout(speed_container); action_row.addLayout(left_actions, 0, 0, Qt.AlignLeft | Qt.AlignVCenter); action_row.addLayout(center_actions, 0, 1, Qt.AlignCenter); action_row.addLayout(speed_and_btn_layout, 0, 2, Qt.AlignRight | Qt.AlignVCenter); main_layout.addLayout(action_row); self._update_clear_all_btn_state()

    def setup_player(self, attempt=0):
        if not self.input_file_path: return
        try:
            self.video_frame.setAttribute(Qt.WA_NativeWindow)
            wid = self._preview_wid()
            if wid <= 0 and attempt < 10:
                QTimer.singleShot(120, lambda: self.setup_player(attempt + 1))
                return
            self.player = MPVSafetyManager.create_safe_mpv(
                wid=wid if wid > 0 else None,
                osc=False, hr_seek='yes', hwdec='auto', keep_open='yes',
                ytdl=False,
                vo='gpu' if sys.platform == 'win32' else 'gpu',
                input_default_bindings=False, input_vo_keyboard=False,
                extra_mpv_flags=[('force-window', 'no')] if wid > 0 else []
            )
            if not self.player: return
            self._bind_player_to_preview(True)
            self._safe_mpv_set("mute", False); self._safe_mpv_set("volume", max(1, self.volume))
            start_sec = self.abs_trim_start / 1000.0
            end_sec = self.abs_trim_end / 1000.0
            if end_sec > start_sec:
                options = f"start={start_sec:.3f},end={end_sec:.3f}"
                if not self._safe_mpv_command("loadfile", self.input_file_path, "replace", 0, options): return
            else:
                if not self._safe_mpv_command("loadfile", self.input_file_path, "replace"): return
        except Exception: return

        def _get_dur(bind_attempt=0):
            if not getattr(self, "player", None): return
            try:
                if bind_attempt == 0:
                    self._bind_player_to_preview(True)
                dur = self._safe_mpv_get('duration', 0)
                if dur and dur > 0:
                    self.timeline.setRange(0, int(self.duration))
                    self.timeline.set_duration_ms(int(self.duration))
                    self.timeline.set_segments(self.speed_segments)
                    if self.speed_segments:
                        self.on_selection_cleared()
                    else:
                        self._set_default_pending_range()
                    self.selection_modified = False
                    QTimer.singleShot(250, self._finalize_startup)
                elif bind_attempt < 40:
                    QTimer.singleShot(100, lambda: _get_dur(bind_attempt + 1))
            except Exception:
                if bind_attempt < 40:
                    QTimer.singleShot(100, lambda: _get_dur(bind_attempt + 1))
        QTimer.singleShot(100, lambda: _get_dur(0))

    def _finalize_startup(self):
        if not self.player: return
        self._bind_player_to_preview(True)
        self._safe_mpv_set("pause", True)
        self.last_position_ms = self._normalize_seek_position(self.start_time_ms)
        self._pending_seek = None
        self._stop_seek_timer()
        self._issue_seek_now(self.last_position_ms, True)
        self.timeline.setValue(int(self.start_time_ms)); self.play_btn.setText("PLAY"); self.play_btn.setIcon(self.style().standardIcon(QStyle.SP_MediaPlay)); self.is_playing = False

    def _preview_wid(self):
        try:
            if not getattr(self, "video_frame", None): return 0
            self.video_frame.setAttribute(Qt.WA_NativeWindow)
            wid = int(self.video_frame.winId())
            return wid if wid > 0 else 0
        except Exception:
            return 0

    def _bind_player_to_preview(self, force=False):
        if not getattr(self, "player", None): return False
        wid = self._preview_wid()
        if wid <= 0:
            return False
        last_wid = getattr(self, "_last_bound_wid", 0)
        if not force and last_wid == wid:
            return True
        now = import_time.time()
        if not force and now - getattr(self, "_last_preview_bind_ts", 0) < 0.75:
            return True
        self._last_preview_bind_ts = now
        wid_changed = (last_wid != wid)
        self._last_bound_wid = wid
        self._safe_mpv_set("wid", wid)
        self._safe_mpv_set("force_window", "no")
        if wid_changed:
            try:
                self.player.command("video-reload")
            except Exception:
                pass
        return True

    def _is_editing_widget_focused(self) -> bool:
        fw = QApplication.focusWidget()
        if fw is None: return False
        if isinstance(fw, (QLineEdit, QTextEdit, QPlainTextEdit, QAbstractSpinBox)): return True
        if isinstance(fw, QComboBox) and (fw.isEditable() or fw.hasFocus()): return True
        return False

    def keyPressEvent(self, event):
        if self._is_editing_widget_focused():
            super().keyPressEvent(event); return
        key = event.key(); mods = event.modifiers()
        if key == Qt.Key_Space: self.toggle_play(); event.accept()
        elif key == Qt.Key_Left:
            ms = -100 if mods == Qt.ControlModifier else (-3000 if mods == Qt.ShiftModifier else -500)
            self.seek_relative(ms); event.accept()
        elif key == Qt.Key_Right:
            ms = 100 if mods == Qt.ControlModifier else (3000 if mods == Qt.ShiftModifier else 500)
            self.seek_relative(ms); event.accept()
        elif key == Qt.Key_BracketLeft: self.set_start(); event.accept()
        elif key == Qt.Key_BracketRight: self.set_end(); event.accept()
        elif key == Qt.Key_Delete:
            if self.timeline.active_segment_index != -1: self.delete_segment(self.timeline.active_segment_index)
            else: self.delete_current_selected_segment()
            event.accept()
        else: super().keyPressEvent(event)

    def seek_relative(self, ms):
        if not self.player: return
        curr_rel = (self._safe_mpv_get('time-pos', 0) or 0) * 1000 - self.abs_trim_start
        new_rel = max(0, min(self.duration, curr_rel + ms))
        self.seek_video(new_rel, exact=True); self.timeline.setValue(int(new_rel)); self.timeline.update()

    def _on_timeline_moved(self, value):
        self.seek_video(value, exact=False)

    def _on_timeline_released(self):
        self.seek_video(self.timeline.value(), exact=True)

    def _check_collision(self, start, end, exclude_idx=-1):
        for i, seg in enumerate(self.speed_segments):
            if i == exclude_idx: continue
            if start < seg['end'] + SEGMENT_GAP_MS and end > seg['start'] - SEGMENT_GAP_MS: return True
        return False

    def _commit_pending_segment(self, require_difference=False):
        if self.timeline.active_segment_index != -1:
            return True
        if not (getattr(self, "_pending_segment_active", False) or getattr(self, "_start_manually_set", False)):
            return True
        s = int(self.timeline.trimmed_start_ms)
        e = int(self.timeline.trimmed_end_ms)
        if e <= s:
            e = self._clamp_selection_end(s + PENDING_PREVIEW_MS, s)
        else:
            e = self._clamp_selection_end(e, s)
        if e - s < MIN_SEGMENT_MS:
            self._emit_error("Segment too short or blocked by a neighboring segment.")
            return False
        speed = float(self.speed_spin.value())
        if require_difference and abs(speed - self.base_speed) <= 0.01:
            self._pending_segment_active = False
            self._start_manually_set = False
            self.timeline.stop_ants()
            return True
        if self._check_collision(s, e):
            self._emit_error("Collision: segment overlaps another segment.")
            return False
        new_seg = {'start': s, 'end': e, 'speed': speed}
        self.speed_segments.append(new_seg)
        self.speed_segments.sort(key=lambda x: x['start'])
        self.timeline.set_segments(self.speed_segments)
        self._pending_segment_active = False
        self._start_manually_set = False
        self.list_modified = True
        self.selection_modified = False
        self._update_clear_all_btn_state()
        for i, seg in enumerate(self.speed_segments):
            if seg == new_seg:
                self.edit_segment(i)
                break
        return True

    def _emit_error(self, message: str):
        clean = str(message or "").strip()
        if hasattr(self.parent_app, 'statusBar'):
            try: self.parent_app.statusBar().showMessage(clean, 4000)
            except Exception: pass
        self.status_update_signal.emit(clean)

    def _emit_info(self, message: str):
        clean = str(message or "").strip()
        if hasattr(self.parent_app, 'statusBar'):
            try: self.parent_app.statusBar().showMessage(clean, 3000)
            except Exception: pass
        self.status_update_signal.emit(clean)

    def freeze_image(self):
        if not self.player: return
        try:
            was_playing = not bool(self._safe_mpv_get("pause", True))
            if was_playing: self.pause_video()
        except Exception:
            was_playing = False
        curr_rel = (self._safe_mpv_get('time-pos', 0) or 0) * 1000 - self.abs_trim_start
        requested_dur_sec = float(self.freeze_sec_spin.value())
        freeze_dur_ms = int(round(requested_dur_sec * 1000))
        duration_limit = int(self.duration)
        if duration_limit <= MIN_SEGMENT_MS:
            self._emit_error("Video clip is too short to freeze.")
            return
        start = max(0, min(int(curr_rel), duration_limit - MIN_SEGMENT_MS))
        max_possible_ms = duration_limit - start
        end = min(duration_limit, start + freeze_dur_ms)
        if end - start < MIN_SEGMENT_MS:
            self._emit_error("Freeze needs more remaining video time after the playhead.")
            return
        shrunk = False
        if freeze_dur_ms > max_possible_ms:
            shrunk = True
        if self._check_collision(start, end):
            for i, seg in enumerate(self.speed_segments):
                if seg['start'] > start:
                    end = min(end, seg['start'])
                    break
            if end - start < MIN_SEGMENT_MS:
                self._emit_error("Collision: this freeze overlaps an existing speed segment.")
                return
            if self._check_collision(start, end):
                self._emit_error("Collision: this freeze overlaps an existing speed segment.")
                return
            shrunk = True
        new_seg = {'start': start, 'end': end, 'speed': 0.0}
        self.speed_segments.append(new_seg); self.speed_segments.sort(key=lambda x: x['start'])
        self.timeline.set_segments(self.speed_segments); self.on_selection_cleared()
        for i, seg in enumerate(self.speed_segments):
            if seg == new_seg: self.edit_segment(i); break
        self.timeline.setValue(start)
        self.list_modified = True; self._update_clear_all_btn_state()
        actual_dur_sec = (end - start) / 1000.0
        if shrunk:
            self._emit_info(f"Frame frozen for {actual_dur_sec:.1f}s at {self._fmt(start)} (shrunk to fit; gameplay in this range is replaced).")
        else:
            self._emit_info(f"Frame frozen for {actual_dur_sec:.1f}s at {self._fmt(start)} (gameplay in this range is replaced).")

    def _fmt(self, ms: int) -> str:
        s = max(0, int(ms) // 1000); h, s = divmod(s, 3600); m, s = divmod(s, 60)
        return f"{h:d}:{m:02d}:{s:02d}" if h else f"{m:d}:{s:02d}"

    def toggle_play(self):
        if not self.player: return
        if not self._safe_mpv_get("pause", True): self.pause_video()
        else:
            curr_rel = (self._safe_mpv_get('time-pos', 0) or 0) * 1000 - self.abs_trim_start
            if curr_rel >= self.duration - 100: self.seek_video(0, exact=True); self.timeline.setValue(0); curr_rel = 0
            self._safe_mpv_set("pause", False); self.update_playback_speed(int(curr_rel)); self.timer.start(); self.play_btn.setText("PAUSE"); self.play_btn.setIcon(self.style().standardIcon(QStyle.SP_MediaPause))

    def pause_video(self):
        if self.player: self._safe_mpv_set("pause", True)
        self.timer.stop(); self.play_btn.setText("PLAY"); self.play_btn.setIcon(self.style().standardIcon(QStyle.SP_MediaPlay))

    def _normalize_seek_position(self, pos):
        try:
            rel_pos = int(round(float(pos)))
        except (TypeError, ValueError):
            rel_pos = int(getattr(self, "last_position_ms", 0) or 0)
        return max(0, min(int(self.duration), rel_pos))

    def _stop_seek_timer(self):
        timer = getattr(self, "_seek_timer", None)
        if timer is not None:
            try: timer.stop()
            except Exception: pass

    def _start_seek_timer(self, delay_ms):
        timer = getattr(self, "_seek_timer", None)
        if timer is None:
            return
        try:
            timer.start(max(1, int(delay_ms)))
        except Exception:
            pass

    def _last_seek_timestamp(self):
        try:
            return float(getattr(self, "_last_seek_command_ts", 0.0) or 0.0)
        except (TypeError, ValueError):
            return 0.0

    def _is_duplicate_seek(self, abs_pos_ms, exact, now):
        last_target = getattr(self, "_last_seek_target_ms", None)
        if last_target is None:
            return False
        try:
            last_target_ms = int(last_target)
        except (TypeError, ValueError):
            return False
        if last_target_ms != int(abs_pos_ms):
            return False
        elapsed_ms = (now - self._last_seek_timestamp()) * 1000.0
        if elapsed_ms > SEEK_DUPLICATE_WINDOW_MS:
            return False
        if not exact:
            return True
        return bool(getattr(self, "_last_seek_exact", False))

    def _issue_seek_now(self, rel_pos, exact, now=None):
        if not self.player:
            return False
        rel_pos = self._normalize_seek_position(rel_pos)
        abs_pos_ms = int(self.abs_trim_start) + rel_pos
        now = import_time.monotonic() if now is None else now
        if self._is_duplicate_seek(abs_pos_ms, bool(exact), now):
            return True
        mode = "exact" if exact else "keyframes"
        ok = self._safe_mpv_command("seek", abs_pos_ms / 1000.0, "absolute", mode)
        if ok:
            self._last_seek_command_ts = now
            self._last_seek_target_ms = abs_pos_ms
            self._last_seek_exact = bool(exact)
        return ok

    def _flush_pending_seek(self):
        pending = getattr(self, "_pending_seek", None)
        if pending is None:
            return
        rel_pos, exact = pending
        now = import_time.monotonic()
        elapsed_ms = (now - self._last_seek_timestamp()) * 1000.0
        if not exact and elapsed_ms < SEEK_THROTTLE_MS:
            self._start_seek_timer(SEEK_THROTTLE_MS - elapsed_ms)
            return
        self._pending_seek = None
        self._issue_seek_now(rel_pos, exact, now=now)

    def seek_video(self, pos, exact=False):
        self._in_freeze_segment = False
        self._freeze_seg_idx = -1
        if not self.player: return
        rel_pos = self._normalize_seek_position(pos)
        self.last_position_ms = rel_pos
        self.update_playback_speed(rel_pos)
        if exact:
            self._pending_seek = None
            self._stop_seek_timer()
            self._issue_seek_now(rel_pos, True)
            return
        now = import_time.monotonic()
        abs_pos_ms = int(self.abs_trim_start) + rel_pos
        if self._is_duplicate_seek(abs_pos_ms, False, now):
            return
        elapsed_ms = (now - self._last_seek_timestamp()) * 1000.0
        if elapsed_ms >= SEEK_THROTTLE_MS:
            self._pending_seek = None
            self._issue_seek_now(rel_pos, False, now=now)
        else:
            self._pending_seek = (rel_pos, False)
            self._start_seek_timer(SEEK_THROTTLE_MS - elapsed_ms)

    def update_ui(self):
        if not self.player or getattr(self, "_updating_ui", False): return
        self._updating_ui = True
        try:
            if getattr(self, "_in_freeze_segment", False):
                now = import_time.time(); elapsed = (now - self._freeze_start_ts) * 1000
                if self._freeze_seg_idx < 0 or self._freeze_seg_idx >= len(self.speed_segments):
                    self._in_freeze_segment = False; self._freeze_seg_idx = -1
                    return
                seg = self.speed_segments[self._freeze_seg_idx]; seg_dur = seg['end'] - seg['start']
                if elapsed >= seg_dur:
                    resume_rel = int(seg['start'])
                    self._in_freeze_segment = False; self._freeze_seg_idx = -1
                    self._safe_mpv_command("seek", (self.abs_trim_start + resume_rel) / 1000.0, "absolute", "exact")
                    self.update_playback_speed(resume_rel)
                    self._safe_mpv_set("pause", False)
                    self.timeline.blockSignals(True); self.timeline.setValue(resume_rel); self.timeline.blockSignals(False); self.timeline.update()
                    self.last_position_ms = resume_rel
                return
            curr_rel = (self._safe_mpv_get('time-pos', 0) or 0) * 1000 - self.abs_trim_start
            if getattr(self, "_start_manually_set", False):
                start = int(self.timeline.trimmed_start_ms)
                end = self._clamp_selection_end(int(curr_rel), start)
                self.timeline.set_trim_times(start, end)
                self.update_pending_visualization()
            if not self.timeline.isSliderDown():
                if curr_rel >= self.duration: 
                    self.pause_video()
                    curr_rel = self.duration
                    self.seek_video(self.duration, exact=True)
                elif curr_rel < 0: curr_rel = 0
                t_i = int(round(curr_rel)); self.timeline.blockSignals(True); self.timeline.setValue(t_i); self.timeline.blockSignals(False); self.timeline.update()
                self.update_playback_speed(t_i); self.last_position_ms = t_i
        finally: self._updating_ui = False

    def update_playback_speed(self, rel_time):
        if not self.player: return
        target_speed = self.base_speed; seg_idx = -1
        for i, seg in enumerate(self.speed_segments):
            if abs(seg['speed']) < 0.001 and seg['start'] <= rel_time < seg['end']:
                target_speed = 0.0; seg_idx = i; break
        if abs(target_speed) > 0.001:
            for i, seg in enumerate(self.speed_segments):
                if abs(seg['speed']) >= 0.001 and seg['start'] <= rel_time < seg['end']:
                    target_speed = seg['speed']; seg_idx = i; break
        if abs(target_speed) < 0.001:
            if not getattr(self, "_in_freeze_segment", False):
                self._in_freeze_segment = True; self._freeze_start_ts = import_time.time(); self._freeze_seg_idx = seg_idx; self._safe_mpv_set("pause", True)
            return
        if getattr(self, "_in_freeze_segment", False):
            self._in_freeze_segment = False; self._freeze_seg_idx = -1
        now = import_time.time()
        if now - getattr(self, "_last_rate_update", 0) < 0.05: return
        current_rate = self._safe_mpv_get("speed", 1.0)
        if abs(current_rate - target_speed) > 0.005:
            result = self._safe_mpv_set("speed", target_speed)
            if result is not False:
                self._last_rate_update = now

    def on_speed_changed(self, val):
        if getattr(self, "_suppress_speed_change_flag", False):
            self.update_pending_visualization()
            self.update_playback_speed(self.last_position_ms)
            return
        idx = self.timeline.active_segment_index
        if idx != -1 and 0 <= idx < len(self.speed_segments):
            if abs(self.speed_segments[idx]['speed'] - val) > 1e-9:
                self.speed_segments[idx]['speed'] = val
                self.timeline.set_segments(self.speed_segments)
                self.selection_modified = True
                self.list_modified = True
        elif self.timeline.trimmed_start_ms >= 0 and self.timeline.trimmed_end_ms > self.timeline.trimmed_start_ms:
            self.selection_modified = True
        self.update_pending_visualization()
        self.update_playback_speed(self.last_position_ms)

    def on_trim_changed(self, s, e):
        idx = self.timeline.active_segment_index
        if idx != -1 and 0 <= idx < len(self.speed_segments):
            seg = self.speed_segments[idx]
            new_start = int(s)
            new_end = int(e)
            if new_end - new_start < MIN_SEGMENT_MS:
                self.timeline.set_trim_times(seg['start'], seg['end'])
                self.update_pending_visualization()
                return
            if self._check_collision(new_start, new_end, exclude_idx=idx):
                self.timeline.set_trim_times(seg['start'], seg['end'])
                self._emit_error("Collision: segment overlaps another segment.")
                self.update_pending_visualization()
                return
            seg['start'] = new_start
            seg['end'] = new_end
            self.speed_segments.sort(key=lambda x: x['start'])
            self.timeline.set_segments(self.speed_segments)
            self.timeline.active_segment_index = self.speed_segments.index(seg)
            self.selection_modified = True
            self.list_modified = True
            self._update_clear_all_btn_state()
        self.update_pending_visualization()

    def update_pending_visualization(self):
        s, e = self.timeline.trimmed_start_ms, self.timeline.trimmed_end_ms
        if self.timeline.active_segment_index != -1:
            self.timeline.set_pending_segment(-1, -1, self.speed_spin.value())
            return
        if s < 0 or e <= s:
            self.timeline.set_pending_segment(-1, -1, self.speed_spin.value())
            return
        if not (getattr(self, "_pending_segment_active", False) or getattr(self, "_start_manually_set", False)):
            self.timeline.set_pending_segment(-1, -1, self.speed_spin.value())
            return
        self.timeline.set_pending_segment(s, e, self.speed_spin.value())

    def _update_delete_button_label(self, idx):
        if not hasattr(self, "delete_seg_btn"): return
        if 0 <= idx < len(self.speed_segments) and abs(self.speed_segments[idx].get('speed', 1.0)) < 0.001:
            self.delete_seg_btn.setText("DELETE FREEZE IMAGE")
        else:
            self.delete_seg_btn.setText("DELETE SPEED SEGMENT")

    def on_selection_cleared(self):
        self.timeline.active_segment_index = -1
        self._pending_segment_active = False
        self._start_manually_set = False
        self.delete_seg_btn.setEnabled(False)
        self.delete_seg_btn.setStyleSheet(self.BUTTON_STANDARD)
        self._update_delete_button_label(-1)
        self._suppress_speed_change_flag = True
        try:
            self.speed_spin.setValue(self.base_speed)
        finally:
            self._suppress_speed_change_flag = False
        self.timeline.set_trim_times(-1, -1)
        self.update_pending_visualization()
        self.timeline.stop_ants()

    def edit_segment(self, idx):
        if 0 <= idx < len(self.speed_segments):
            seg = self.speed_segments[idx]
            self._pending_segment_active = False
            self._start_manually_set = False
            self.timeline.active_segment_index = idx
            self.timeline.set_trim_times(seg['start'], seg['end'])
            self._suppress_speed_change_flag = True
            try:
                self.speed_spin.setValue(seg['speed'])
            finally:
                self._suppress_speed_change_flag = False
            self.delete_seg_btn.setEnabled(True)
            self.delete_seg_btn.setStyleSheet(self.BUTTON_DANGER)
            self._update_delete_button_label(idx)
            self.update_pending_visualization()
            self.timeline.start_ants()

    def add_segment(self):
        self._commit_pending_segment()

    def delete_segment(self, idx):
        if 0 <= idx < len(self.speed_segments):
            self.speed_segments.pop(idx); self.timeline.set_segments(self.speed_segments); self.on_selection_cleared(); self.list_modified = True; self._update_clear_all_btn_state()

    def delete_current_selected_segment(self):
        if self.timeline.active_segment_index != -1: self.delete_segment(self.timeline.active_segment_index)

    def clear_all_segments(self):
        if not self.speed_segments: return
        msg = QMessageBox(self); msg.setIcon(QMessageBox.Question); msg.setWindowTitle("Clear All"); msg.setText("Are you sure you want to delete all custom speed segments?"); msg.setStandardButtons(QMessageBox.Yes | QMessageBox.No)
        if msg.exec_() == QMessageBox.Yes:
            self.speed_segments = []
            self.timeline.set_segments([])
            self.list_modified = True
            self._update_clear_all_btn_state()
            self._set_default_pending_range()

    def set_start(self):
        current_speed = float(self.speed_spin.value())
        self.on_selection_cleared()
        self._suppress_speed_change_flag = True
        try:
            self.speed_spin.setValue(current_speed)
        finally:
            self._suppress_speed_change_flag = False
        curr_rel = (self._safe_mpv_get('time-pos', 0) or 0) * 1000 - self.abs_trim_start
        anchor = max(0, int(curr_rel))
        gap = self._gap_for_point(anchor)
        if gap is None:
            self._emit_error("Move the playhead outside an existing segment before marking a new start.")
            return
        _low, high = gap
        if high - anchor < MIN_SEGMENT_MS:
            self._emit_error("No free room after this start point.")
            return
        preview_end = min(high, anchor + PENDING_PREVIEW_MS)
        preview_end = max(preview_end, anchor + MIN_SEGMENT_MS)
        preview_end = min(preview_end, high)
        self.timeline.set_trim_times(anchor, preview_end)
        self._start_manually_set = True
        self._pending_segment_active = True
        self.timeline.start_ants()
        self.update_pending_visualization()

    def set_end(self):
        curr_rel = (self._safe_mpv_get('time-pos', 0) or 0) * 1000 - self.abs_trim_start
        e = int(curr_rel)
        if getattr(self, "_start_manually_set", False):
            s = self.timeline.trimmed_start_ms
            e = self._clamp_selection_end(e, s)
        else:
            s, e = self._auto_range_ending_at(e)
        self.timeline.active_segment_index = -1
        self.timeline.set_trim_times(s, e)
        self._pending_segment_active = True
        created = False
        if e - s >= MIN_SEGMENT_MS:
            created = self._commit_pending_segment()
        else:
            self._emit_error("Segment too short (minimum 10 ms).")
        self._start_manually_set = False
        if not created:
            self._pending_segment_active = False
            self.timeline.stop_ants()
        self.update_pending_visualization()
        self.pause_video()

    def accept(self):
        if self.timeline.active_segment_index == -1:
            speed_changed = abs(float(self.speed_spin.value()) - self.base_speed) > 0.01
            should_commit = bool(getattr(self, "_start_manually_set", False) or getattr(self, "_pending_segment_active", False) and (self.selection_modified or speed_changed))
            if should_commit and not self._commit_pending_segment(require_difference=not getattr(self, "_start_manually_set", False)):
                return
        self.cleanup(); self.save_geometry(); super().accept()

    def reject(self):
        if self.list_modified or self.selection_modified:
            msg = QMessageBox(self); msg.setIcon(QMessageBox.Warning); msg.setWindowTitle("Unsaved Changes"); msg.setText("You have unsaved speed segments. Discard and close?"); msg.setStandardButtons(QMessageBox.Yes | QMessageBox.Cancel)
            if msg.exec_() == QMessageBox.Cancel: return
        self.cleanup(); self.save_geometry(); super().reject()

    def cleanup(self):
        self.timer.stop()
        self._pending_seek = None
        self._stop_seek_timer()
        try: self.timeline.stop_ants()
        except Exception: pass
        if self.player: MPVSafetyManager.safe_mpv_shutdown(self.player); self.player = None

    def showEvent(self, event):
        super().showEvent(event)
        QTimer.singleShot(0, lambda: self._bind_player_to_preview(True))
        if self.timeline.active_segment_index != -1 or getattr(self, "_start_manually_set", False):
            self.timeline.start_ants()

    def hideEvent(self, event):
        super().hideEvent(event)
        try: self.timeline.stop_ants()
        except Exception: pass

    def resizeEvent(self, event):
        super().resizeEvent(event)
        QTimer.singleShot(0, lambda: self._bind_player_to_preview(True))

    def closeEvent(self, event):
        self.reject()
        if self.isVisible(): event.ignore()
        else: event.accept()
