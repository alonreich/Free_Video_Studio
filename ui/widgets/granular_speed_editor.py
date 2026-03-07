try:
    import mpv
except Exception:
    mpv = None

import sys
import os
import threading
import time as import_time
from PyQt5.QtWidgets import (QDialog, QVBoxLayout, QHBoxLayout, QPushButton,
                             QLabel, QDoubleSpinBox, QWidget, QMessageBox, QStyle, QStyleOptionSlider, QApplication, QSizePolicy, QLineEdit, QTextEdit, QPlainTextEdit, QAbstractSpinBox, QComboBox)

from PyQt5.QtCore import Qt, QTimer, QRect, QSize, pyqtSignal, QPoint
from PyQt5.QtGui import QPixmap, QPainter, QFont, QFontMetrics, QPen, QBrush, QLinearGradient, QCursor, QPainterPath, QIcon, QColor
from ui.widgets.trimmed_slider import TrimmedSlider
from system.utils import MPVSafetyManager

class ClickableLabel(QLabel):
    def __init__(self, text, parent=None):
        super().__init__(text, parent)
        self.setCursor(Qt.PointingHandCursor)
    
    def mousePressEvent(self, event):
        if event.button() == Qt.LeftButton:
            if hasattr(self, 'clicked_callback'):
                self.clicked_callback()
        super().mousePressEvent(event)

class GranularTimelineSlider(TrimmedSlider):
    def __init__(self, parent=None):
        super().__init__(parent)
        self.segments = []
        self.pending_start = -1
        self.pending_end = -1
        self.pending_speed = 1.1
        self.active_segment_index = -1
        self.m_ants_offset = 0
        self.m_ants_timer = QTimer(self)
        self.m_ants_timer.setInterval(100)
        self.m_ants_timer.timeout.connect(self._update_ants)
        self.setContextMenuPolicy(Qt.CustomContextMenu)
        self.customContextMenuRequested.connect(self._show_context_menu)

    def _update_ants(self):
        self.m_ants_offset = (self.m_ants_offset + 2) % 20
        self.update()

    def start_ants(self):
        if not self.m_ants_timer.isActive():
            self.m_ants_timer.start()

    def stop_ants(self):
        self.m_ants_timer.stop()
        self.update()

    def _show_context_menu(self, pos):
        from PyQt5.QtWidgets import QMenu
        val_ms = self._map_pos_to_value(pos.x())
        target_seg = None
        for i, seg in enumerate(self.segments):
            if seg['start'] <= val_ms <= seg['end']:
                target_seg = i
                break
        menu = QMenu(self)
        if target_seg is not None:
            action = menu.addAction("Delete Segment")
            action.triggered.connect(lambda: self.parent().delete_segment(target_seg))
        clear_action = menu.addAction("Clear All Segments")
        clear_action.triggered.connect(self.parent().clear_all_segments)
        menu.exec_(self.mapToGlobal(pos))

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
            val = self._map_pos_to_value(e.pos().x())
            is_over_handle = self._get_handle_rect('start').contains(e.pos()) or \
                             self._get_handle_rect('end').contains(e.pos()) or \
                             self._get_playhead_rect().contains(e.pos())
            if not is_over_handle:
                groove_center_y = self.height() // 2
                is_vertical_hit = abs(e.pos().y() - groove_center_y) <= 12
                found_seg = -1
                if is_vertical_hit:
                    for i, seg in enumerate(self.segments):
                        if seg['start'] <= val <= seg['end']:
                            found_seg = i
                            break
                if found_seg != -1:
                    self.active_segment_index = found_seg
                    if hasattr(self.parent(), 'edit_segment'):
                        self.parent().edit_segment(found_seg)
                        return
                else:
                    self.active_segment_index = -1
                    if hasattr(self.parent(), 'on_selection_cleared'):
                        self.parent().on_selection_cleared()
        super().mousePressEvent(e)

    def mouseMoveEvent(self, e):
        if self._dragging_handle == 'start':
            val = self._map_pos_to_value(e.pos().x())
            low = self.minimum()
            for i, seg in enumerate(self.segments):
                if i != self.active_segment_index and seg['end'] <= self.trimmed_start_ms:
                    low = max(low, seg['end'])
            val = max(low, min(val, self.trimmed_end_ms - 10))
            if val != self.trimmed_start_ms:
                self.trimmed_start_ms = val
                self.trim_times_changed.emit(self.trimmed_start_ms, self.trimmed_end_ms)
                self.update()
            return
        elif self._dragging_handle == 'end':
            val = self._map_pos_to_value(e.pos().x())
            high = self.maximum()
            for i, seg in enumerate(self.segments):
                if i != self.active_segment_index and seg['start'] >= self.trimmed_end_ms:
                    high = min(high, seg['start'])
            val = min(high, max(val, self.trimmed_start_ms + 10))
            if val != self.trimmed_start_ms:
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
            p.fillRect(self.rect(), Qt.transparent)
            groove_rect = self._get_groove_rect()
            p.setPen(Qt.NoPen)
            p.setBrush(QColor("#3d3d3d"))
            p.drawRoundedRect(groove_rect, 2, 2)
            if self.segments:
                for i, seg in enumerate(self.segments):
                    if i == self.active_segment_index: continue
                    self._draw_segment_on_groove(p, groove_rect, seg['start'], seg['end'], seg['speed'])
            if self.pending_start >= 0 and self.pending_end > self.pending_start:
                 self._draw_segment_on_groove(p, groove_rect, self.pending_start, self.pending_end, self.pending_speed, is_pending=True)
            f = QFont(self.font())
            f.setPointSize(max(10, f.pointSize()))
            p.setFont(f)
            fm = QFontMetrics(f)
            min_v, max_v = self.minimum(), self.maximum()
            range_ms = max_v - min_v
            if range_ms > 0 and groove_rect.width() > 10:
                major_tick_pixels = 120 
                num_major_ticks = max(1, int(round(groove_rect.width() / major_tick_pixels)))
                for i in range(num_major_ticks + 1):
                    ratio = i / float(num_major_ticks)
                    ms = min_v + (range_ms * ratio)
                    x = groove_rect.left() + int(ratio * (groove_rect.width() - 1))
                    is_obscured = False
                    s_rect, e_rect = self._get_handle_rect('start'), self._get_handle_rect('end')
                    if s_rect.isValid() and (abs(x - s_rect.center().x()) < (s_rect.width() / 2 + 5) or abs(x - e_rect.center().x()) < (e_rect.width() / 2 + 5)):
                        is_obscured = True
                    playhead_rect = self._get_playhead_rect()
                    if playhead_rect.isValid() and abs(x - playhead_rect.center().x()) < (playhead_rect.width() / 2 + 5):
                        is_obscured = True
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
        seg_color = QColor("#2ecc71") if speed > 1.101 else QColor("#e74c3c") if speed < 1.099 else QColor("#95a5a6")
        alpha = 180 if is_pending else 120
        seg_color.setAlpha(alpha); p.setBrush(seg_color); p.setPen(Qt.NoPen)
        s_pos, e_pos = self._map_value_to_pos(start_ms), self._map_value_to_pos(end_ms)
        if s_pos > e_pos: s_pos, e_pos = e_pos, s_pos
        h, y = 24, groove_rect.center().y() - 12
        seg_rect = QRect(s_pos, y, max(1, e_pos - s_pos), h); p.drawRect(seg_rect)
        if is_pending:
            p.setBrush(Qt.NoBrush)
            if self.m_ants_timer.isActive():
                pen = QPen(QColor("white"), 2, Qt.DashLine); pen.setDashOffset(self.m_ants_offset); p.setPen(pen)
            else: p.setPen(QPen(QColor("white"), 1, Qt.DashLine))
            p.drawRect(seg_rect)

