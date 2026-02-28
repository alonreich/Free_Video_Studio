try:
    import mpv
except Exception:
    mpv = None

import sys
import os
import time as import_time
from PyQt5.QtWidgets import (QDialog, QVBoxLayout, QHBoxLayout, QPushButton,
                             QLabel, QDoubleSpinBox, QWidget, QMessageBox, QStyle, QStyleOptionSlider, QApplication, QSizePolicy)

from PyQt5.QtCore import Qt, QTimer, QRect, QSize, pyqtSignal, QPoint
from PyQt5.QtGui import QPainter, QColor, QFont, QFontMetrics, QPen, QBrush, QLinearGradient, QCursor, QPainterPath, QIcon
from ui.widgets.trimmed_slider import TrimmedSlider

class GranularTimelineSlider(TrimmedSlider):
    def __init__(self, parent=None):
        super().__init__(parent)
        self.segments = []
        self.pending_start = -1
        self.pending_end = -1
        self.pending_speed = 1.1
        self.setContextMenuPolicy(Qt.CustomContextMenu)
        self.customContextMenuRequested.connect(self._show_context_menu)

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
                for i, seg in enumerate(self.segments):
                    if seg['start'] <= val <= seg['end']:
                        if hasattr(self.parent(), 'edit_segment'):
                            self.parent().edit_segment(i)
                            return
        super().mousePressEvent(e)

    def mouseMoveEvent(self, e):
        if self._dragging_handle == 'start':
            val = self._map_pos_to_value(e.pos().x())
            low = 0
            for seg in self.segments:
                if seg['end'] <= self.trimmed_start_ms:
                    low = max(low, seg['end'])
            val = max(low, min(val, self.trimmed_end_ms - 10))
            if val != self.trimmed_start_ms:
                self.trimmed_start_ms = val
                self.trim_times_changed.emit(self.trimmed_start_ms, self.trimmed_end_ms)
                self.update()
            return
        elif self._dragging_handle == 'end':
            val = self._map_pos_to_value(e.pos().x())
            high = self._duration_ms
            for seg in self.segments:
                if seg['start'] >= self.trimmed_end_ms:
                    high = min(high, seg['start'])
            val = min(high, max(val, self.trimmed_start_ms + 10))
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

    def paintEvent(self, event):
        p = QPainter(self)
        try:
            p.setRenderHint(QPainter.Antialiasing)
            p.fillRect(self.rect(), Qt.transparent)
            groove_rect = self._get_groove_rect()
            p.setPen(Qt.NoPen)
            p.setBrush(QColor("#3d3d3d"))
            p.drawRoundedRect(groove_rect, 2, 2)
            if self._duration_ms > 0:
                s_pos = self._map_value_to_pos(self.trimmed_start_ms)
                e_pos = self._map_value_to_pos(self.trimmed_end_ms)
                p.setBrush(QColor(0, 0, 0, 100))
                if s_pos > groove_rect.left():
                    p.drawRect(groove_rect.left(), groove_rect.top(), s_pos - groove_rect.left(), groove_rect.height())
                if e_pos < groove_rect.right():
                    p.drawRect(e_pos, groove_rect.top(), groove_rect.right() - e_pos, groove_rect.height())
            if self.segments:
                for seg in self.segments:
                    if abs(seg['start'] - self.pending_start) < 2 and abs(seg['end'] - self.pending_end) < 2:
                        continue
                    self._draw_segment_on_groove(p, groove_rect, seg['start'], seg['end'], seg['speed'])
            if self.pending_start >= 0 and self.pending_end > self.pending_start:
                 self._draw_segment_on_groove(p, groove_rect, self.pending_start, self.pending_end, self.pending_speed, is_pending=True)
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
                g.setColorAt(0.0, c1); g.setColorAt(0.4, c1)
                g.setColorAt(0.5, c2)
                g.setColorAt(0.6, c1); g.setColorAt(1.0, c1)
                p.setBrush(QBrush(g))
                p.drawRoundedRect(knob_rect, 4, 4)
        finally:
            if p.isActive():
                p.end()

    def _draw_segment_on_groove(self, p, groove_rect, start_ms, end_ms, speed, is_pending=False):
        if end_ms <= start_ms: return
        if speed > 1.101:
            seg_color = QColor("#2ecc71")
        elif speed < 1.099:
            seg_color = QColor("#e74c3c")
        else:
            seg_color = QColor("#95a5a6")
        alpha = 120
        if is_pending:
            alpha = 80
        seg_color.setAlpha(alpha)
        p.setBrush(seg_color)
        p.setPen(Qt.NoPen)
        s_pos = self._map_value_to_pos(start_ms)
        e_pos = self._map_value_to_pos(end_ms)
        if s_pos > e_pos: s_pos, e_pos = e_pos, s_pos
        h = 24
        y = groove_rect.center().y() - h // 2
        seg_rect = QRect(s_pos, y, max(1, e_pos - s_pos), h)
        p.drawRect(seg_rect)
        if is_pending:
            p.setBrush(Qt.NoBrush)
            p.setPen(QPen(QColor("white"), 1, Qt.DashLine))
            p.drawRect(seg_rect)

