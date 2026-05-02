import os
import sys
import subprocess
import uuid
import tempfile
from threading import Thread
from PyQt5.QtCore import Qt, QTimer, QSize, QEvent, QCoreApplication
from PyQt5.QtGui import QPixmap, QPainter, QFont, QIcon
from PyQt5.QtWidgets import QGridLayout, QMessageBox, QHBoxLayout, QVBoxLayout, QFrame, QSlider, QLabel, QStyle, QPushButton, QSpinBox, QDoubleSpinBox, QCheckBox, QProgressBar, QWidget, QStyleOptionSpinBox, QStackedLayout, QLineEdit, QGraphicsOpacityEffect
from ui.widgets.clickable_button import ClickableButton
from ui.widgets.trimmed_slider import TrimmedSlider
from ui.widgets.drop_area import DropAreaFrame
from ui.styles import UIStyles
try:
    from ui.widgets.portrait_mask_overlay import PortraitMaskOverlay
except ImportError:
    PortraitMaskOverlay = None
try:
    from ui.widgets.timeline_overlay import TimelineOverlay
except ImportError:
    TimelineOverlay = None

class ClickableSpinBox(QDoubleSpinBox):
    def __init__(self, *args, **kwargs):
        super().__init__(*args, **kwargs)
        self.setCursor(Qt.PointingHandCursor)

    def mousePressEvent(self, event):
        if event.button() == Qt.LeftButton:
            opt = QStyleOptionSpinBox()
            self.initStyleOption(opt)
            up_rect = self.style().subControlRect(QStyle.CC_SpinBox, opt, QStyle.SC_SpinBoxUp, self)
            down_rect = self.style().subControlRect(QStyle.CC_SpinBox, opt, QStyle.SC_SpinBoxDown, self)
            if up_rect.contains(event.pos()) or down_rect.contains(event.pos()):
                super().mousePressEvent(event)
                return
        super().mousePressEvent(event)

