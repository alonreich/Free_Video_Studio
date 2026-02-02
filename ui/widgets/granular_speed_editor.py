import vlc
import sys
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
        self.pending_speed = 1.0

    def set_segments(self, segments):
        self.segments = segments
        self.update()

    def set_pending_segment(self, start, end, speed):
        self.pending_start = start
        self.pending_end = end
        self.pending_speed = speed
        self.update()

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
                for seg in self.segments:
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
        if speed > 1.1:
            seg_color = QColor("#3498db")
        elif speed < 1.1:
            seg_color = QColor("#e74c3c")
        else:
            seg_color = QColor("#95a5a6") 
        alpha = 76
        if is_pending:
            alpha = 100
        seg_color.setAlpha(alpha)
        p.setBrush(seg_color)
        p.setPen(Qt.NoPen)
        s_pos = self._map_value_to_pos(start_ms)
        e_pos = self._map_value_to_pos(end_ms)
        if s_pos > e_pos: s_pos, e_pos = e_pos, s_pos
        h = 20
        y = groove_rect.center().y() - h // 2
        seg_rect = QRect(s_pos, y, e_pos - s_pos, h)
        p.drawRect(seg_rect)
        if is_pending:
            p.setBrush(Qt.NoBrush)
            p.setPen(QPen(QColor("white"), 1, Qt.DashLine))
            p.drawRect(seg_rect)