class GranularSpeedEditor(QDialog):
    STYLE_METALLIC_GREY = """QPushButton { background: qlineargradient(x1:0, y1:0, x2:0, y2:1, stop:0 #7f8c8d, stop:0.5 #4d5656, stop:1 #2c3e50); color: #eeeeee; font-weight: bold; font-size: 11px; border-radius: 8px; border-top: 1px solid rgba(255, 255, 255, 0.2); border-left: 1px solid rgba(255, 255, 255, 0.2); border-bottom: 2px solid rgba(0, 0, 0, 0.9); border-right: 2px solid rgba(0, 0, 0, 0.9); padding: 10px 2px; } QPushButton:hover { border: 2px solid #7DD3FC; } QPushButton:pressed { background: #1a1a1a; border-top: 2px solid rgba(0, 0, 0, 1.0); border-left: 2px solid rgba(0, 0, 0, 1.0); padding-top: 11px; padding-left: 3px; }"""
    STYLE_METALLIC_RED = """QPushButton { background: qlineargradient(x1:0, y1:0, x2:0, y2:1, stop:0 #b00000, stop:0.5 #600000, stop:1 #300000); color: #eeeeee; font-weight: bold; font-size: 11px; border-radius: 8px; border-top: 1px solid rgba(255, 255, 255, 0.2); border-left: 1px solid rgba(255, 255, 255, 0.2); border-bottom: 2px solid rgba(0, 0, 0, 0.9); border-right: 2px solid rgba(0, 0, 0, 0.9); padding: 10px 2px; } QPushButton:hover { border: 2px solid #ff6666; } QPushButton:pressed { background: #110000; border-top: 2px solid rgba(0, 0, 0, 1.0); border-left: 2px solid rgba(0, 0, 0, 1.0); padding-top: 11px; padding-left: 3px; }"""
    STYLE_METALLIC_GREEN = """QPushButton { background: qlineargradient(x1:0, y1:0, x2:0, y2:1, stop:0 #1a7a1a, stop:0.5 #105410, stop:1 #0a300a); color: #eeeeee; font-weight: bold; font-size: 11px; border-radius: 8px; border-top: 1px solid rgba(255, 255, 255, 0.2); border-left: 1px solid rgba(255, 255, 255, 0.2); border-bottom: 2px solid rgba(0, 0, 0, 0.9); border-right: 2px solid rgba(0, 0, 0, 0.9); padding: 10px 2px; } QPushButton:hover { border: 2px solid #2ecc71; } QPushButton:pressed { background: #031003; border-top: 2px solid rgba(0, 0, 0, 1.0); border-left: 2px solid rgba(0, 0, 0, 1.0); padding-top: 11px; padding-left: 3px; }"""
    STYLE_METALLIC_BLUE = """QPushButton { background: qlineargradient(x1:0, y1:0, x2:0, y2:1, stop:0 #2e82a0, stop:0.5 #1e648c, stop:1 #123d50); color: #eeeeee; font-weight: bold; font-size: 11px; border-radius: 8px; border-top: 1px solid rgba(255, 255, 255, 0.2); border-left: 1px solid rgba(255, 255, 255, 0.2); border-bottom: 2px solid rgba(0, 0, 0, 0.9); border-right: 2px solid rgba(0, 0, 0, 0.9); padding: 10px 2px; } QPushButton:hover { border: 2px solid #7DD3FC; } QPushButton:pressed { background: #061a26; border-top: 2px solid rgba(0, 0, 0, 1.0); border-left: 2px solid rgba(0, 0, 0, 1.0); padding-top: 11px; padding-left: 3px; }"""
    STYLE_DELETE_ACTIVE = STYLE_METALLIC_RED 

    def _ensure_mpv_lock(self):
        if not hasattr(self, "_mpv_lock") or self._mpv_lock is None:
            self._mpv_lock = threading.RLock()
        if not hasattr(self, "_mpv_lock_timeout"): self._mpv_lock_timeout = 0.20
        return self._mpv_lock

    def _safe_mpv_set(self, prop, value):
        if not getattr(self, "player", None): return False
        lock = self._ensure_mpv_lock()
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
        lock = self._ensure_mpv_lock()
        try:
            if not lock.acquire(timeout=self._mpv_lock_timeout): return default
            try: return getattr(self.player, prop, default)
            finally: lock.release()
        except: return default

    def _safe_mpv_command(self, *args):
        if not getattr(self, "player", None): return False
        lock = self._ensure_mpv_lock()
        try:
            if not lock.acquire(timeout=self._mpv_lock_timeout): return False
            try: self.player.command(*args); return True
            finally: lock.release()
        except: return False

    def _init_state(self): self.selection_modified = False; self.list_modified = False

    def __init__(self, input_file_path, parent=None, initial_segments=None, base_speed=1.1, start_time_ms=0, mpv_instance=None, volume=100):
        super().__init__(parent)
        self._mpv_lock = threading.RLock()
        self._init_state()
        self.setWindowTitle("Granular Speed Editor")
        self.input_file_path = input_file_path
        self.parent_app = parent
        self.base_speed = max(0.5, min(3.1, float(base_speed)))
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
        self.start_time_ms = 0
        self.last_position_ms = 0
        self.volume = volume
        self.duration = clip_dur
        self.player = None
        self.mpv_instance = None
        self._owns_player = True
        self._parent_mpv_instance = mpv_instance
        self.timer = QTimer(self)
        self.timer.setInterval(40)
        self._mpv_lock_timeout = 0.20
        self.timer.timeout.connect(self.update_ui)
        self.is_playing = False
        self._last_rate_update = 0
        self._last_seek_ts = 0
        self._updating_ui = False
        self._start_manually_set = False
        self.restore_geometry()
        self.init_ui()
        if sys.platform == 'win32':
            os.environ["LC_NUMERIC"] = "C"
        QTimer.singleShot(100, self.setup_player)

    def _current_play_window_ms(self): return 0, int(self.duration)

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

    def _restore_parent_video_output(self): pass

    def _update_clear_all_btn_state(self):
        has_segments = len(self.speed_segments) > 0; self.clear_all_btn.setEnabled(has_segments); self.clear_all_btn.setStyleSheet(self.STYLE_METALLIC_RED if has_segments else self.STYLE_METALLIC_GREY); self._update_segment_counter()

    def _update_segment_counter(self):
        count = sum(1 for seg in self.speed_segments if abs(seg['speed'] - self.base_speed) > 0.01)
        if hasattr(self, "segment_counter_label"):
            self.segment_counter_label.setText(f"CURRENT DIFFERENT SPEED SEGMENTS: {count}"); self.segment_counter_label.setStyleSheet(f"color: {'#e74c3c' if count >= 10 else '#eeeeee'}; font-weight: bold; font-size: 13px;")

    def init_ui(self):
        self.setStyleSheet('''QWidget { background-color: #2c3e50; color: #ecf0f1; font-family: "Helvetica Neue", Arial, sans-serif; } QLabel { font-size: 12px; padding: 5px; } QToolTip { border: 1px solid #ecf0f1; background-color: #34495e; color: white; }''')
        main_layout = QVBoxLayout(self); main_layout.setContentsMargins(20, 20, 20, 30); main_layout.setSpacing(15); top_bar = QHBoxLayout()
        self.segment_counter_label = QLabel("CURRENT DIFFERENT SPEED SEGMENTS: 0"); self.segment_counter_label.setStyleSheet("color: #eeeeee; font-weight: bold; font-size: 13px;")
        top_bar.addWidget(self.segment_counter_label); top_bar.addStretch(); main_layout.addLayout(top_bar)
        self.video_frame = QWidget(); self.video_frame.setStyleSheet("background-color: black;"); self.video_frame.setAttribute(Qt.WA_DontCreateNativeAncestors); self.video_frame.setAttribute(Qt.WA_NativeWindow); main_layout.addWidget(self.video_frame, stretch=1)
        self.timeline = GranularTimelineSlider(self); self.timeline.setRange(0, int(self.duration)); self.timeline.sliderMoved.connect(self.seek_video); self.timeline.trim_times_changed.connect(self.on_trim_changed); main_layout.addWidget(self.timeline)

        from ui.styles import UIStyles
        controls_layout = QHBoxLayout(); controls_layout.setSpacing(14); controls_layout.addStretch(1)
        self.start_trim_button = QPushButton("SET START"); self.start_trim_button.setStyleSheet(self.STYLE_METALLIC_BLUE); self.start_trim_button.setFixedWidth(150); self.start_trim_button.setFixedHeight(35); self.start_trim_button.setCursor(Qt.PointingHandCursor); self.start_trim_button.clicked.connect(self.set_start); self.start_trim_button.setFocusPolicy(Qt.NoFocus); controls_layout.addWidget(self.start_trim_button)
        self.play_btn = QPushButton("PLAY"); self.play_btn.setStyleSheet(self.STYLE_METALLIC_BLUE); self.play_btn.setFixedWidth(150); self.play_btn.setFixedHeight(35); self.play_btn.setCursor(Qt.PointingHandCursor); self.play_btn.setIcon(self.style().standardIcon(QStyle.SP_MediaPlay)); self.play_btn.clicked.connect(self.toggle_play); self.play_btn.setFocusPolicy(Qt.NoFocus); controls_layout.addWidget(self.play_btn)
        self.end_trim_button = QPushButton("SET END"); self.end_trim_button.setStyleSheet(self.STYLE_METALLIC_BLUE); self.end_trim_button.setFixedWidth(150); self.end_trim_button.setFixedHeight(35); self.end_trim_button.setCursor(Qt.PointingHandCursor); self.end_trim_button.clicked.connect(self.set_end); self.end_trim_button.setFocusPolicy(Qt.NoFocus); controls_layout.addWidget(self.end_trim_button)
        controls_layout.addStretch(1); main_layout.addLayout(controls_layout)
        action_row = QHBoxLayout(); action_row.setContentsMargins(0, 10, 0, 0); self.clear_all_btn = QPushButton("CLEAR ALL"); self.clear_all_btn.setStyleSheet(self.STYLE_METALLIC_GREY); self.clear_all_btn.clicked.connect(self.clear_all_segments); self.clear_all_btn.setCursor(Qt.PointingHandCursor); self.clear_all_btn.setFocusPolicy(Qt.NoFocus); action_row.addWidget(self.clear_all_btn); action_row.addStretch(1)
        self.cancel_btn = QPushButton("CANCEL"); self.cancel_btn.setStyleSheet(self.STYLE_METALLIC_RED); self.cancel_btn.setFixedWidth(105); self.cancel_btn.setFixedHeight(35); self.cancel_btn.clicked.connect(self.reject); self.cancel_btn.setCursor(Qt.PointingHandCursor); self.cancel_btn.setFocusPolicy(Qt.NoFocus); action_row.addWidget(self.cancel_btn)
        action_row.addSpacing(20); self.save_btn = QPushButton("APPLY"); self.save_btn.setStyleSheet(self.STYLE_METALLIC_GREEN); self.save_btn.setFixedWidth(105); self.save_btn.setFixedHeight(35); self.save_btn.clicked.connect(self.accept); self.save_btn.setCursor(Qt.PointingHandCursor); self.save_btn.setFocusPolicy(Qt.NoFocus); action_row.addWidget(self.save_btn); action_row.addStretch(1)
        speed_container = QVBoxLayout(); speed_container.setSpacing(0); speed_container.setContentsMargins(0, 0, 0, 15) 
        lbl_speed = QLabel("Segment Speed"); lbl_speed.setStyleSheet("font-size: 11px; font-weight: bold; text-align: center; padding: 0; margin: 0;"); lbl_speed.setAlignment(Qt.AlignCenter)
        self.speed_spin = QDoubleSpinBox(); self.speed_spin.setRange(0.5, 3.1); self.speed_spin.setDecimals(1); self.speed_spin.setSingleStep(0.1); self.speed_spin.setValue(self.base_speed); self.speed_spin.setFixedWidth(65); self.speed_spin.setFixedHeight(30); self.speed_spin.setAlignment(Qt.AlignCenter); self.speed_spin.setCursor(Qt.PointingHandCursor); self.speed_spin.setStyleSheet(UIStyles.SPINBOX + "QDoubleSpinBox { font-size: 11px; margin: 0; padding: 0; }"); self.speed_spin.valueChanged.connect(self.on_speed_changed)
        speed_container.addWidget(lbl_speed); speed_container.addWidget(self.speed_spin, alignment=Qt.AlignCenter)
        fast_tick_layout = QVBoxLayout(); fast_tick_layout.setSpacing(2); fast_tick_layout.setContentsMargins(0, 15, 0, 15)
        self.up_fast_btn = ClickableLabel("▲▲"); self.up_fast_btn.setStyleSheet("color: #7DD3FC; font-weight: bold; font-size: 14px; background: transparent;"); self.up_fast_btn.clicked_callback = lambda: self.speed_spin.setValue(min(3.1, self.speed_spin.value() + 1.0))
        self.down_fast_btn = ClickableLabel("▼▼"); self.down_fast_btn.setStyleSheet("color: #7DD3FC; font-weight: bold; font-size: 14px; background: transparent;"); self.down_fast_btn.clicked_callback = lambda: self.speed_spin.setValue(max(0.5, self.speed_spin.value() - 1.0))
        fast_tick_layout.addWidget(self.up_fast_btn, 0, Qt.AlignCenter); fast_tick_layout.addWidget(self.down_fast_btn, 0, Qt.AlignCenter)
        btn_container_widget = QWidget(); btn_container_widget.setFixedWidth(180); btn_col = QVBoxLayout(btn_container_widget); btn_col.setContentsMargins(0, 0, 0, 0); btn_col.setSpacing(5)
        self.delete_seg_btn = QPushButton("DELETE SPEED SEGMENT"); self.delete_seg_btn.setStyleSheet(self.STYLE_METALLIC_GREY); self.delete_seg_btn.setCursor(Qt.PointingHandCursor); self.delete_seg_btn.clicked.connect(self.delete_current_selected_segment); self.delete_seg_btn.setFocusPolicy(Qt.NoFocus); self.delete_seg_btn.setFixedWidth(180); self.delete_seg_btn.setFixedHeight(35); self.delete_seg_btn.setEnabled(False); btn_col.addWidget(self.delete_seg_btn)
        self.add_seg_btn = QPushButton("ADD NEW SPEED SEGMENT"); self.add_seg_btn.setStyleSheet(self.STYLE_METALLIC_BLUE); self.add_seg_btn.setCursor(Qt.PointingHandCursor); self.add_seg_btn.clicked.connect(self.add_segment); self.add_seg_btn.setFocusPolicy(Qt.NoFocus); self.add_seg_btn.setFixedWidth(180); self.add_seg_btn.setFixedHeight(35); btn_col.addWidget(self.add_seg_btn)
        speed_and_btn_layout = QHBoxLayout(); speed_and_btn_layout.setSpacing(10); speed_and_btn_layout.addLayout(fast_tick_layout); speed_and_btn_layout.addLayout(speed_container); speed_and_btn_layout.addWidget(btn_container_widget); action_row.addLayout(speed_and_btn_layout); main_layout.addLayout(action_row); self._update_clear_all_btn_state()

    def setup_player(self):
        if not self.input_file_path: return
        try:
            self.video_frame.setAttribute(Qt.WA_NativeWindow); wid = int(self.video_frame.winId())
            self.player = MPVSafetyManager.create_safe_mpv(wid=wid, osc=False, hr_seek='yes', hwdec='auto', keep_open='yes', ytdl=False, vo='gpu' if sys.platform == 'win32' else 'gpu', extra_mpv_flags=[('force-window', 'no')])
            self.mpv_instance = self.player
            if not self.player: return
            self._safe_mpv_set("mute", False); self._safe_mpv_set("volume", max(1, self.volume))
            if not self._safe_mpv_command("loadfile", self.input_file_path, "replace"): return
        except Exception: return

        def _get_dur():
            if not getattr(self, "player", None): return
            try:
                dur = self._safe_mpv_get('duration', 0)
                if dur and dur > 0: self.timeline.setRange(0, int(self.duration)); self.timeline.set_duration_ms(int(self.duration)); self.timeline.set_segments(self.speed_segments); self.timeline.set_trim_times(0, 0); self.selection_modified = False; self.update_pending_visualization(); QTimer.singleShot(250, self._finalize_startup)
                else: QTimer.singleShot(100, _get_dur)
            except Exception: QTimer.singleShot(100, _get_dur)
        QTimer.singleShot(100, _get_dur)

    def _finalize_startup(self):
        if not self.player: return
        self._safe_mpv_set("pause", True)
        self._safe_mpv_command("seek", (self.abs_trim_start + self.start_time_ms) / 1000.0, "absolute", "exact")
        self.timeline.setValue(int(self.start_time_ms)); self.play_btn.setText("PLAY"); self.play_btn.setIcon(self.style().standardIcon(QStyle.SP_MediaPlay)); self.is_playing = False

    def _is_editing_widget_focused(self) -> bool:
        fw = QApplication.focusWidget()
        if fw is None: return False
        if isinstance(fw, (QLineEdit, QTextEdit, QPlainTextEdit, QAbstractSpinBox)): return True
        if isinstance(fw, QComboBox) and (fw.isEditable() or fw.hasFocus()): return True
        return False

    def keyPressEvent(self, event):
        if self._is_editing_widget_focused():
            super().keyPressEvent(event)
            return
        key = event.key()
        mods = event.modifiers()
        if key == Qt.Key_Space:
            self.toggle_play()
            event.accept()
        elif key == Qt.Key_Left:
            ms = -100 if mods == Qt.ControlModifier else (-3000 if mods == Qt.ShiftModifier else -500)
            self.seek_relative(ms)
            event.accept()
        elif key == Qt.Key_Right:
            ms = 100 if mods == Qt.ControlModifier else (3000 if mods == Qt.ShiftModifier else 500)
            self.seek_relative(ms)
            event.accept()
        elif key == Qt.Key_BracketLeft:
            self.set_start()
            event.accept()
        elif key == Qt.Key_BracketRight:
            self.set_end()
            event.accept()
        elif key == Qt.Key_Delete and self.player:
            rel_t = (self._safe_mpv_get('time-pos', 0) or 0) * 1000 - self.abs_trim_start
            for i, seg in enumerate(self.speed_segments):
                if seg['start'] <= rel_t <= seg['end']: self.delete_segment(i); break
            event.accept()
        else:
            super().keyPressEvent(event)

    def seek_relative(self, ms):
        if not self.player: return
        curr_rel = (self._safe_mpv_get('time-pos', 0) or 0) * 1000 - self.abs_trim_start
        new_rel = max(0, min(self.duration, curr_rel + ms))
        self.seek_video(new_rel); self.timeline.setValue(int(new_rel)); self.timeline.update()

    def _fmt(self, ms: int) -> str:
        s = max(0, int(ms) // 1000); h, s = divmod(s, 3600); m, s = divmod(s, 60)
        return f"{h:d}:{m:02d}:{s:02d}" if h else f"{m:d}:{s:02d}"

    def toggle_play(self):
        if not self.player: return
        if not self._safe_mpv_get("pause", True): self.pause_video()
        else:
            curr_rel = (self._safe_mpv_get('time-pos', 0) or 0) * 1000 - self.abs_trim_start
            if curr_rel >= self.duration - 100: self.seek_video(0); self.timeline.setValue(0)
            self._safe_mpv_set("pause", False); self.timer.start(); self.play_btn.setText("PAUSE"); self.play_btn.setIcon(self.style().standardIcon(QStyle.SP_MediaPause))

    def pause_video(self):
        if self.player: self._safe_mpv_set("pause", True)
        self.timer.stop(); self.play_btn.setText("PLAY"); self.play_btn.setIcon(self.style().standardIcon(QStyle.SP_MediaPlay))

    def seek_video(self, rel_pos):
        if not self.player: return
        now = import_time.time()
        if now - getattr(self, "_last_seek_ts", 0) < 0.1: return
        self._last_seek_ts = now; rel_pos = max(0, min(self.duration, rel_pos)); abs_pos = self.abs_trim_start + rel_pos
        self._safe_mpv_command("seek", abs_pos / 1000.0, "absolute", "exact")
        self.update_playback_speed(rel_pos)

    def update_ui(self):
        if not self.player or getattr(self, "_updating_ui", False): return
        self._updating_ui = True
        try:
            if not self.timeline.isSliderDown():
                curr_rel = (self._safe_mpv_get('time-pos', 0) or 0) * 1000 - self.abs_trim_start
                if curr_rel >= self.duration: self.pause_video(); curr_rel = self.duration; self._safe_mpv_set("time-pos", (self.abs_trim_start + curr_rel) / 1000.0)
                elif curr_rel < 0: curr_rel = 0; self._safe_mpv_set("time-pos", (self.abs_trim_start + curr_rel) / 1000.0)
                t_i = int(round(curr_rel)); self.timeline.blockSignals(True); self.timeline.setValue(t_i); self.timeline.blockSignals(False); self.timeline.update(); self.update_playback_speed(t_i); self.last_position_ms = t_i
        finally: self._updating_ui = False
    
    def update_playback_speed(self, rel_time):
        if not self.player: return
        target_speed = self.base_speed
        for seg in self.speed_segments:
            if seg['start'] <= rel_time < seg['end']: target_speed = seg['speed']; break
        if target_speed == self.base_speed:
            p_s, p_e = self.timeline.trimmed_start_ms, self.timeline.trimmed_end_ms
            if p_e > p_s + 10 and p_s <= rel_time < p_e: target_speed = self.speed_spin.value()
        now = import_time.time()
        if abs(self._safe_mpv_get("speed", 1.0) - target_speed) > 0.01 and now - self._last_rate_update > 0.1:
            if self._safe_mpv_set("speed", target_speed): self._last_rate_update = now

    def on_selection_cleared(self): self.update_pending_visualization(); self.selection_modified = False

    def set_start(self):
        if not self.player: return
        rel_t = (self._safe_mpv_get('time-pos', 0) or 0) * 1000 - self.abs_trim_start
        rel_t = max(0, min(self.duration, rel_t))
        for seg in self.speed_segments:
            if seg['start'] < rel_t < seg['end']:
                rel_t = seg['end']
                break
        if self.timeline.active_segment_index != -1:
            idx = self.timeline.active_segment_index
            low_limit = 0
            if idx > 0: low_limit = self.speed_segments[idx-1]['end']
            rel_t = max(low_limit, min(rel_t, self.speed_segments[idx]['end'] - 10))
            self.speed_segments[idx]['start'] = rel_t
            self.timeline.set_trim_times(rel_t, self.speed_segments[idx]['end'])
        else:
            self._start_manually_set = True
            self.timeline.active_segment_index = -1
            self.timeline.set_trim_times(rel_t, rel_t)
            self.delete_seg_btn.setEnabled(False)
            self.delete_seg_btn.setStyleSheet(self.STYLE_METALLIC_GREY)
        self.update_pending_visualization()
        self.selection_modified = True

    def set_end(self):
        if not self.player: return
        rel_t = (self._safe_mpv_get('time-pos', 0) or 0) * 1000 - self.abs_trim_start
        rel_t = max(0, min(self.duration, rel_t))
        if self.timeline.active_segment_index != -1:
            idx = self.timeline.active_segment_index
            high_limit = self.duration
            if idx < len(self.speed_segments) - 1: high_limit = self.speed_segments[idx+1]['start']
            rel_t = min(high_limit, max(rel_t, self.speed_segments[idx]['start'] + 10))
            self.speed_segments[idx]['end'] = rel_t
            self.timeline.set_trim_times(self.speed_segments[idx]['start'], rel_t)
            self.edit_segment(idx)
        else:
            if not getattr(self, "_start_manually_set", False):
                prev_end = 0
                for seg in self.speed_segments:
                    if seg['end'] <= rel_t:
                        prev_end = max(prev_end, seg['end'])
                smart_s = prev_end + 1000
                if smart_s >= rel_t: smart_s = prev_end
                self.timeline.set_trim_times(smart_s, rel_t)
            curr_s = self.timeline.trimmed_start_ms
            for seg in self.speed_segments:
                if seg['start'] < rel_t <= seg['end']:
                    rel_t = seg['start']
                    break
            rel_t = max(rel_t, curr_s + 10)
            self.timeline.set_trim_times(curr_s, rel_t)
            if rel_t > curr_s:
                new_idx = self.add_segment()
                if new_idx is not None:
                    self.edit_segment(new_idx)
            self._start_manually_set = False
        self.update_pending_visualization()
        self.pause_video()
        self.selection_modified = True

    def on_trim_changed(self, start, end):
        if self.timeline.active_segment_index != -1:
            idx = self.timeline.active_segment_index; self.speed_segments[idx]['start'] = start; self.speed_segments[idx]['end'] = end; self.list_modified = True
        self.update_pending_visualization(); self.selection_modified = True

    def on_speed_changed(self, val):
        updated = False
        if self.timeline.active_segment_index != -1:
            idx = self.timeline.active_segment_index
            if abs(self.speed_segments[idx]['speed'] - val) > 0.01: self.speed_segments[idx]['speed'] = val; updated = True
        else:
            for seg in self.speed_segments:
                if abs(seg['start'] - self.timeline.trimmed_start_ms) < 2 and abs(seg['end'] - self.timeline.trimmed_end_ms) < 2:
                    if abs(seg['speed'] - val) > 0.01: seg['speed'] = val; updated = True
        if updated: self.timeline.set_segments(self.speed_segments); self.list_modified = True
        self.update_pending_visualization(); self.pause_video(); self.selection_modified = True; self._update_segment_counter()

    def update_pending_visualization(self):
        if self.timeline.active_segment_index != -1:
            seg = self.speed_segments[self.timeline.active_segment_index]; self.timeline.set_pending_segment(seg['start'], seg['end'], seg['speed'])
        else: self.timeline.set_pending_segment(self.timeline.trimmed_start_ms, self.timeline.trimmed_end_ms, self.speed_spin.value())

    def clear_all_segments(self):
        if not self.speed_segments: return
        msg = QMessageBox(self); msg.setIcon(QMessageBox.Question); msg.setWindowTitle("Clear All"); msg.setText("Remove all speed segments?"); msg.setStandardButtons(QMessageBox.Yes | QMessageBox.No)
        for b in msg.findChildren(QPushButton): b.setCursor(Qt.PointingHandCursor)
        if msg.exec_() == QMessageBox.Yes:
            self.speed_segments = []; self.timeline.set_segments([]); self.timeline.active_segment_index = -1; self.timeline.set_trim_times(0, 0); self._start_manually_set = False; self.update_pending_visualization(); rel_t = (self._safe_mpv_get('time-pos', 0) or 0) * 1000 - self.abs_trim_start; self.update_playback_speed(rel_t); self.list_modified = True; self.selection_modified = False; self._update_clear_all_btn_state()

    def delete_segment(self, index):
        if 0 <= index < len(self.speed_segments):
            self.speed_segments.pop(index); self.timeline.set_segments(self.speed_segments); self.timeline.active_segment_index = -1; self.timeline.set_trim_times(0, 0); self._start_manually_set = False; self.update_pending_visualization(); rel_t = (self._safe_mpv_get('time-pos', 0) or 0) * 1000 - self.abs_trim_start; self.update_playback_speed(rel_t); self.list_modified = True; self.delete_seg_btn.setEnabled(False); self.delete_seg_btn.setStyleSheet(self.STYLE_METALLIC_GREY); self.timeline.stop_ants(); self._update_clear_all_btn_state()

    def delete_current_selected_segment(self):
        if self.timeline.active_segment_index != -1: self.delete_segment(self.timeline.active_segment_index)
        else:
            s, e = self.timeline.trimmed_start_ms, self.timeline.trimmed_end_ms
            for i, seg in enumerate(self.speed_segments):
                if abs(seg['start'] - s) < 2 and abs(seg['end'] - e) < 2: self.delete_segment(i); break

    def edit_segment(self, index):
        if 0 <= index < len(self.speed_segments):
            seg = self.speed_segments[index]; self.timeline.active_segment_index = index; self.timeline.set_trim_times(seg['start'], seg['end']); self.speed_spin.blockSignals(True); self.speed_spin.setValue(seg['speed']); self.speed_spin.blockSignals(False); self.update_pending_visualization(); self.selection_modified = False; self.delete_seg_btn.setEnabled(True); self.delete_seg_btn.setStyleSheet(self.STYLE_DELETE_ACTIVE); self.timeline.start_ants(); self._update_segment_counter()

    def add_segment(self):
        s, e, sp = self.timeline.trimmed_start_ms, self.timeline.trimmed_end_ms, float(self.speed_spin.value())
        if abs(sp - self.base_speed) > 0.01 and sum(1 for seg in self.speed_segments if abs(seg['speed'] - self.base_speed) > 0.01) >= 10:
            QMessageBox.warning(self, "Limit Reached", "Maximum of 10 unique speed segments reached."); return None
        if e <= s: return None
        new_seg, updated = {'start': s, 'end': e, 'speed': sp}, []
        for i, seg in enumerate(self.speed_segments):
            if i == self.timeline.active_segment_index: continue
            if abs(seg['start'] - s) < 2 and abs(seg['end'] - e) < 2: continue
            o_s, o_e = max(seg['start'], s), min(seg['end'], e)
            if o_s < o_e:
                if seg['start'] < o_s: updated.append({'start': seg['start'], 'end': o_s, 'speed': seg['speed']})
                if seg['end'] > o_e: updated.append({'start': o_e, 'end': seg['end'], 'speed': seg['speed']})
            else: updated.append(seg)
        updated.append(new_seg); updated.sort(key=lambda x: x['start']); merged = []
        if updated:
            curr = updated[0]
            for nxt in updated[1:]:
                if nxt['start'] <= curr['end'] + 5 and abs(curr['speed'] - nxt['speed']) < 0.01: curr['end'] = max(curr['end'], nxt['end'])
                else: merged.append(curr); curr = nxt
            merged.append(curr)
        self.speed_segments = merged; self.timeline.set_segments(self.speed_segments); self.timeline.active_segment_index = -1; self.timeline.set_trim_times(0, 0); self._start_manually_set = False; self.list_modified = True; self.delete_seg_btn.setEnabled(False); self.delete_seg_btn.setStyleSheet(self.STYLE_METALLIC_GREY); self.timeline.stop_ants(); self._update_clear_all_btn_state()
        for i, seg in enumerate(self.speed_segments):
            if abs(seg['start'] - s) < 5 or abs(seg['end'] - e) < 5: return i
        return None

    def cleanup(self):
        if hasattr(self, "player") and self.player:
            try:
                self._safe_mpv_set("pause", True)
                self._safe_mpv_command("stop")
                self._safe_mpv_set("wid", -1)
            except: pass
            try: MPVSafetyManager.safe_mpv_shutdown(self.player, timeout=1.5)
            except: pass
            self.player = None
            self.mpv_instance = None

        import gc
        gc.collect()

    def accept(self):
        self.speed_segments = [{'start': int(s['start'] + self.abs_trim_start), 'end': int(s['end'] + self.abs_trim_start), 'speed': s['speed']} for s in self.speed_segments]
        self.last_position_ms = int(self.last_position_ms + self.abs_trim_start); self.cleanup(); self.save_geometry(); super().accept()

    def reject(self):
        if self.list_modified or self.selection_modified:
            msg = QMessageBox(self); msg.setIcon(QMessageBox.Question); msg.setWindowTitle("Discard?"); msg.setText("Discard changes?"); msg.setStandardButtons(QMessageBox.Discard | QMessageBox.Cancel)
            for b in msg.findChildren(QPushButton): b.setCursor(Qt.PointingHandCursor)
            if msg.exec_() == QMessageBox.Cancel: return
        self.cleanup(); self.save_geometry(); super().reject()

    def closeEvent(self, event):
        self.reject()
        if self.isVisible(): event.ignore()
        else: event.accept()