class GranularSpeedEditor(QDialog):
    def __init__(self, input_file_path, parent=None, initial_segments=None, base_speed=1.1, start_time_ms=0, mpv_instance=None, volume=100):
        super().__init__(parent)
        self.setWindowTitle("Granular Speed Editor")
        self.input_file_path = input_file_path
        self.parent_app = parent
        self.base_speed = max(0.5, min(3.1, float(base_speed)))
        self.speed_segments = list(initial_segments) if initial_segments else []
        self.start_time_ms = start_time_ms
        self.last_position_ms = start_time_ms
        self.volume = volume
        self.selection_modified = False
        self.list_modified = False
        self.duration = 0.0
        self.view_start_ms = 0
        self.view_end_ms = max(0, int(start_time_ms or 0))
        if self.parent_app and hasattr(self.parent_app, 'logger'):
             self.parent_app.logger.info(f"GRANULAR: Opened editor. Base Speed: {base_speed}x. Start Time: {start_time_ms}ms, Volume: {volume}")
        self.player = None
        self._owns_player = (mpv_instance is None)
        if mpv_instance:
            self.player = mpv_instance
        else:
            if mpv:
                try:
                    mpv_kwargs = {
                        'hr_seek': 'yes',
                        'hwdec': 'auto',
                        'keep_open': 'yes',
                        'ytdl': False,
                        'vo': 'gpu'
                    }
                    if sys.platform == 'win32':
                        mpv_kwargs['gpu-context'] = 'd3d11'
                    self.player = mpv.MPV(**mpv_kwargs)
                except Exception as e:
                    if self.parent_app and hasattr(self.parent_app, 'logger'):
                        self.parent_app.logger.error(f"GRANULAR: Failed to init MPV: {e}")
        self.timer = QTimer(self)
        self.timer.setInterval(100)
        self.timer.timeout.connect(self.update_ui)
        self.is_playing = False
        self._last_rate_update = 0
        self.restore_geometry()
        self.init_ui()
        if sys.platform == 'win32':
             os.environ["LC_NUMERIC"] = "C"
        QTimer.singleShot(50, self.setup_player)

    def _current_play_window_ms(self):
        """Playback window follows current SET START/SET END selection when valid."""
        try:
            s = int(getattr(self.timeline, "trimmed_start_ms", 0) or 0)
            e = int(getattr(self.timeline, "trimmed_end_ms", 0) or 0)
        except Exception:
            s, e = 0, 0
        if e > s + 10:
            return s, e
        fallback_s = int(getattr(self, "view_start_ms", 0) or 0)
        fallback_e = int(getattr(self, "view_end_ms", fallback_s) or fallback_s)
        if fallback_e <= fallback_s:
            fallback_e = fallback_s + 10
        return fallback_s, fallback_e

    def restore_geometry(self):
        def_w, def_h = 1500, 800
        if self.parent_app and hasattr(self.parent_app, 'config_manager'):
            geom = self.parent_app.config_manager.config.get('granular_editor_geometry')
            if geom and isinstance(geom, dict):
                w, h = geom.get('w', def_w), geom.get('h', def_h)
                x, y = geom.get('x', -1), geom.get('y', -1)
                if x == -1 or y == -1:
                    self.resize(w, h)
                    self._center_on_screen()
                else:
                    screen = QApplication.screenAt(QPoint(x, y))
                    if not screen:
                        screen = QApplication.primaryScreen()
                    avail = screen.availableGeometry()
                    w = min(w, avail.width())
                    h = min(h, avail.height())
                    x = max(avail.x(), min(x, avail.right() - w))
                    y = max(avail.y(), min(y, avail.bottom() - h))
                    self.setGeometry(x, y, w, h)
            else:
                self.resize(def_w, def_h)
                self._center_on_screen()
        else:
            self.resize(def_w, def_h)
            self._center_on_screen()

    def _center_on_screen(self):
        screen_geo = QApplication.primaryScreen().availableGeometry()
        x = screen_geo.x() + (screen_geo.width() - self.width()) // 2
        y = screen_geo.y() + (screen_geo.height() - self.height()) // 2
        self.move(x, y)

    def save_geometry(self):
        if self.parent_app and hasattr(self.parent_app, 'config_manager'):
            cfg = dict(getattr(self.parent_app.config_manager, 'config', {}) or {})
            cfg['granular_editor_geometry'] = {
                'x': self.geometry().x(),
                'y': self.geometry().y(),
                'w': self.geometry().width(),
                'h': self.geometry().height()
            }
            try:
                self.parent_app.config_manager.save_config(cfg)
            except Exception:
                pass

    def _restore_parent_video_output(self):
        """If we borrowed main-window MPV, rebind it back to main preview surface."""
        if self._owns_player or not self.player:
            return
        try:
            if self.parent_app and hasattr(self.parent_app, 'video_surface'):
                wid = int(self.parent_app.video_surface.winId())
                try:
                    self.player.wid = wid
                except Exception:
                    try:
                        self.player.command("set", "wid", wid)
                    except Exception:
                        pass
        except Exception:
            pass

    def init_ui(self):
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
            QToolTip {
                border: 1px solid #ecf0f1;
                background-color: #34495e;
                color: white;
            }
        ''')
        main_layout = QVBoxLayout(self)
        main_layout.setContentsMargins(20, 20, 20, 30)
        main_layout.setSpacing(15)
        top_bar = QHBoxLayout()
        top_bar.addStretch()
        main_layout.addLayout(top_bar)
        self.video_frame = QWidget()
        self.video_frame.setStyleSheet("background-color: black;")
        self.video_frame.setAttribute(Qt.WA_DontCreateNativeAncestors)
        self.video_frame.setAttribute(Qt.WA_NativeWindow)
        main_layout.addWidget(self.video_frame, stretch=1)
        self.timeline = GranularTimelineSlider(self)
        self.timeline.setRange(0, 0)
        self.timeline.sliderMoved.connect(self.seek_video)
        self.timeline.trim_times_changed.connect(self.on_trim_changed)
        main_layout.addWidget(self.timeline)

        from ui.styles import UIStyles
        controls_layout = QHBoxLayout()
        controls_layout.setSpacing(14)
        controls_layout.addStretch(1)
        self.start_trim_button = QPushButton("SET START")
        self.start_trim_button.setStyleSheet(UIStyles.BUTTON_WIZARD_BLUE)
        self.start_trim_button.setFixedWidth(150)
        self.start_trim_button.setFixedHeight(42)
        self.start_trim_button.setCursor(Qt.PointingHandCursor)
        self.start_trim_button.clicked.connect(self.set_start)
        self.start_trim_button.setFocusPolicy(Qt.NoFocus)
        controls_layout.addWidget(self.start_trim_button)
        self.play_btn = QPushButton("PLAY")
        self.play_btn.setStyleSheet(UIStyles.BUTTON_WIZARD_BLUE)
        self.play_btn.setFixedWidth(150)
        self.play_btn.setFixedHeight(42)
        self.play_btn.setCursor(Qt.PointingHandCursor)
        self.play_btn.setIcon(self.style().standardIcon(QStyle.SP_MediaPlay))
        self.play_btn.clicked.connect(self.toggle_play)
        self.play_btn.setFocusPolicy(Qt.NoFocus)
        controls_layout.addWidget(self.play_btn)
        self.end_trim_button = QPushButton("SET END")
        self.end_trim_button.setStyleSheet(UIStyles.BUTTON_WIZARD_BLUE)
        self.end_trim_button.setFixedWidth(150)
        self.end_trim_button.setFixedHeight(42)
        self.end_trim_button.setCursor(Qt.PointingHandCursor)
        self.end_trim_button.clicked.connect(self.set_end)
        self.end_trim_button.setFocusPolicy(Qt.NoFocus)
        controls_layout.addWidget(self.end_trim_button)
        controls_layout.addStretch(1)
        main_layout.addLayout(controls_layout)
        action_row = QHBoxLayout()
        action_row.setContentsMargins(0, 10, 0, 0)
        self.clear_all_btn = QPushButton("CLEAR ALL")
        self.clear_all_btn.setStyleSheet(UIStyles.get_3d_style("#7f8c8d", font_size=12, padding="10px 18px"))
        self.clear_all_btn.clicked.connect(self.clear_all_segments)
        self.clear_all_btn.setCursor(Qt.PointingHandCursor)
        self.clear_all_btn.setFocusPolicy(Qt.NoFocus)
        action_row.addWidget(self.clear_all_btn)
        action_row.addStretch(1)
        self.cancel_btn = QPushButton("CANCEL")
        self.cancel_btn.setStyleSheet(UIStyles.BUTTON_CANCEL)
        self.cancel_btn.setFixedWidth(105)
        self.cancel_btn.setFixedHeight(35)
        self.cancel_btn.clicked.connect(self.reject)
        self.cancel_btn.setCursor(Qt.PointingHandCursor)
        self.cancel_btn.setFocusPolicy(Qt.NoFocus)
        action_row.addWidget(self.cancel_btn)
        action_row.addSpacing(20)
        self.save_btn = QPushButton("APPLY")
        self.save_btn.setStyleSheet(UIStyles.get_3d_style("#146314", font_size=12, padding="10px 18px"))
        self.save_btn.setFixedWidth(105)
        self.save_btn.setFixedHeight(35)
        self.save_btn.clicked.connect(self.accept)
        self.save_btn.setCursor(Qt.PointingHandCursor)
        self.save_btn.setFocusPolicy(Qt.NoFocus)
        action_row.addWidget(self.save_btn)
        action_row.addStretch(1)
        speed_container = QVBoxLayout()
        speed_container.setSpacing(0)
        speed_container.setContentsMargins(0, 0, 0, 15) 
        lbl_speed = QLabel("Segment Speed")
        lbl_speed.setStyleSheet("font-size: 11px; font-weight: bold; text-align: center; padding: 0; margin: 0;")
        lbl_speed.setAlignment(Qt.AlignCenter)
        self.speed_spin = QDoubleSpinBox()
        self.speed_spin.setRange(0.5, 3.1)
        self.speed_spin.setDecimals(1)
        self.speed_spin.setSingleStep(0.1)
        self.speed_spin.setValue(self.base_speed)
        self.speed_spin.setFixedWidth(65)
        self.speed_spin.setFixedHeight(30)
        self.speed_spin.setAlignment(Qt.AlignCenter)
        self.speed_spin.setStyleSheet(UIStyles.SPINBOX + "QDoubleSpinBox { font-size: 11px; margin: 0; padding: 0; }")
        self.speed_spin.setCursor(Qt.PointingHandCursor)
        self.speed_spin.valueChanged.connect(self.on_speed_changed)
        speed_container.addWidget(lbl_speed)
        speed_container.addWidget(self.speed_spin, alignment=Qt.AlignCenter)
        self.add_seg_btn = QPushButton("ADD NEW SPEED SEGMENT")
        self.add_seg_btn.setStyleSheet(UIStyles.BUTTON_STANDARD)
        self.add_seg_btn.setCursor(Qt.PointingHandCursor)
        self.add_seg_btn.clicked.connect(self.add_segment)
        self.add_seg_btn.pressed.connect(lambda: self.parent_app.logger.info("UI_EVENT: ADD NEW SPEED SEGMENT button physically pressed.") if self.parent_app else None)
        self.add_seg_btn.setFocusPolicy(Qt.NoFocus)
        self.add_seg_btn.setFixedWidth(180)
        self.add_seg_btn.setFixedHeight(35)
        speed_and_btn_layout = QHBoxLayout()
        speed_and_btn_layout.setSpacing(10)
        speed_and_btn_layout.setAlignment(Qt.AlignVCenter)
        speed_and_btn_layout.addLayout(speed_container)
        speed_and_btn_layout.addWidget(self.add_seg_btn)
        action_row.addLayout(speed_and_btn_layout)
        main_layout.addLayout(action_row)

    def setup_player(self):
        if not self.input_file_path or not getattr(self, "player", None):
            return
        try:
            self.player.command("loadfile", self.input_file_path, "replace")
        except Exception as e:
            if self.parent_app and hasattr(self.parent_app, "logger"):
                self.parent_app.logger.warning(f"GRANULAR: loadfile failed: {e}")
            return
        try:
            wid = int(self.video_frame.winId())
            self.player.wid = wid
        except Exception: pass
        try:
            self.player.mute = False
            self.player.volume = max(1, self.volume)
        except Exception: pass

        def _get_dur():
            if not getattr(self, "player", None): return
            try:
                dur = getattr(self.player, 'duration', 0)
                if dur and dur > 0:
                    self.duration = dur * 1000.0
                    self.view_start_ms = self.parent_app.trim_start_ms if self.parent_app else 0
                    self.view_end_ms = self.parent_app.trim_end_ms if (self.parent_app and self.parent_app.trim_end_ms > 0) else self.duration
                    self.timeline.setRange(self.view_start_ms, self.view_end_ms)
                    self.timeline.set_duration_ms(self.duration)
                    self.timeline.set_segments(self.speed_segments)
                    self.timeline.set_trim_times(self.view_start_ms, self.view_start_ms)
                    self.selection_modified = False
                    self.update_pending_visualization()
                    QTimer.singleShot(250, self._finalize_startup)
                else:
                    QTimer.singleShot(100, _get_dur)
            except Exception:
                QTimer.singleShot(100, _get_dur)
        QTimer.singleShot(100, _get_dur)

    def _finalize_startup(self):
        if not self.player: return
        self.player.pause = True
        start_t = self.start_time_ms
        if start_t < self.view_start_ms or start_t > self.view_end_ms:
            start_t = self.view_start_ms
        self.player.seek(start_t / 1000.0, reference='absolute', precision='exact')
        self.timeline.setValue(int(start_t))
        self.play_btn.setText("PLAY")
        self.play_btn.setIcon(self.style().standardIcon(QStyle.SP_MediaPlay))
        self.is_playing = False

    def keyPressEvent(self, event):
        if event.key() == Qt.Key_Space:
            self.toggle_play()
            return
        elif event.key() == Qt.Key_Left:
            self.seek_relative(-500)
        elif event.key() == Qt.Key_Right:
            self.seek_relative(500)
        elif event.key() == Qt.Key_BracketLeft:
            self.set_start()
        elif event.key() == Qt.Key_BracketRight:
            self.set_end()
        elif event.key() == Qt.Key_Delete:
            if self.player:
                t = (getattr(self.player, 'time-pos', 0) or 0) * 1000
                deleted = False
                for i, seg in enumerate(self.speed_segments):
                    if seg['start'] <= t <= seg['end']:
                        self.delete_segment(i)
                        deleted = True
                        break
        else:
            super().keyPressEvent(event)

    def seek_relative(self, ms):
        if not self.player: return
        play_start, play_end = self._current_play_window_ms()
        curr = (getattr(self.player, 'time-pos', 0) or 0) * 1000
        new_pos = max(play_start, min(play_end, curr + ms))
        self.seek_video(new_pos)
        self.timeline.setValue(int(new_pos))
        self.timeline.update()

    def _fmt(self, ms: int) -> str:
        s = max(0, int(ms) // 1000)
        h, s = divmod(s, 3600)
        m, s = divmod(s, 60)
        return f"{h:d}:{m:02d}:{s:02d}" if h else f"{m:d}:{s:02d}"

    def toggle_play(self):
        if not self.player: return
        is_paused = getattr(self.player, "pause", True)
        play_start, play_end = self._current_play_window_ms()
        if not is_paused:
            self.pause_video()
        else:
            curr = (getattr(self.player, 'time-pos', 0) or 0) * 1000
            if curr < play_start or curr >= play_end - 100:
                self.seek_video(play_start)
                self.timeline.setValue(int(play_start))
            self.player.pause = False
            self.timer.start()
            self.play_btn.setText("PAUSE")
            self.play_btn.setIcon(self.style().standardIcon(QStyle.SP_MediaPause))

    def pause_video(self):
        if self.player:
            self.player.pause = True
        self.timer.stop()
        self.play_btn.setText("PLAY")
        self.play_btn.setIcon(self.style().standardIcon(QStyle.SP_MediaPlay))

    def seek_video(self, pos):
        if not self.player: return
        if False: self.player.time_pos = int(pos) / 1000.0
        play_start, play_end = self._current_play_window_ms()
        pos = max(play_start, min(play_end, pos))
        self.player.seek(pos / 1000.0, reference='absolute', precision='exact')
        self.update_playback_speed(pos)

    def update_ui(self):
        if not self.player: return
        if False: self.timeline.setValue(t)
        if not self.timeline.isSliderDown():
            t = (getattr(self.player, 'time-pos', 0) or 0) * 1000
            play_start, play_end = self._current_play_window_ms()
            if t >= play_end:
                self.pause_video()
                t = play_end
                self.player.seek(t / 1000.0, reference='absolute', precision='exact')
            elif t < play_start:
                t = play_start
                self.player.seek(t / 1000.0, reference='absolute', precision='exact')
            if t >= 0:
                self.timeline.blockSignals(True)
                self.timeline.setValue(int(t))
                self.timeline.blockSignals(False)
                self.timeline.update()
                self.update_playback_speed(t)
                self.last_position_ms = t
    
    def update_playback_speed(self, current_time):
        if not self.player: return
        target_speed = self.base_speed
        in_saved_segment = False
        for seg in self.speed_segments:
            if seg['start'] <= current_time < seg['end']:
                target_speed = seg['speed']
                in_saved_segment = True
                break
        if not in_saved_segment:
            p_start = self.timeline.trimmed_start_ms
            p_end = self.timeline.trimmed_end_ms
            if p_end > p_start + 10:
                if p_start <= current_time < p_end:
                    target_speed = self.speed_spin.value()
        now = import_time.time()
        curr_rate = getattr(self.player, "speed", 1.0)
        if abs(curr_rate - target_speed) > 0.01:
            is_scrubbing = self.timeline.isSliderDown()
            debounce_limit = 0.15 if is_scrubbing else 0.05
            if now - self._last_rate_update > debounce_limit:
                try:
                    self.player.speed = target_speed
                    self._last_rate_update = now
                except Exception:
                    pass

    def set_start(self):
        if not self.player: return
        t = (getattr(self.player, 'time-pos', 0) or 0) * 1000
        if self.parent_app and hasattr(self.parent_app, 'logger'):
            self.parent_app.logger.info(f"GRANULAR: [SET START] pressed at {t}ms.")
        for seg in self.speed_segments:
            if seg['start'] <= t < seg['end']:
                t = seg['end']
                if self.parent_app and hasattr(self.parent_app, 'logger'):
                    self.parent_app.logger.info(f"GRANULAR: Protected. Jumped START to segment edge at {t}ms.")
                break
        curr_s = self.timeline.trimmed_start_ms
        curr_e = self.timeline.trimmed_end_ms
        if self.selection_modified and curr_e > curr_s + 10:
            if self.parent_app and hasattr(self.parent_app, 'logger'):
                self.parent_app.logger.info(f"GRANULAR: Smart Auto-Add of previous range {curr_s}-{curr_e}ms.")
            self.add_segment()
        self.timeline.set_trim_times(t, t)
        self.update_pending_visualization()
        self.selection_modified = True

    def set_end(self):
        if not self.player: return
        t = (getattr(self.player, 'time-pos', 0) or 0) * 1000
        curr_s = self.timeline.trimmed_start_ms
        if self.parent_app and hasattr(self.parent_app, 'logger'):
            self.parent_app.logger.info(f"GRANULAR: [SET END] pressed at {t}ms.")
        for seg in self.speed_segments:
            if seg['start'] < t <= seg['end']:
                t = seg['start']
                if self.parent_app and hasattr(self.parent_app, 'logger'):
                    self.parent_app.logger.info(f"GRANULAR: Protected. Jumped END to segment edge at {t}ms.")
                break
        t = max(t, curr_s)
        self.timeline.set_trim_times(curr_s, t)
        if t > curr_s:
            if self.parent_app and hasattr(self.parent_app, 'logger'):
                self.parent_app.logger.info("GRANULAR: Smart Auto-Commit on SET END.")
            self.add_segment()
        self.update_pending_visualization()
        self.pause_video()
        self.selection_modified = True

    def on_trim_changed(self, start, end):
        self.update_pending_visualization()
        self.selection_modified = True

    def on_speed_changed(self, val):
        curr_s = self.timeline.trimmed_start_ms
        curr_e = self.timeline.trimmed_end_ms
        updated_any = False
        for seg in self.speed_segments:
            if abs(seg['start'] - curr_s) < 2 and abs(seg['end'] - curr_e) < 2:
                if abs(seg['speed'] - val) > 0.01:
                    seg['speed'] = val
                    updated_any = True
                    if self.parent_app and hasattr(self.parent_app, 'logger'):
                        self.parent_app.logger.info(f"GRANULAR: Live-updated segment speed to {val}x.")
        if updated_any:
            self.timeline.set_segments(self.speed_segments)
            self.list_modified = True
        self.update_pending_visualization()
        self.pause_video()
        self.selection_modified = True

    def update_pending_visualization(self):
        self.timeline.set_pending_segment(
            self.timeline.trimmed_start_ms,
            self.timeline.trimmed_end_ms,
            self.speed_spin.value()
        )

    def clear_all_segments(self):
        if not self.speed_segments: return
        reply = QMessageBox.question(self, "Clear All", "Remove all speed segments?", QMessageBox.Yes | QMessageBox.No)
        if reply == QMessageBox.Yes:
            if self.parent_app and hasattr(self.parent_app, 'logger'):
                self.parent_app.logger.info("GRANULAR: [CLEAR ALL] confirmed. Removing all segments.")
            self.speed_segments = []
            self.timeline.set_segments([])
            if self.player:
                t = (getattr(self.player, 'time-pos', 0) or 0) * 1000
                self.update_playback_speed(t)
            self.list_modified = True
            self.selection_modified = False

    def delete_segment(self, index):
        if 0 <= index < len(self.speed_segments):
            seg = self.speed_segments.pop(index)
            if self.parent_app and hasattr(self.parent_app, 'logger'):
                self.parent_app.logger.info(f"GRANULAR: Deleted segment {index}: {seg['start']}-{seg['end']} @ {seg['speed']}x")
            self.timeline.set_segments(self.speed_segments)
            if self.player:
                t = (getattr(self.player, 'time-pos', 0) or 0) * 1000
                self.update_playback_speed(t)
            self.list_modified = True
            self.timeline.setToolTip(f"Deleted segment {index+1}")
            QTimer.singleShot(1000, lambda: self.timeline.setToolTip(""))

    def edit_segment(self, index):
        if 0 <= index < len(self.speed_segments):
            target_seg = self.speed_segments[index]
            curr_s = self.timeline.trimmed_start_ms
            curr_e = self.timeline.trimmed_end_ms
            if self.selection_modified and curr_e > curr_s + 10:
                self.add_segment()
            found_idx = -1
            for i, s in enumerate(self.speed_segments):
                if abs(s['start'] - target_seg['start']) < 2 and abs(s['end'] - target_seg['end']) < 2:
                    found_idx = i
                    break
            if found_idx != -1:
                seg = self.speed_segments.pop(found_idx)
                if self.parent_app and hasattr(self.parent_app, 'logger'):
                    self.parent_app.logger.info(f"GRANULAR: [EDIT] Segment loaded. Range: {seg['start']}-{seg['end']}ms @ {seg['speed']}x")
                self.timeline.set_trim_times(seg['start'], seg['end'])
                self.speed_spin.blockSignals(True)
                self.speed_spin.setValue(seg['speed'])
                self.speed_spin.blockSignals(False)
                self.timeline.set_segments(self.speed_segments)
                self.update_pending_visualization()
                self.selection_modified = False
                self.list_modified = True

    def add_segment(self):
        if self.parent_app and hasattr(self.parent_app, 'logger'):
            self.parent_app.logger.info("!!! TRIGGER: add_segment function entered !!!")
        start = self.timeline.trimmed_start_ms
        end = self.timeline.trimmed_end_ms
        speed = float(self.speed_spin.value())
        if self.parent_app and hasattr(self.parent_app, 'logger'):
            self.parent_app.logger.info(f"GRANULAR: [ADD NEW SPEED SEGMENT] state check: Start={start}, End={end}, Speed={speed}. Current list size: {len(self.speed_segments)}")
        if end <= start:
            if self.parent_app and hasattr(self.parent_app, 'logger'):
                self.parent_app.logger.error(f"GRANULAR: FAILED ADDITION - Logic error: End ({end}) is not greater than Start ({start})")
            QMessageBox.warning(self, "Invalid Range", f"End time ({self._fmt(end)}) must be greater than start time ({self._fmt(start)}). Move the handles first!")
            return
        new_seg = {'start': start, 'end': end, 'speed': speed}
        updated = []
        for seg in self.speed_segments:
            s_s, s_e, s_sp = seg['start'], seg['end'], seg['speed']
            overlap_start = max(s_s, start)
            overlap_end = min(s_e, end)
            if overlap_start < overlap_end:
                if s_s < overlap_start:
                    updated.append({'start': s_s, 'end': overlap_start, 'speed': s_sp})
                if s_e > overlap_end:
                    updated.append({'start': overlap_end, 'end': s_e, 'speed': s_sp})
            else:
                updated.append(seg)
        updated.append(new_seg)
        updated.sort(key=lambda x: x['start'])
        merged = []
        if updated:
            current = updated[0]
            for next_seg in updated[1:]:
                if next_seg['start'] <= current['end'] + 5 and abs(current['speed'] - next_seg['speed']) < 0.01:
                    current['end'] = max(current['end'], next_seg['end'])
                else:
                    merged.append(current)
                    current = next_seg
            merged.append(current)
        self.speed_segments = merged
        self.timeline.set_segments(self.speed_segments)
        self.timeline.set_trim_times(start, end)
        self.update_pending_visualization()
        self.selection_modified = False
        self.list_modified = True
        if self.parent_app and hasattr(self.parent_app, 'logger'):
            self.parent_app.logger.info(f"GRANULAR: Addition successful. New segments list:")
            for i, s in enumerate(self.speed_segments):
                self.parent_app.logger.info(f"  [{i}] {s['start']}-{s['end']}ms @ {s['speed']}x")

    def accept(self):
        start = self.timeline.trimmed_start_ms
        end = self.timeline.trimmed_end_ms
        is_already_added = False
        for seg in self.speed_segments:
            if abs(seg['start'] - start) < 5 and abs(seg['end'] - end) < 5:
                is_already_added = True
                break
        if self.selection_modified and not is_already_added and end > start:
            if self.parent_app and hasattr(self.parent_app, 'logger'):
                self.parent_app.logger.info(f"GRANULAR: Auto-adding pending selection {start}-{end}ms on APPLY.")
            self.add_segment()
        self._restore_parent_video_output()
        self.save_geometry()
        super().accept()

    def reject(self):
        if self.list_modified or self.selection_modified:
            reply = QMessageBox.question(self, "Unsaved Changes", 
                "You have modified speed segments. Discard changes?", 
                QMessageBox.Discard | QMessageBox.Cancel)
            if reply == QMessageBox.Cancel:
                return
        self._restore_parent_video_output()
        self.save_geometry()
        super().reject()

    def closeEvent(self, event):
        if self.list_modified or self.selection_modified:
            reply = QMessageBox.question(self, "Unsaved Changes", 
                "You have modified speed segments. Discard changes?", 
                QMessageBox.Discard | QMessageBox.Cancel)
            if reply == QMessageBox.Cancel:
                event.ignore()
                return
        self.save_geometry()
        if self.player and self._owns_player:
            try:
                self.player.terminate()
                self.player = None
            except: pass
        else:
            self._restore_parent_video_output()
        super().closeEvent(event)