class GranularSpeedEditor(QDialog):
    def __init__(self, input_file_path, parent=None, initial_segments=None):
        super().__init__(parent)
        self.setWindowTitle("Granular Speed Editor")
        self.setWindowFlags(self.windowFlags() | Qt.WindowStaysOnTopHint)
        self.input_file_path = input_file_path
        self.parent_app = parent
        self.speed_segments = list(initial_segments) if initial_segments else []
        self.vlc_instance = vlc.Instance()
        self.vlc_player = self.vlc_instance.media_player_new()
        self.timer = QTimer(self)
        self.timer.setInterval(50)
        self.timer.timeout.connect(self.update_ui)
        self.is_playing = False
        self.restore_geometry()
        self.init_ui()
        self.setup_player()

    def restore_geometry(self):
        if self.parent_app and hasattr(self.parent_app, 'config_manager'):
            geom = self.parent_app.config_manager.config.get('granular_editor_geometry')
            if geom and isinstance(geom, dict):
                self.setGeometry(geom.get('x', 200), geom.get('y', 200), 
                                 geom.get('w', 1150), geom.get('h', 700))
            else:
                self.resize(1150, 700)
        else:
            self.resize(1150, 700)

    def save_geometry(self):
        if self.parent_app and hasattr(self.parent_app, 'config_manager'):
            cfg = self.parent_app.config_manager.config
            cfg['granular_editor_geometry'] = {
                'x': self.geometry().x(),
                'y': self.geometry().y(),
                'w': self.geometry().width(),
                'h': self.geometry().height()
            }

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
        ''')
        main_layout = QVBoxLayout(self)
        main_layout.setContentsMargins(20, 20, 20, 20)
        main_layout.setSpacing(15)
        self.video_frame = QWidget()
        self.video_frame.setStyleSheet("background-color: black;")
        main_layout.addWidget(self.video_frame, stretch=1)
        self.timeline = GranularTimelineSlider()
        self.timeline.setRange(0, 0)
        self.timeline.sliderMoved.connect(self.seek_video)
        self.timeline.trim_times_changed.connect(self.on_trim_changed)
        main_layout.addWidget(self.timeline)
        controls_layout = QHBoxLayout()
        controls_layout.setSpacing(14)
        _play_style = """
            QPushButton {
                background-color: #59A06D;
                color: white;
                font-size: 14px;
                padding: 4px 8px;
                border-radius: 6px;
            }
            QPushButton:hover { background-color: #6fb57f; }
            QPushButton:pressed { background-color: #4a865a; }
        """
        _std_btn_style = """
            QPushButton {
                background-color: #266b89;
                color: #ffffff;
                border: none;
                padding: 10px 18px;
                border-radius: 8px;
                font-weight: bold;
            }
            QPushButton:hover { background-color: #2980b9; }
            QPushButton:pressed { background-color: #1f5f7a; }
        """
        controls_layout.addStretch(1)
        self.start_trim_button = QPushButton("SET START")
        self.start_trim_button.setStyleSheet(_std_btn_style)
        self.start_trim_button.setFixedWidth(105)
        self.start_trim_button.setCursor(Qt.PointingHandCursor)
        self.start_trim_button.clicked.connect(self.set_start)
        controls_layout.addWidget(self.start_trim_button)
        self.play_btn = QPushButton("PLAY")
        self.play_btn.setStyleSheet(_play_style)
        self.play_btn.setFixedWidth(140)
        self.play_btn.setFixedHeight(35)
        self.play_btn.setCursor(Qt.PointingHandCursor)
        self.play_btn.setIcon(self.style().standardIcon(QStyle.SP_MediaPlay))
        self.play_btn.clicked.connect(self.toggle_play)
        controls_layout.addWidget(self.play_btn)
        self.end_trim_button = QPushButton("SET END")
        self.end_trim_button.setStyleSheet(_std_btn_style)
        self.end_trim_button.setFixedWidth(105)
        self.end_trim_button.setCursor(Qt.PointingHandCursor)
        self.end_trim_button.clicked.connect(self.set_end)
        controls_layout.addWidget(self.end_trim_button)
        controls_layout.addStretch(1)
        main_layout.addLayout(controls_layout)
        action_row = QHBoxLayout()
        action_row.setContentsMargins(0, 10, 0, 0)
        action_row.addStretch(1)
        self.cancel_btn = QPushButton("CANCEL")
        self.cancel_btn.setStyleSheet("background-color: #c0392b; color: white; font-weight: bold; border-radius: 8px; padding: 10px 18px;")
        self.cancel_btn.clicked.connect(self.reject)
        self.cancel_btn.setCursor(Qt.PointingHandCursor)
        action_row.addWidget(self.cancel_btn)
        action_row.addSpacing(20)
        self.save_btn = QPushButton("APPLY")
        self.save_btn.setStyleSheet(_std_btn_style)
        self.save_btn.clicked.connect(self.accept)
        self.save_btn.setCursor(Qt.PointingHandCursor)
        action_row.addWidget(self.save_btn)
        action_row.addStretch(1)
        speed_container = QVBoxLayout()
        speed_container.setSpacing(5)
        speed_label_row = QHBoxLayout()
        lbl_speed = QLabel("Speed Multiplier")
        lbl_speed.setStyleSheet("font-size: 11px; font-weight: bold; margin: 0; padding: 0;")
        speed_label_row.addStretch()
        speed_label_row.addWidget(lbl_speed)
        speed_label_row.addStretch()
        self.speed_spin = QDoubleSpinBox()
        self.speed_spin.setRange(0.5, 3.1)
        self.speed_spin.setDecimals(1)
        self.speed_spin.setSingleStep(0.1)
        self.speed_spin.setValue(1.1)
        self.speed_spin.setFixedWidth(55)
        self.speed_spin.setFixedHeight(35)
        self.speed_spin.setAlignment(Qt.AlignCenter)
        self.speed_spin.setStyleSheet("font-size: 11px; background-color: #4a667a; border: 1px solid #266b89; border-radius: 5px; color: #ecf0f1;")
        self.speed_spin.setCursor(Qt.PointingHandCursor)
        self.speed_spin.valueChanged.connect(self.on_speed_changed)
        spin_row = QHBoxLayout()
        spin_row.addStretch()
        spin_row.addWidget(self.speed_spin)
        spin_row.addStretch()
        speed_container.addLayout(speed_label_row)
        speed_container.addLayout(spin_row)
        self.add_seg_btn = QPushButton("ADD SEGMENT")
        self.add_seg_btn.setStyleSheet("background-color: #27ae60; color: white; font-weight: bold; border-radius: 8px; padding: 6px 12px; font-size: 11px;")
        self.add_seg_btn.setCursor(Qt.PointingHandCursor)
        self.add_seg_btn.clicked.connect(self.add_segment)
        speed_container.addWidget(self.add_seg_btn)
        action_row.addLayout(speed_container)
        main_layout.addLayout(action_row)

    def setup_player(self):
        if not self.input_file_path:
            return
        media = self.vlc_instance.media_new(self.input_file_path)
        self.vlc_player.set_media(media)
        if sys.platform.startswith('linux'):
            self.vlc_player.set_xwindow(self.video_frame.winId())
        elif sys.platform == "win32":
            self.vlc_player.set_hwnd(self.video_frame.winId())
        elif sys.platform == "darwin":
            self.vlc_player.set_nsobject(self.video_frame.winId())
        media.parse()
        self.duration = media.get_duration()
        self.timeline.setRange(0, self.duration)
        self.timeline.set_duration_ms(self.duration)
        self.timeline.set_segments(self.speed_segments)
        self.timeline.set_trim_times(0, self.duration)
        self.update_pending_visualization()

    def toggle_play(self):
        if self.vlc_player.is_playing():
            self.pause_video()
        else:
            self.vlc_player.play()
            self.timer.start()
            self.play_btn.setText("PAUSE")
            self.play_btn.setIcon(self.style().standardIcon(QStyle.SP_MediaPause))

    def pause_video(self):
        if self.vlc_player.is_playing():
            self.vlc_player.pause()
        self.timer.stop()
        self.play_btn.setText("PLAY")
        self.play_btn.setIcon(self.style().standardIcon(QStyle.SP_MediaPlay))

    def seek_video(self, pos):
        self.vlc_player.set_time(pos)
        self.update_playback_speed(pos)

    def update_ui(self):
        if not self.timeline.isSliderDown():
            t = self.vlc_player.get_time()
            self.timeline.setValue(t)
            self.update_playback_speed(t)
    
    def update_playback_speed(self, current_time):
        target_speed = 1.0
        in_pending = False
        if self.timeline.trimmed_start_ms <= current_time < self.timeline.trimmed_end_ms:
             target_speed = self.speed_spin.value()
             in_pending = True
        if not in_pending:
            for seg in self.speed_segments:
                if seg['start'] <= current_time < seg['end']:
                    target_speed = seg['speed']
                    break
        if abs(self.vlc_player.get_rate() - target_speed) > 0.05:
            self.vlc_player.set_rate(target_speed)

    def set_start(self):
        t = self.vlc_player.get_time()
        self.timeline.set_trim_times(t, self.timeline.trimmed_end_ms)
        self.update_pending_visualization()

    def set_end(self):
        t = self.vlc_player.get_time()
        self.timeline.set_trim_times(self.timeline.trimmed_start_ms, t)
        self.update_pending_visualization()
        self.pause_video()

    def on_trim_changed(self, start, end):
        self.update_pending_visualization()

    def on_speed_changed(self, val):
        self.update_pending_visualization()
        self.pause_video()

    def update_pending_visualization(self):
        self.timeline.set_pending_segment(
            self.timeline.trimmed_start_ms,
            self.timeline.trimmed_end_ms,
            self.speed_spin.value()
        )

    def add_segment(self):
        start = self.timeline.trimmed_start_ms
        end = self.timeline.trimmed_end_ms
        speed = self.speed_spin.value()
        if end <= start:
            QMessageBox.warning(self, "Invalid Range", "End time must be greater than start time.")
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
        self.speed_segments = updated
        self.timeline.set_segments(self.speed_segments)
        if self.parent_app and hasattr(self.parent_app, 'logger'):
            self.parent_app.logger.info(f"GRANULAR: Added segment {start}-{end}ms @ {speed}x. Total segments: {len(self.speed_segments)}")

    def closeEvent(self, event):
        self.save_geometry()
        self.vlc_player.stop()
        super().closeEvent(event)