class UiBuilderMixin:
    def _pick_thumbnail_from_current_frame(self):
        try:
            if not self.input_file_path or not os.path.exists(self.input_file_path):
                QMessageBox.information(self, 'No Video', 'Please load a video first.')
                return
            request_id = str(uuid.uuid4())
            self._current_thumb_request = request_id
            pos_ms = 0
            try:
                if hasattr(self, 'positionSlider'):
                    pos_ms = int(self.positionSlider.value())
            except:
                pass
            if not pos_ms and getattr(self, 'player', None):
                pos_ms = int((getattr(self.player, 'time-pos', 0) or 0) * 1000)
            pos_s = float(pos_ms) / 1000.0
            if self.original_duration_ms > 0 and pos_ms > self.original_duration_ms:
                pos_s = self.original_duration_ms / 1000.0
            self.selected_intro_abs_time = pos_s
            mm = int(self.selected_intro_abs_time // 60)
            ss = self.selected_intro_abs_time % 60.0
            self.thumb_pick_btn.setText('⏳ EXTRACTING... ⏳')
            self.thumb_pick_btn.setEnabled(False)
            self.status_update_signal.emit('📸 Extracting thumbnail... please wait.')

            def _safety_reset():
                if getattr(self, '_current_thumb_request', None) == request_id:
                    if self.thumb_pick_btn.text() == '⏳ EXTRACTING... ⏳':
                        self.thumb_pick_btn.setEnabled(True)
                        self.thumb_pick_btn.setText('📸 SET THUMBNAIL 📸')
                        self.status_update_signal.emit('❌ Extraction timed out.')
            QTimer.singleShot(15000, _safety_reset)
            temp_thumb = os.path.normpath(os.path.join(tempfile.gettempdir(), f'fvs_thumb_{os.getpid()}_{request_id[:8]}.jpg'))
            ffmpeg_path = os.path.normpath(os.path.join(getattr(self, 'bin_dir', ''), 'ffmpeg.exe'))
            if not os.path.exists(ffmpeg_path):
                ffmpeg_path = 'ffmpeg.exe'
            cmd = [ffmpeg_path, '-y', '-ss', f'{pos_s:.3f}', '-i', os.path.normpath(self.input_file_path), '-frames:v', '1', '-an', '-f', 'image2', '-q:v', '2', temp_thumb]

            def _run_extract(rid, m, s, p_ms):
                try:
                    res = subprocess.run(cmd, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, creationflags=134217728 if sys.platform == 'win32' else 0, timeout=12)
                except:
                    pass
                finally:
                    try:
                        is_latest = getattr(self, '_current_thumb_request', None) == rid
                        if hasattr(self, 'thumbnail_extracted_signal'):
                            self.thumbnail_extracted_signal.emit(m, s, is_latest)
                        else:
                            QTimer.singleShot(0, lambda: self._on_thumb_extracted(m, s, is_latest, p_ms))
                    except:
                        pass
            Thread(target=lambda: _run_extract(request_id, mm, ss, pos_ms), daemon=True).start()
        except Exception as e:
            self.thumb_pick_btn.setEnabled(True)
            self.thumb_pick_btn.setText('📸 SET THUMBNAIL 📸')
            QMessageBox.warning(self, 'Error', f'Failed to pick thumbnail: {e}')

    def _on_thumb_extracted(self, mm, ss, is_latest=True, pos_ms=0):
        self.thumb_pick_btn.setEnabled(True)
        if is_latest:
            self.thumb_pick_btn.setText(f'✅ THUMBNAIL SET ✅')
            self.status_update_signal.emit(f'✅ SUCCESS: Thumbnail set at {mm:02d}:{ss:05.2f} ✅')
            if hasattr(self, 'positionSlider'):
                self.positionSlider.set_thumbnail_pos_ms(pos_ms)
            QTimer.singleShot(4000, lambda: self.thumb_pick_btn.setText(f'📸 THUMBNAIL SET 📸'))
        else:
            self.thumb_pick_btn.setText('📸 SET THUMBNAIL 📸')

    def set_overlays_force_hidden(self, hidden):
        if hasattr(self, 'timeline_overlay') and self.timeline_overlay:
            self.timeline_overlay.set_force_hidden(hidden)
        if hasattr(self, 'portrait_mask_overlay') and self.portrait_mask_overlay:
            self.portrait_mask_overlay.set_force_hidden(hidden)

    def _on_boss_hp_toggled(self, checked):
        pass

    def _update_granular_button_state(self):
        if not hasattr(self, "granular_button"): return
        has_segments = bool(getattr(self, "speed_segments", []))
        has_freeze = bool(getattr(self, "freeze_images", []))
        if has_segments or has_freeze:
            self.granular_button.setText("REMOVE GRANULAR SPEEDS")
            self.granular_button.setStyleSheet(UIStyles.BUTTON_DANGER + "QPushButton { font-size: 10px; }")
        else:
            self.granular_button.setText("GRANULAR SPEED")
            self.granular_button.setStyleSheet(UIStyles.BUTTON_WIZARD_BLUE + "QPushButton { font-size: 10px; }")

    def _update_process_button_text(self) -> None:
        try:
            self._pulse_phase = (getattr(self, '_pulse_phase', 0) + 1) % 8
            if getattr(self, 'is_processing', False):
                dots = '.' * (1 + self._pulse_phase // 2)
                spinner = '⣾⣽⣻⢿⡿⣟⣯ⷿ'
                glyph = spinner[self._pulse_phase % len(spinner)]
                pm = QPixmap(24, 24)
                pm.fill(Qt.transparent)
                p = QPainter(pm)
                f = QFont(self.font())
                f.setPixelSize(14)
                f.setBold(True)
                p.setFont(f)
                p.setPen(Qt.black)
                p.drawText(pm.rect(), Qt.AlignCenter, glyph)
                p.end()
                self.process_button.setText(f'PROCESSING{dots}')
                self.process_button.setIcon(QIcon(pm))
                self.process_button.setIconSize(QSize(24, 24))
            else:
                self.process_button.setText('PROCESS')
                self.process_button.setIcon(QIcon())
        except:
            pass

    def _handle_granular_click(self):
        if hasattr(self, 'open_granular_speed_dialog'):
            self.open_granular_speed_dialog()

    def _ensure_default_trim(self):
        try:
            if self.end_minute_input.maximum() == 0 and self.positionSlider.maximum() == 0:
                QMessageBox.warning(self, 'No video', 'Please load a video file first.')
                return False
            if (self.start_minute_input.value() == 0 and self.start_second_input.value() == 0) and (self.end_minute_input.value() == 0 and self.end_second_input.value() == 0):
                self.end_minute_input.setValue(self.end_minute_input.maximum())
                self.end_second_input.setValue(self.end_second_input.maximum())
            return True
        except:
            return False

    def _on_process_clicked(self):
        try:
            if not getattr(self, 'scan_complete', False):
                self.process_button.setEnabled(False)
                self.process_button.setText('WAITING FOR SCAN...')
                self.status_update_signal.emit('⌛ Hardware scan in progress... Export will start automatically.')
                self._pending_process = True
                return
            if hasattr(self, 'portrait_mask_overlay') and self.portrait_mask_overlay:
                self.portrait_mask_overlay.hide()
            try:
                if hasattr(self, '_safe_stop_playback'):
                    self._safe_stop_playback()
                elif getattr(self, 'player', None):
                    self.player.stop()
            except:
                pass
            if not self._ensure_default_trim():
                return
            self.start_processing()
        except:
            QMessageBox.critical(self, 'Error', 'Could not start processing.')

    def _maybe_enable_process(self):
        try:
            path = getattr(self, 'input_file_path', None)
            has_video = bool(path and isinstance(path, str) and os.path.exists(path) and self.positionSlider.maximum() > 0)
            scan_done = getattr(self, 'scan_complete', False)
            if has_video and scan_done:
                if not self.process_button.isEnabled():
                    self.process_button.setEnabled(True)
                    self.process_button.setText('PROCESS')
                    self._ensure_default_trim()
            elif has_video and not scan_done:
                self.process_button.setEnabled(False)
                self.process_button.setText('SCANNING HW...')
            else:
                self.process_button.setEnabled(False)
                self.process_button.setText('PROCESS')
            if hasattr(self, 'music_button'):
                self.music_button.setCursor(Qt.PointingHandCursor if self.music_button.isEnabled() else Qt.ArrowCursor)
            if hasattr(self, 'granular_button'):
                self.granular_button.setCursor(Qt.PointingHandCursor if self.granular_button.isEnabled() else Qt.ArrowCursor)
        except:
            pass

    def launch_video_merger(self):
        p = os.path.join(self.base_dir, 'utilities', 'video_merger.py')
        if not os.path.exists(p):
            QMessageBox.critical(self, 'Missing Component', f'Missing: {p}')
            return
        try:
            from system.state_transfer import StateTransfer
            StateTransfer.save_state({'input_file': getattr(self, 'input_file_path', None), 'trim_start': getattr(self, 'trim_start_ms', 0), 'trim_end': getattr(self, 'trim_end_ms', 0), 'mobile_checked': self.mobile_checkbox.isChecked()})
            flags = 16 if sys.platform == 'win32' else 0
            subprocess.Popen([sys.executable, p], cwd=self.base_dir, creationflags=flags, close_fds=True, start_new_session=True)
            QCoreApplication.quit()
        except Exception as e:
            QMessageBox.critical(self, 'Launch Error', f'Failed: {e}')

    def eventFilter(self, obj, event):
        if event.type() in (QEvent.Resize, QEvent.Move):
            vs = getattr(self, 'video_surface', None)
            if obj is self or obj is vs:
                if event.type() == QEvent.Resize and obj is vs:
                    if hasattr(self, 'portrait_mask_overlay'):
                        self._update_portrait_mask_overlay_state()
                if hasattr(self, '_update_upload_hint_responsive'):
                    self._update_upload_hint_responsive()
        return False

    def init_ui(self):
        main_layout = QHBoxLayout()
        main_layout.setContentsMargins(0, 0, 0, 0)
        left_layout = QVBoxLayout()
        left_layout.setContentsMargins(0, 0, 0, 0)
        left_layout.setSpacing(0)
        self._top_row = QHBoxLayout()
        self._top_row.setSpacing(6)
        self._build_player_column()
        self._build_right_panel()
        self._top_row.addWidget(self.player_col_container, stretch=1)
        self._top_row.addWidget(self.right_panel)
        left_layout.addLayout(self._top_row)
        left_layout.addWidget(self.progress_bar)
        main_layout.addLayout(left_layout, stretch=1)
        self.central_widget.setLayout(main_layout)
        if hasattr(self, '_init_upload_hint_blink'):
            self._init_upload_hint_blink()
        self._ensure_overlay_widgets()
        self._hide_processing_overlay()
        self._maybe_enable_process()
        QTimer.singleShot(0, self._adjust_trim_margins)
        QTimer.singleShot(0, self.apply_master_volume)
        QTimer.singleShot(0, lambda: self.setFocus(Qt.ActiveWindowFocusReason))

    def _build_player_column(self):
        player_col = QVBoxLayout()
        player_col.setContentsMargins(0, 0, 0, 0)
        player_col.setSpacing(6)
        video_and_volume = QHBoxLayout()
        video_and_volume.setContentsMargins(0, 0, 0, 0)
        video_and_volume.setSpacing(2)
        self._init_volume_slider()
        video_and_volume.addWidget(self.volume_container, 0, Qt.AlignHCenter)
        self._init_video_surface()
        video_and_volume.addWidget(self.video_frame, stretch=1)
        player_col.addLayout(video_and_volume)
        player_col.setStretch(0, 1)
        badge_style = "background: rgb(52, 152, 219); color: white; border-radius: 4px; padding: 2px 6px; font-weight: bold; font-size: 11px; border: none;"
        self._top_badge_container = QFrame(self)
        self._top_badge_container.setAttribute(Qt.WA_TransparentForMouseEvents)
        self._top_badge_container.hide()
        tl = QHBoxLayout(self._top_badge_container); tl.setContentsMargins(0,0,0,0)
        self._main_time_badge_top = QLabel(self._top_badge_container)
        self._main_time_badge_top.setStyleSheet(badge_style)
        tl.addWidget(self._main_time_badge_top)
        self._top_badge_opacity = QGraphicsOpacityEffect(self._top_badge_container)
        self._top_badge_container.setGraphicsEffect(self._top_badge_opacity)
        self._top_badge_opacity.setOpacity(0.0)
        self._bottom_badge_container = QFrame(self)
        self._bottom_badge_container.setAttribute(Qt.WA_TransparentForMouseEvents)
        self._bottom_badge_container.hide()
        bl = QHBoxLayout(self._bottom_badge_container); bl.setContentsMargins(0,0,0,0)
        self._main_time_badge_bottom = QLabel(self._bottom_badge_container)
        self._main_time_badge_bottom.setStyleSheet(badge_style)
        bl.addWidget(self._main_time_badge_bottom)
        self._bottom_badge_opacity = QGraphicsOpacityEffect(self._bottom_badge_container)
        self._bottom_badge_container.setGraphicsEffect(self._bottom_badge_opacity)
        self._bottom_badge_opacity.setOpacity(0.0)
        self._init_trim_controls()
        trim_c = QHBoxLayout()
        trim_c.setContentsMargins(10, 0, 10, 0)
        trim_c.addSpacing(15)
        trim_c.addWidget(self.thumb_pick_btn, 0, Qt.AlignLeft)
        trim_c.addSpacing(30)
        trim_c.addWidget(self.boss_hp_checkbox, 0, Qt.AlignLeft)
        trim_c.addStretch(1)
        trim_c.addLayout(self.trim_layout)
        trim_c.addStretch(1)
        player_col.addSpacing(8)
        player_col.addLayout(trim_c)
        self._init_process_controls()
        player_col.addLayout(self.center_btn_container)
        player_col.addSpacing(10)
        self._init_status_bar()
        self.player_col_container = QWidget()
        self.player_col_container.setLayout(player_col)
        self.player_col_container.installEventFilter(self)

    def _set_video_controls_enabled(self, enabled: bool):
        widgets = [self.playPauseButton, self.start_trim_button, self.end_trim_button, self.thumb_pick_btn, self.boss_hp_checkbox, self.quality_slider, self.speed_spinbox, self.granular_button, self.mobile_checkbox, self.teammates_checkbox, self.no_fade_checkbox, self.portrait_text_input, self.music_button, self.positionSlider, self.start_minute_input, self.start_second_input, self.start_ms_input, self.end_minute_input, self.end_second_input, self.end_ms_input]
        for w in widgets:
            if w is not None:
                w.setEnabled(enabled)
        if enabled:
            is_m = self.mobile_checkbox.isChecked()
            self.teammates_checkbox.setEnabled(is_m)
            self.portrait_text_input.setEnabled(is_m)

    def _init_volume_slider(self):
        v_l = QVBoxLayout()
        v_l.setContentsMargins(0, 15, 0, 15)
        v_l.setSpacing(0)
        self.volume_slider = QSlider(Qt.Vertical)
        self.volume_slider.setFixedWidth(40)
        self.volume_slider.setRange(0, 100)
        self.volume_slider.setCursor(Qt.PointingHandCursor)
        self.volume_slider.setToolTip("Adjust Video Volume (Up/Down / Scroll)")
        self.volume_slider.setStyleSheet(UIStyles.SLIDER_VOLUME_VERTICAL_METALLIC)
        try:
            eff = int(self.config_manager.config.get('video_mix_volume', 100))
        except:
            eff = 100
        self.volume_slider.setValue(eff)
        self.volume_slider.valueChanged.connect(self._on_master_volume_changed)
        self.volume_slider.sliderMoved.connect(lambda _: self._update_volume_badge())
        self.volume_badge = QLabel('0%', self.volume_slider)
        self.volume_badge.setStyleSheet('color: white; background: rgba(0,0,0,160); padding: 2px 6px; border-radius: 6px; font-weight: bold;')
        self.volume_badge.hide()
        v_l.addWidget(self.volume_badge, 0, Qt.AlignHCenter)
        v_l.addWidget(self.volume_slider, 1, Qt.AlignHCenter)
        self.volume_container = QWidget()
        self.volume_container.setLayout(v_l)
        self.volume_container.setFixedWidth(40)

    def _init_video_surface(self):
        if hasattr(self, 'video_frame') and self.video_frame is not None:
            return
        self.video_frame = QFrame()
        self.video_frame.setMinimumHeight(360)
        self.video_frame.setFocusPolicy(Qt.NoFocus)
        self.video_stack = QStackedLayout(self.video_frame)
        self.video_stack.setStackingMode(QStackedLayout.StackAll)
        self.video_surface = QWidget()
        self.video_surface.setStyleSheet('background-color: black;')
        self.video_surface.setAttribute(Qt.WA_DontCreateNativeAncestors)
        self.video_surface.setAttribute(Qt.WA_NativeWindow)
        self.video_surface.setAttribute(Qt.WA_OpaquePaintEvent)
        self.video_surface.setAttribute(Qt.WA_NoSystemBackground)
        self.video_surface.setAutoFillBackground(False)
        self.video_surface.winId()
        self.video_stack.addWidget(self.video_surface)
        self.video_viewport = self.video_surface
        self.hint_overlay_widget = QWidget()
        self.hint_overlay_widget.setAttribute(Qt.WA_TransparentForMouseEvents, True)
        self.hint_overlay_widget.setAttribute(Qt.WA_TranslucentBackground, True)
        self.hint_overlay_widget.setStyleSheet('background: transparent;')
        self.hint_group_container = QWidget(self.hint_overlay_widget)
        self.hint_group_container.setAttribute(Qt.WA_TranslucentBackground, True)
        self.hint_group_container.setStyleSheet('background: transparent;')
        self.hint_group_layout = QHBoxLayout(self.hint_group_container)
        self.hint_group_layout.setContentsMargins(0, 0, 0, 0)
        self.upload_hint_container = QFrame()
        self.upload_hint_container.setObjectName('uploadHintContainer')
        hi_l = QVBoxLayout(self.upload_hint_container)
        self.upload_hint_label = QLabel('')
        self.upload_hint_label.hide()
        hi_l.addWidget(self.upload_hint_label)
        self.upload_hint_arrow = QLabel()
        self.hint_group_layout.addWidget(self.upload_hint_container)
        self.hint_group_layout.addWidget(self.upload_hint_arrow)
        self.preview_notice_container = QFrame(self.hint_overlay_widget)
        self.preview_notice_container.setObjectName('previewIsolationContainer')
        self.preview_notice_container.hide()
        pn_l = QVBoxLayout(self.preview_notice_container)
        pn_l.setContentsMargins(18, 16, 18, 16)
        pn_l.setSpacing(6)
        self.preview_notice_title = QLabel('Diagnostic Isolation Active')
        self.preview_notice_title.setAlignment(Qt.AlignCenter)
        self.preview_notice_detail = QLabel(
            'CPU-only crash triage is active. Video should remain visible using software decode,\n'
            'while hardware acceleration stays disabled and extra containment guards are applied.'
        )
        self.preview_notice_detail.setAlignment(Qt.AlignCenter)
        self.preview_notice_detail.setWordWrap(True)
        pn_l.addWidget(self.preview_notice_title)
        pn_l.addWidget(self.preview_notice_detail)
        self.video_stack.addWidget(self.hint_overlay_widget)
        if PortraitMaskOverlay:
            self.portrait_mask_overlay = PortraitMaskOverlay(self)
            self.portrait_mask_overlay.hide()
        else:
            self.portrait_mask_overlay = None
        self.video_surface.installEventFilter(self)
        self.video_frame.installEventFilter(self)
        self.video_stack.setCurrentWidget(self.hint_overlay_widget)

    def _init_trim_controls(self):
        self.playPauseButton = QPushButton('PLAY')
        self.playPauseButton.setStyleSheet(UIStyles.BUTTON_WIZARD_GREEN)
        self.playPauseButton.setFixedSize(80, 35)
        self.playPauseButton.setCursor(Qt.PointingHandCursor)
        self.playPauseButton.setToolTip("Toggle video playback (Space)")
        self.playPauseButton.clicked.connect(self.toggle_play_pause)
        self.playPauseButton.setEnabled(False)
        self.thumb_pick_btn = QPushButton('📸 SET THUMBNAIL 📸')
        self.thumb_pick_btn.setStyleSheet(UIStyles.BUTTON_WIZARD_BLUE)
        self.thumb_pick_btn.setFixedSize(100, 35)
        self.thumb_pick_btn.setCursor(Qt.PointingHandCursor)
        self.thumb_pick_btn.setToolTip("Use current frame as video intro/thumbnail")
        self.thumb_pick_btn.clicked.connect(self._pick_thumbnail_from_current_frame)
        self.thumb_pick_btn.setEnabled(False)
        self.start_minute_input = QSpinBox()
        self.start_second_input = QSpinBox()
        self.start_ms_input = QSpinBox()
        self.end_minute_input = QSpinBox()
        self.end_second_input = QSpinBox()
        self.end_ms_input = QSpinBox()
        for s in (self.start_minute_input, self.start_second_input, self.start_ms_input, self.end_minute_input, self.end_second_input, self.end_ms_input):
            s.setFixedHeight(35)
            s.setMaximumWidth(48)
            s.setEnabled(False)
            s.setCursor(Qt.PointingHandCursor)
            s.setToolTip("Fine-tune trim timing")
            s.valueChanged.connect(self._on_trim_spin_changed)
        self.start_trim_button = QPushButton('SET START')
        self.start_trim_button.setStyleSheet(UIStyles.BUTTON_WIZARD_BLUE)
        self.start_trim_button.setFixedSize(100, 35)
        self.start_trim_button.setCursor(Qt.PointingHandCursor)
        self.start_trim_button.setToolTip("Set trim start to current position ([)")
        self.start_trim_button.clicked.connect(self.set_start_time)
        self.start_trim_button.setEnabled(False)
        self.end_trim_button = QPushButton('SET END')
        self.end_trim_button.setStyleSheet(UIStyles.BUTTON_WIZARD_BLUE)
        self.end_trim_button.setFixedSize(100, 35)
        self.end_trim_button.setCursor(Qt.PointingHandCursor)
        self.end_trim_button.setToolTip("Set trim end to current position (])")
        self.end_trim_button.clicked.connect(self.set_end_time)
        self.end_trim_button.setEnabled(False)

        def _ml(t, s):
            l = QHBoxLayout()
            l.setContentsMargins(0, 0, 0, 0)
            lbl = QLabel(t)
            lbl.setStyleSheet('font-size: 10px;')
            l.addWidget(lbl)
            l.addWidget(s)
            return l
        self.start_ms_input.hide()
        self.end_ms_input.hide()
        self.boss_hp_checkbox = QCheckBox('Boss HP')
        self.boss_hp_checkbox.setStyleSheet('font-size: 10px;')
        self.boss_hp_checkbox.setCursor(Qt.PointingHandCursor)
        self.boss_hp_checkbox.setToolTip("Enable social media safe-zone for Boss HP bars")
        self.boss_hp_checkbox.setEnabled(False)
        self.trim_layout = QHBoxLayout()
        self.trim_layout.setSpacing(14)
        self.trim_layout.addLayout(_ml('Min:', self.start_minute_input))
        self.trim_layout.addLayout(_ml('Sec:', self.start_second_input))
        self.trim_layout.addWidget(self.start_trim_button)
        self.trim_layout.addWidget(self.playPauseButton)
        self.trim_layout.addWidget(self.end_trim_button)
        self.trim_layout.addLayout(_ml('Min:', self.end_minute_input))
        self.trim_layout.addLayout(_ml('Sec:', self.end_second_input))

    def _init_process_controls(self):
        self.quality_label = QLabel('OUTPUT FILE SIZE')
        self.quality_label.setStyleSheet('font-size: 11px; font-weight: bold;')

        from ui.widgets.spinning_wheel_slider import SpinningWheelSlider
        self.quality_slider = SpinningWheelSlider()
        self.quality_slider.setRange(0, 20)
        mb_labels = [f'{5 + i * 5}MB' for i in range(20)] + ['ORIGINAL QUALITY']
        self.quality_slider.setLabels(mb_labels)
        self.quality_slider.setValue(7)
        self.quality_slider.setFixedSize(180, 35)
        self.quality_slider.setEnabled(False)
        self.quality_slider.setCursor(Qt.PointingHandCursor)
        self.quality_slider.setToolTip("Adjust target output file size (MB)")
        self.quality_slider.valueChanged.connect(lambda _: self._update_quality_label())
        self.quality_value_label = QLabel('')
        self.quality_value_label.setStyleSheet('font-size: 10px; font-weight: bold;')
        self.quality_value_label.setMinimumWidth(100)
        self.quality_value_label.setAlignment(Qt.AlignCenter)
        q_v = QVBoxLayout()
        q_v.setContentsMargins(0, 0, 0, 0)
        q_v.setSpacing(2)
        q_v.addWidget(self.quality_label, 0, Qt.AlignHCenter)
        q_v.addWidget(self.quality_slider, 0, Qt.AlignHCenter)
        q_v.addWidget(self.quality_value_label, 0, Qt.AlignHCenter)
        self.quality_container = QWidget()
        self.quality_container.setLayout(q_v)
        self.process_button = QPushButton('PROCESS')
        self.process_button.setFixedSize(140, 50)
        self.process_button.setCursor(Qt.PointingHandCursor)
        self.process_button.setStyleSheet(UIStyles.BUTTON_WIZARD_GREEN)
        self.process_button.setToolTip("Start video processing (Enter)")
        self.process_button.clicked.connect(self._on_process_clicked)
        self.process_button.setEnabled(False)
        self.cancel_button = ClickableButton('CANCEL')
        self.cancel_button.setFixedSize(140, 50)
        self.cancel_button.setCursor(Qt.PointingHandCursor)
        self.cancel_button.setStyleSheet(UIStyles.BUTTON_CANCEL)
        self.cancel_button.setToolTip("Abort the current processing task")
        self.cancel_button.setVisible(False)
        self.cancel_button.clicked.connect(self.cancel_processing)
        self.is_processing = False
        self._pulse_phase = 0
        self._pulse_timer = QTimer(self)
        self._pulse_timer.timeout.connect(self._update_process_button_text)
        btn_l = QHBoxLayout()
        btn_l.setSpacing(20)
        btn_l.addWidget(self.cancel_button)
        btn_l.addWidget(self.process_button)
        self.speed_spinbox = ClickableSpinBox()
        self.speed_spinbox.setRange(0.5, 3.1)
        self.speed_spinbox.setValue(1.1)
        self.speed_spinbox.setFixedWidth(55)
        self.speed_spinbox.setFixedHeight(35)
        self.speed_spinbox.setEnabled(False)
        self.speed_spinbox.setCursor(Qt.PointingHandCursor)
        self.speed_spinbox.setToolTip("Adjust overall playback speed (Up/Down)")
        self.speed_spinbox.valueChanged.connect(self._on_speed_changed)
        s_l = QVBoxLayout()
        s_l.setSpacing(2)
        speed_title = QLabel('Speed Multiplier')
        speed_title.setStyleSheet('font-size: 11px; font-weight: bold;')
        s_l.addWidget(speed_title, 0, Qt.AlignHCenter)
        s_l.addWidget(self.speed_spinbox, 0, Qt.AlignHCenter)
        speed_w = QWidget()
        speed_w.setLayout(s_l)
        self.granular_checkbox = QCheckBox('GRANULAR SPEED')
        self.granular_checkbox.hide()
        self.granular_checkbox.setCursor(Qt.PointingHandCursor)
        self.granular_checkbox.toggled.connect(lambda _: self._update_quality_label())
        self.granular_button = QPushButton('GRANULAR SPEED')
        self.granular_button.setStyleSheet(UIStyles.BUTTON_WIZARD_BLUE)
        self.granular_button.setFixedSize(140, 35)
        self.granular_button.setCursor(Qt.PointingHandCursor)
        self.granular_button.setToolTip("Open the detailed speed curve editor")
        self.granular_button.clicked.connect(self._handle_granular_click)
        self.granular_button.setEnabled(False)
        granular_w = QWidget()
        g_l = QHBoxLayout(granular_w)
        g_l.setContentsMargins(0,0,0,0)
        g_l.addWidget(self.granular_button)
        g_l.addWidget(self.granular_checkbox)
        self.mobile_checkbox = QCheckBox('Portrait (9:16)')
        self.mobile_checkbox.setStyleSheet(UIStyles.CHECKBOX)
        self.mobile_checkbox.setCursor(Qt.PointingHandCursor)
        self.mobile_checkbox.setToolTip("Switch to vertical 9:16 portrait crop for social media")
        try:
            val = bool(self.config_manager.config.get('mobile_checked', False))
        except:
            val = False
        self.mobile_checkbox.setChecked(val)
        self.mobile_checkbox.setEnabled(False)
        self.mobile_checkbox.toggled.connect(self._on_mobile_toggled)
        self.teammates_checkbox = QCheckBox('Show Teammates Healthbar')
        self.teammates_checkbox.setStyleSheet('font-size: 11px;')
        self.teammates_checkbox.setCursor(Qt.PointingHandCursor)
        self.teammates_checkbox.setToolTip("Enable overlay for teammates' health in portrait mode")
        self.teammates_checkbox.setEnabled(False)
        self.teammates_checkbox.setVisible(self.mobile_checkbox.isChecked())
        self.portrait_text_input = QLineEdit()
        self.portrait_text_input.setPlaceholderText('Overlay Text')
        self.portrait_text_input.setToolTip("Custom text for the portrait mode overlay")
        self.portrait_text_input.setEnabled(False)
        self.portrait_text_input.setVisible(self.mobile_checkbox.isChecked())
        self.portrait_text_input.setStyleSheet('background-color: #4a667a; color: white; border: 1px solid #266b89; border-radius: 4px; padding: 4px;')
        grid = QGridLayout()
        grid.setContentsMargins(0, 0, 0, 0)
        grid.setHorizontalSpacing(16)
        l_col = QVBoxLayout()
        l_row = QHBoxLayout()
        l_row.addWidget(self.mobile_checkbox)
        l_row.addWidget(self.teammates_checkbox)
        l_col.addLayout(l_row)
        l_col.addWidget(self.portrait_text_input)
        l_w = QWidget()
        l_w.setLayout(l_col)
        r_col = QHBoxLayout()
        r_col.addWidget(granular_w)
        r_col.addWidget(speed_w)
        r_w = QWidget()
        r_w.setLayout(r_col)
        grid.addWidget(l_w, 0, 0, Qt.AlignLeft | Qt.AlignVCenter)
        grid.addWidget(self.quality_container, 0, 1, Qt.AlignLeft | Qt.AlignVCenter)
        grid.addLayout(btn_l, 0, 2, Qt.AlignCenter)
        grid.addWidget(r_w, 0, 3, Qt.AlignRight | Qt.AlignVCenter)
        self.center_btn_container = grid

    def _build_right_panel(self):
        self.right_panel = QWidget()
        self.right_panel.setFixedWidth(125)
        self.right_panel.installEventFilter(self)
        r_l = QVBoxLayout(self.right_panel)
        r_l.setContentsMargins(0, 0, 0, 0)
        r_l.setAlignment(Qt.AlignTop)
        self.drop_area = DropAreaFrame()
        self.drop_area.file_dropped.connect(self.handle_file_selection)
        self.drop_area.setFixedSize(120, 200)
        self.drop_area.setCursor(Qt.PointingHandCursor)
        self.drop_area.setToolTip("Drag and drop a video file here to start")
        d_l = QVBoxLayout(self.drop_area)
        self.drop_label = QLabel('Drag & Drop\r\na Video File Here:')
        self.drop_label.setAlignment(Qt.AlignCenter)
        self.drop_label.setStyleSheet('font-size: 10px; font-weight: bold;')
        d_l.addWidget(self.drop_label)
        r_l.addWidget(self.drop_area)
        self.upload_button = QPushButton('📂  UPLOAD VIDEO  📂')
        self.upload_button.setCursor(Qt.PointingHandCursor)
        self.upload_button.setStyleSheet(UIStyles.BUTTON_WIZARD_BLUE + ' QPushButton { font-size: 10px; padding: 0px; }')
        self.upload_button.setFixedSize(120, 35)
        self.upload_button.setToolTip("Browse files to upload a video")
        self.upload_button.clicked.connect(self.select_file)
        r_l.addWidget(self.upload_button)
        r_l.addSpacing(15)
        self.music_button = QPushButton('♪    ADD MUSIC    ♪')
        self.music_button.setCursor(Qt.PointingHandCursor)
        self.music_button.setStyleSheet(UIStyles.BUTTON_WIZARD_BLUE + ' QPushButton { font-size: 10px; padding: 0px; }')
        self.music_button.setFixedSize(120, 35)
        self.music_button.setToolTip("Open the background music synchronization wizard")
        self.music_button.clicked.connect(self.open_music_wizard)
        self.music_button.setEnabled(False)
        r_l.addWidget(self.music_button)
        r_l.addSpacing(15)
        r_l.addStretch(1)
        self.no_fade_checkbox = QCheckBox('Disable Fade-In/Out')
        self.no_fade_checkbox.setCursor(Qt.PointingHandCursor)
        self.no_fade_checkbox.setToolTip("Toggle automatic fade transitions at start/end")
        self.no_fade_checkbox.setEnabled(False)
        r_l.addWidget(self.no_fade_checkbox, 0, Qt.AlignRight)
        bb = QVBoxLayout()
        bb.setContentsMargins(0, 0, 25, 0)
        self.merge_btn = QPushButton('VIDEO MERGER')
        self.crop_tool_btn = QPushButton('CROP SETTINGS')
        self.adv_editor_btn = QPushButton('ADVANCED\n VIDEO EDITOR')
        self.merge_btn.setToolTip("Open the multi-video merger tool")
        self.crop_tool_btn.setToolTip("Open crop and portrait configuration (F12)")
        self.adv_editor_btn.setToolTip("Launch the professional video editor")
        for b in (self.merge_btn, self.crop_tool_btn, self.adv_editor_btn):
            b.setStyleSheet(UIStyles.BUTTON_TOOL)
            b.setFixedSize(130, 38)
            b.setCursor(Qt.PointingHandCursor)
            bb.addWidget(b, 0, Qt.AlignRight)
        self.merge_btn.clicked.connect(self.launch_video_merger)
        self.crop_tool_btn.clicked.connect(self.launch_crop_tool)
        self.adv_editor_btn.clicked.connect(self.launch_advanced_editor)
        r_l.addLayout(bb)

    def _effective_project_duration_sec(self, trim_start_ms, trim_end_ms):
        base_speed = 1.0
        try:
            base_speed = max(0.001, float(self.speed_spinbox.value()))
        except Exception:
            base_speed = 1.0
        trim_start_ms = int(trim_start_ms)
        trim_end_ms = int(trim_end_ms)
        if trim_end_ms <= trim_start_ms:
            return 0.0
        granular_enabled = bool(getattr(self, "granular_checkbox", None) and self.granular_checkbox.isChecked())
        segments = list(getattr(self, "speed_segments", []) or []) if granular_enabled else []
        if not segments:
            return ((trim_end_ms - trim_start_ms) / 1000.0) / base_speed
        total_ms = 0.0
        cursor = trim_start_ms
        for seg in sorted(segments, key=lambda item: int(item.get("start_ms", item.get("start", 0)))):
            try:
                raw_start = int(seg.get("start_ms", seg.get("start", 0)))
                raw_end = int(seg.get("end_ms", seg.get("end", 0)))
                seg_speed = float(seg.get("speed", base_speed))
            except Exception:
                continue
            seg_start = max(trim_start_ms, raw_start, cursor)
            seg_end = min(trim_end_ms, raw_end)
            if seg_end <= seg_start:
                continue
            if seg_start > cursor:
                total_ms += (seg_start - cursor) / base_speed
            if abs(seg_speed) < 0.001:
                total_ms += seg_end - seg_start
            else:
                total_ms += (seg_end - seg_start) / max(0.001, seg_speed)
            cursor = max(cursor, seg_end)
        if cursor < trim_end_ms:
            total_ms += (trim_end_ms - cursor) / base_speed
        return max(0.001, total_ms / 1000.0)

    def _update_quality_label(self):
        if not hasattr(self, 'quality_value_label'):
            return
        if not getattr(self, 'input_file_path', None) or not os.path.exists(self.input_file_path):
            self.quality_value_label.setText('')
            return
        idx = int(self.quality_slider.value())
        dur_ms = getattr(self, 'trim_end_ms', 0) - getattr(self, 'trim_start_ms', 0)
        if dur_ms <= 0:
            self.quality_value_label.setText('')
            return
        try:
            if idx >= 20:
                target_mb = os.path.getsize(self.input_file_path) / (1024 * 1024)
            else:
                target_mb = 5 + idx * 5
        except:
            target_mb = 5 + idx * 5
        dur_sec = self._effective_project_duration_sec(getattr(self, 'trim_start_ms', 0), getattr(self, 'trim_end_ms', 0))
        if dur_sec <= 0:
            self.quality_value_label.setText('')
            return
        kbps = target_mb * 8 * 1024 / dur_sec
        try:
            res = str(getattr(self, 'original_resolution', '') or '1920x1080').lower()
            w, h = [int(part) for part in res.split('x', 1)]
        except Exception:
            w, h = 1920, 1080
        bpp = kbps * 1024 / (max(1, w) * max(1, h) * 60)
        if not self.mobile_checkbox.isChecked():
            bpp /= 1.5
        spectrum = [(0.02, 'Unwatchable', '#e74c3c'), (0.04, 'Pixelated', '#e74c3c'), (0.06, 'Blurry', '#e74c3c'), (0.1, 'Clear', 'white'), (0.15, 'Sharp', '#2ecc71'), (0.25, 'Crisp-Clear', '#2ecc71'), (99.0, 'Lifelike', '#2ecc71')]
        desc, color = ('Standard', 'white')
        for i, (thresh, d, c) in enumerate(spectrum):
            if bpp < thresh:
                desc, color = (d, c)
                prev = spectrum[i - 1][0] if i > 0 else 0.0
                mid = (thresh + prev) / 2.0
                if thresh < 90.0:
                    desc += ' -' if bpp < mid else ' +'
                break
        self.quality_value_label.setText(desc)
        self.quality_value_label.setStyleSheet(f'color: {color}; font-weight: bold;')

    def _on_mobile_toggled(self, checked: bool):
        self.teammates_checkbox.setVisible(checked)
        self.teammates_checkbox.setEnabled(checked)
        self.portrait_text_input.setVisible(checked)
        self.portrait_text_input.setEnabled(checked)
        if hasattr(self, 'portrait_mask_overlay') and self.portrait_mask_overlay:
            self.portrait_mask_overlay.setVisible(checked and bool(self.input_file_path))
            self._update_portrait_mask_overlay_state()
        self._update_quality_label()

    def _update_portrait_mask_overlay_state(self):
        if not hasattr(self, 'portrait_mask_overlay') or not self.portrait_mask_overlay or getattr(self, 'is_processing', False):
            return
        res = getattr(self, 'original_resolution', '1920x1080')
        if self.mobile_checkbox.isChecked() and self.input_file_path:
            try:
                tl = self.video_surface.mapToGlobal(self.video_surface.rect().topLeft())
                self.portrait_mask_overlay.setGeometry(tl.x(), tl.y(), self.video_surface.width(), self.video_surface.height())
                self.portrait_mask_overlay.set_video_info(res, self.video_surface.size())
                self.portrait_mask_overlay.show()
                self.portrait_mask_overlay.raise_()
            except:
                pass
        else:
            self.portrait_mask_overlay.hide()

    def _init_status_bar(self):
        self.progress_bar = QProgressBar()
        self.progress_bar.setStyleSheet(UIStyles.PROGRESS_BAR)
        self.progress_bar.hide()
        self.status_container = QWidget()
        self.status_container.setFixedHeight(23)
        status_l = QHBoxLayout(self.status_container)
        status_l.setContentsMargins(10, 0, 10, 0)
        self.hardware_status_label = QLabel('')
        self.hardware_status_label.setStyleSheet(UIStyles.LABEL_STATUS)
        self.resolution_label = QLabel('')
        self.resolution_label.setStyleSheet(UIStyles.LABEL_STATUS)
        status_l.addWidget(self.hardware_status_label)
        status_l.addStretch(1)
        status_l.addWidget(self.resolution_label)
        status_l.addStretch(1)
        if hasattr(self, 'status_bar') and self.status_bar:
            self.status_bar.addPermanentWidget(self.status_container, 1)
        self.progress_update_signal.connect(self.on_progress)
        self.status_update_signal.connect(self.on_phase_update)
        self.process_finished_signal.connect(self.on_process_finished)
        try:
            from ui.main_window import _QtLiveLogHandler
            import logging
            h = _QtLiveLogHandler(self)
            h.setFormatter(logging.Formatter('%(asctime)s | %(message)s', '%H:%M:%S'))
            if hasattr(self, 'logger'):
                self.logger.addHandler(h)
        except:
            pass
