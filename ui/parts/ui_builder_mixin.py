import os
import sys
import subprocess
from PyQt5.QtCore import Qt, QTimer, QSize, QEvent
from PyQt5.QtGui import QPixmap, QPainter, QFont, QIcon, QFontMetrics, QColor, QPen
from PyQt5.QtWidgets import (QGridLayout, QMessageBox, QSizePolicy, QHBoxLayout,
                             QVBoxLayout, QFrame, QSlider, QLabel, QStyle,
                             QPushButton, QSpinBox, QDoubleSpinBox, QCheckBox,
                             QProgressBar, QComboBox, QWidget, QStyleOptionSpinBox, QStackedLayout)

from ui.widgets.clickable_button import ClickableButton
from ui.widgets.trimmed_slider import TrimmedSlider
from ui.widgets.drop_area import DropAreaFrame
from ui.styles import UIStyles
try:
    from ui.widgets.portrait_mask_overlay import PortraitMaskOverlay
except ImportError:
    PortraitMaskOverlay = None

class ClickableSpinBox(QDoubleSpinBox):
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
        """
        Capture the current absolute player time (seconds) as the desired
        thumbnail frame using FFmpeg for precision.
        """
        try:
            if not self.input_file_path or not os.path.exists(self.input_file_path):
                QMessageBox.information(self, "No Video", "Please load a video first.")
                return
            pos_ms = 0
            try:
                pos_ms = int(self.positionSlider.value())
            except Exception:
                pass
            if (not pos_ms) and getattr(self, 'vlc_player', None):
                pos_ms = int(self.vlc_player.get_time() or 0)
            pos_s = float(pos_ms) / 1000.0
            if self.original_duration_ms > 0 and pos_ms > self.original_duration_ms:
                pos_s = self.original_duration_ms / 1000.0
            self.selected_intro_abs_time = pos_s
            mm = int(self.selected_intro_abs_time // 60)
            ss = self.selected_intro_abs_time % 60.0
            label = f"📸 THUMBNAIL\n SET: {mm:02d}:{ss:05.2f}"
            self.thumb_pick_btn.setText(label)

            import tempfile
            temp_thumb = os.path.join(tempfile.gettempdir(), f"thumb_preview_{os.getpid()}.jpg")
            ffmpeg_path = os.path.join(self.bin_dir, 'ffmpeg.exe')
            cmd = [
                ffmpeg_path, "-y", "-ss", f"{pos_s:.3f}", "-i", self.input_file_path,
                "-vframes", "1", "-q:v", "2", temp_thumb
            ]

            def _run_extract():
                subprocess.run(cmd, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, creationflags=(subprocess.CREATE_NO_WINDOW if sys.platform == 'win32' else 0))
                
            from threading import Thread
            t = Thread(target=_run_extract)
            t.start()
            if hasattr(self, "logger"):
                self.logger.info("THUMB: Selected absolute time %.3fs", self.selected_intro_abs_time)
            self.status_update_signal.emit(f"✅ Thumbnail frame selected: {mm:02d}:{ss:05.2f}")
        except Exception as e:
            try:
                if hasattr(self, "logger"):
                    self.logger.exception("Thumbnail pick failed: %s", e)
            finally:
                QMessageBox.warning(self, "Error", f"Failed to pick thumbnail: {e}")

    def _on_boss_hp_toggled(self, checked):
        if hasattr(self, "logger"):
            self.logger.info(f"OPTION: Boss HP -> {checked}")

    def _update_process_button_text(self) -> None:
        """Updates the process button text AND spinner icon."""
        try:
            self._pulse_phase = (getattr(self, "_pulse_phase", 0) + 1) % 8
            if getattr(self, "is_processing", False):
                dots = "." * (1 + (self._pulse_phase // 2))
                text = f"PROCESSING{dots}"
                spinner = "⣾⣽⣻⢿⡿⣟⣯⣷"
                glyph = spinner[self._pulse_phase % len(spinner)]
                px = 24
                pm = QPixmap(px, px)
                pm.fill(Qt.transparent)
                p = QPainter(pm)
                f = QFont(self.font())
                f.setPixelSize(14)
                f.setBold(True)
                p.setFont(f)
                p.setPen(Qt.black)
                p.drawText(pm.rect(), Qt.AlignCenter, glyph)
                p.end()
                self.process_button.setText(text)
                self.process_button.setIcon(QIcon(pm))
                self.process_button.setIconSize(QSize(px, px))
            else:
                self.process_button.setText("PROCESS VIDEO")
                self.process_button.setIcon(QIcon())
        except Exception:
            pass

    def _handle_granular_click(self):
        """Delegates granular checkbox click to main window logic."""
        if hasattr(self, 'open_granular_speed_dialog'):
            self.open_granular_speed_dialog()

    def _safe_add_to_grid(self, layout, w, r=None, c=None, rs=1, cs=1, align=Qt.Alignment()):
        """Add widget whether layout is QGridLayout or QBoxLayout/etc."""
        try:
            from PyQt5.QtWidgets import QGridLayout
            if isinstance(layout, QGridLayout) and r is not None and c is not None:
                layout.addWidget(w, r, c, rs, cs, align)
            else:
                if align:
                    layout.addWidget(w, 0, align)
                else:
                    layout.addWidget(w)
        except Exception:
            try:
                layout.addWidget(w)
            except Exception:
                pass

    def _ensure_default_trim(self):
        """
        If user didn't set trims, default to full video length based on the current
        spinbox maxima (updated when media loads).
        """
        try:
            if self.end_minute_input.maximum() == 0 and self.positionSlider.maximum() == 0:
                QMessageBox.warning(self, "No video", "Please load a video file first.")
                if hasattr(self, "logger"): self.logger.error("PROCESS: blocked - no video loaded")
                return False
            start_is_zero = (self.start_minute_input.value() == 0 and
                            self.start_second_input.value() == 0)
            end_is_zero   = (self.end_minute_input.value()   == 0 and
                            self.end_second_input.value()   == 0)
            if start_is_zero and end_is_zero:
                end_min = self.end_minute_input.maximum()
                end_sec = self.end_second_input.maximum()
                self.end_minute_input.setValue(end_min)
                self.end_second_input.setValue(end_sec)
                if hasattr(self, "logger"):
                    self.logger.info("TRIM: defaulted to full length end=%02d:%02d", end_min, end_sec)
            return True
        except Exception as e:
            try:
                if hasattr(self, "logger"): self.logger.exception("TRIM default failed: %s", e)
            except Exception:
                pass
            return False

    def _on_process_clicked(self):
        """Click-safe entrypoint: log, ensure trims, then call existing processing."""
        try:
            if hasattr(self, "logger"): self.logger.info("CLICK: Process Video")
            try:
                if getattr(self, "vlc_player", None):
                    self.vlc_player.stop()
            except Exception:
                pass
            if (self.end_minute_input.maximum() == 0 and
                self.positionSlider.maximum() == 0):
                QMessageBox.warning(self, "No video", "Please load a video file first.")
                if hasattr(self, "logger"): self.logger.error("PROCESS: blocked - no video loaded")
                return
            if not self._ensure_default_trim():
                return
            self.start_processing()
        except Exception as e:
            try:
                if hasattr(self, "logger"): self.logger.exception("PROCESS click failed: %s", e)
            except Exception:
                pass
            QMessageBox.critical(self, "Error", "Could not start processing. See log for details.")

    def _maybe_enable_process(self):
        """
        Turn on the Process button once duration exists,
        and if user hasn’t set trims yet, default to full video.
        checks if hardware scan is complete [Fix #2].
        """
        try:
            if not getattr(self, "scan_complete", False):
                return
            has_duration = (self.positionSlider.maximum() > 0) or (self.end_minute_input.maximum() > 0)
            if has_duration:
                if not self.process_button.isEnabled():
                    if hasattr(self, "logger"): self.logger.info("READY: Media loaded; enabling Process button")
                    self.process_button.setEnabled(True)
                    self._ensure_default_trim()
        except Exception:
            pass

    def launch_video_merger(self):
        """Launches the Video Merger with sanity checks."""
        merger_path = os.path.join(self.base_dir, 'utilities', 'video_merger.py')
        if not os.path.exists(merger_path):
            self.logger.error(f"Sanity Check Failed: Merger script missing at {merger_path}")
            QMessageBox.critical(self, "Missing Component", f"Could not find the Video Merger script:\n{merger_path}")
            return
        try:
            from system.state_transfer import StateTransfer
            StateTransfer.save_state({
                'input_file': getattr(self, 'input_file_path', None),
                'trim_start': getattr(self, 'trim_start_ms', 0),
                'trim_end': getattr(self, 'trim_end_ms', 0),
                'mobile_checked': self.mobile_checkbox.isChecked() if hasattr(self, 'mobile_checkbox') else False
            })
            self.logger.info(f"ACTION: Launching Video Merger: {merger_path}")
            flags = 0
            if sys.platform == "win32":
                flags = 0x00000010 | subprocess.CREATE_NEW_PROCESS_GROUP
            subprocess.Popen(
                [sys.executable, merger_path],
                cwd=self.base_dir,
                creationflags=flags,
                close_fds=True,
                start_new_session=True if sys.platform != "win32" else False
            )
            self.logger.info("Merger launched successfully via Popen.")
            self._switching_app = True

            from PyQt5.QtCore import QCoreApplication
            QTimer.singleShot(500, QCoreApplication.quit)
        except Exception as e:
            self.logger.critical(f"Error launching merger: {e}")
            QMessageBox.critical(self, "Launch Error", f"Failed to start the Video Merger:\n{e}")

    def launch_crop_tool(self):
        """Launches the Crop Tool."""
        tool_path = os.path.join(self.base_dir, 'developer_tools', 'crop_tools.py')
        if not os.path.exists(tool_path):
            QMessageBox.critical(self, "Error", "Crop tool not found.")
            return
        try:
            from system.state_transfer import StateTransfer
            StateTransfer.save_state({
                'input_file': getattr(self, 'input_file_path', None),
                'trim_start': getattr(self, 'trim_start_ms', 0),
                'trim_end': getattr(self, 'trim_end_ms', 0)
            })
            self.logger.info(f"ACTION: Launching Crop Tool: {tool_path}")
            flags = 0
            if sys.platform == "win32":
                flags = 0x00000010 | subprocess.CREATE_NEW_PROCESS_GROUP
            subprocess.Popen(
                [sys.executable, tool_path],
                cwd=self.base_dir,
                creationflags=flags,
                close_fds=True,
                start_new_session=True if sys.platform != "win32" else False
            )
            self._switching_app = True

            from PyQt5.QtCore import QCoreApplication
            QTimer.singleShot(500, QCoreApplication.quit)
        except Exception as e:
            QMessageBox.critical(self, "Error", f"Failed to launch Crop Tool: {e}")

    def launch_advanced_editor(self):
        """Placeholder for Advanced Editor."""
        QMessageBox.information(self, "Coming Soon", "The Advanced Video Editor is currently under development.")

    def eventFilter(self, obj, event):
        if event.type() == QEvent.Resize:
            if obj is getattr(self, "video_surface", None):
                if hasattr(self, 'portrait_mask_overlay'):
                    self._update_portrait_mask_overlay_state()
        return super().eventFilter(obj, event)

    def init_ui(self):
        """
        Main entry point for UI construction. 
        Refactored to be modular and cleaner.
        """
        main_layout = QHBoxLayout()
        main_layout.setContentsMargins(0, 0, 0, 0)
        left_layout = QVBoxLayout()
        left_layout.setContentsMargins(0, 0, 0, 0)
        left_layout.setSpacing(0)
        self._top_row = QHBoxLayout()
        self._top_row.setSpacing(12)
        self._build_player_column()
        self._build_right_panel()
        self._top_row.addWidget(self.player_col_container, stretch=6)
        self._top_row.addWidget(self.right_panel, stretch=1)
        self._top_row.addSpacing(10)
        left_layout.addLayout(self._top_row)
        main_layout.addLayout(left_layout, stretch=1)
        self.central_widget.setLayout(main_layout)
        self._ensure_overlay_widgets()
        self._hide_processing_overlay()
        old_resize = getattr(self, "resizeEvent", None)

        def _resized(e):
            if callable(old_resize): old_resize(e)
            try: self._resize_overlay()
            except: pass
        self.resizeEvent = _resized
        self._update_ui_positions(self.add_music_checkbox.isChecked())
        QTimer.singleShot(0, self._adjust_trim_margins)
        QTimer.singleShot(0, self.apply_master_volume)
        QTimer.singleShot(0, lambda: self.setFocus(Qt.ActiveWindowFocusReason))

    def _build_player_column(self):
        """Constructs the video player, volume slider, and trim controls."""
        player_col = QVBoxLayout()
        player_col.setContentsMargins(0, 0, 0, 0)
        player_col.setSpacing(6)
        video_and_volume_layout = QHBoxLayout()
        video_and_volume_layout.setContentsMargins(0, 0, 0, 0)
        video_and_volume_layout.setSpacing(2)
        self._init_volume_slider()
        video_and_volume_layout.addWidget(self.volume_container, 0, Qt.AlignHCenter)
        self._init_video_surface()
        video_and_volume_layout.addWidget(self.video_frame, stretch=1)
        player_col.addLayout(video_and_volume_layout)
        player_col.setStretch(0, 1)
        self.positionSlider = TrimmedSlider(self)
        self.positionSlider.setRange(0, 0)
        self.positionSlider.setFixedHeight(50)
        self.positionSlider.setSizePolicy(QSizePolicy.Expanding, QSizePolicy.Fixed)
        self.positionSlider.setObjectName("timelineSlider")
        self.positionSlider.setStyleSheet(UIStyles.SLIDER_TIMELINE_METALLIC)
        self.positionSlider.setEnabled(False)
        self.tooltip_manager.add_tooltip(self.positionSlider, "Seek: ← / →\nFast Seek: Shift + ← / →\nFine Seek: Ctrl + ← / →")
        self.positionSlider.sliderMoved.connect(self.set_vlc_position)
        self.positionSlider.rangeChanged.connect(lambda *_: self._maybe_enable_process())
        self.positionSlider.trim_times_changed.connect(self._on_slider_trim_changed)
        player_col.addWidget(self.positionSlider)
        player_col.setStretch(1, 0)
        self._init_trim_controls()
        trim_container = QHBoxLayout()
        trim_container.setContentsMargins(10, 0, 10, 0)
        trim_container.addSpacing(15)
        trim_container.addWidget(self.thumb_pick_btn, 0, Qt.AlignLeft)
        trim_container.addSpacing(30)
        trim_container.addWidget(self.boss_hp_checkbox, 0, Qt.AlignLeft)
        trim_container.addStretch(1)
        trim_container.addLayout(self.trim_layout)
        trim_container.addStretch(1)
        player_col.addLayout(trim_container)
        self._init_process_controls()
        player_col.addLayout(self.center_btn_container)
        self.mobile_checkbox.toggled.connect(self._on_mobile_toggled)
        player_col.addSpacing(10)
        self._init_status_bar()
        progress_layout = QHBoxLayout()
        progress_layout.setContentsMargins(0, 0, 0, 0)
        progress_layout.setSpacing(10)
        progress_layout.addWidget(self.progress_bar, stretch=1)
        player_col.addLayout(progress_layout)
        self.player_col_container = QWidget()
        self.player_col_container.setLayout(player_col)
        self.player_col_container.installEventFilter(self)

    def _set_video_controls_enabled(self, enabled: bool):
        """[FIX] Enforces 'Greyed Out' methodology for all video-dependent UI elements."""
        for widget in [
            self.playPauseButton, self.start_trim_button, self.end_trim_button,
            self.thumb_pick_btn, self.boss_hp_checkbox, self.quality_slider,
            self.speed_spinbox, self.granular_checkbox, self.mobile_checkbox,
            self.teammates_checkbox, self.no_fade_checkbox, self.portrait_text_input,
            self.music_volume_slider, self.add_music_checkbox, self.music_combo,
            self.music_offset_input, self.positionSlider,
            self.start_minute_input, self.start_second_input, self.start_ms_input,
            self.end_minute_input, self.end_second_input, self.end_ms_input
        ]:
            if hasattr(self, widget.objectName()) or widget:
                widget.setEnabled(enabled)
        if enabled:
            if hasattr(self, 'add_music_checkbox') and self.add_music_checkbox.isChecked():
                self.music_combo.setEnabled(True)
                self.music_volume_slider.setEnabled(True)
                if self.music_combo.currentIndex() >= 0:
                    self.music_offset_input.setEnabled(True)
            if hasattr(self, 'mobile_checkbox') and self.mobile_checkbox.isChecked():
                self.teammates_checkbox.setEnabled(True)
                self.portrait_text_input.setEnabled(True)
            else:
                self.teammates_checkbox.setEnabled(False)
                self.portrait_text_input.setEnabled(False)

    def _init_volume_slider(self):
        self.volume_stack_layout = QVBoxLayout()
        self.volume_stack_layout.setContentsMargins(0, 0, 0, 0)
        self.volume_stack_layout.setSpacing(0)
        self.volume_stack_layout.setAlignment(Qt.AlignTop)
        self.volume_slider = QSlider(Qt.Vertical)
        self.volume_slider.setObjectName("volumeSlider")
        self.volume_slider.setFixedWidth(40)
        self.volume_slider.setRange(0, 100)
        self.volume_slider.setSingleStep(1)
        self.volume_slider.setPageStep(15)
        self.volume_slider.setTickInterval(10)
        self.volume_slider.setTickPosition(QSlider.TicksBothSides)
        self.volume_slider.setTracking(True)
        self.volume_slider.setInvertedAppearance(True)
        self.volume_slider.setCursor(Qt.PointingHandCursor)
        self.volume_slider.setEnabled(True)
        self.volume_slider.setStyleSheet(UIStyles.SLIDER_VOLUME_VERTICAL_METALLIC)
        self.tooltip_manager.add_tooltip(self.volume_slider, "Adjust Volume: ↑ / ↓\nLarge Step: Shift + ↑ / ↓")
        try:
            eff = int(self.config_manager.config.get('last_volume', 100))
        except:
            eff = 100
        raw = self.volume_slider.maximum() + self.volume_slider.minimum() - eff
        self.volume_slider.setValue(max(self.volume_slider.minimum(), min(self.volume_slider.maximum(), raw)))
        self.volume_slider.valueChanged.connect(self._on_master_volume_changed)
        self.volume_slider.sliderMoved.connect(lambda _: self._update_volume_badge())
        self.volume_slider.installEventFilter(self)
        self.volume_badge = QLabel("0%", self.volume_slider)
        self.volume_badge.setObjectName("volumeBadge")
        self.volume_badge.setStyleSheet("color: white; background: rgba(0,0,0,160); padding: 2px 6px; border-radius: 6px; font-weight: bold;")
        self.volume_badge.adjustSize()
        self.volume_badge.hide()
        self.volume_stack_layout.addWidget(self.volume_badge, 0, Qt.AlignHCenter)
        self.volume_stack_layout.addWidget(self.volume_slider, 1, Qt.AlignHCenter)
        self.volume_container = QWidget()
        self.volume_container.setLayout(self.volume_stack_layout)
        self.volume_container.setFixedWidth(40)

    def _init_video_surface(self):
        self.video_frame = QFrame()
        self.video_frame.setMinimumHeight(360)
        self.video_frame.setFocusPolicy(Qt.NoFocus)
        self.video_frame.installEventFilter(self)
        video_layout = QVBoxLayout(self.video_frame)
        video_layout.setContentsMargins(0, 0, 0, 0)
        self.video_viewport_container = QWidget()
        self.video_viewport_container.setContentsMargins(0, 0, 0, 0)
        self.video_viewport_container.setSizePolicy(QSizePolicy.Expanding, QSizePolicy.Expanding)
        _center_row = QHBoxLayout(self.video_viewport_container)
        _center_row.setContentsMargins(0, 0, 0, 0)
        _center_row.setSpacing(0)
        self.video_surface = QWidget()
        self.video_surface.setStyleSheet("background-color: black;")
        self.video_surface.setAttribute(Qt.WA_NativeWindow)
        self.video_surface.setAttribute(Qt.WA_TransparentForMouseEvents, True)
        _center_row.addWidget(self.video_surface)
        video_layout.addWidget(self.video_viewport_container, stretch=1)
        if PortraitMaskOverlay:
            self.portrait_mask_overlay = PortraitMaskOverlay(self)
            self.portrait_mask_overlay.hide()
        else:
            self.portrait_mask_overlay = None
        self.video_surface.installEventFilter(self)

    def _init_trim_controls(self):
        self.playPauseButton = QPushButton("PLAY")
        self.playPauseButton.setObjectName("playPauseButton")
        self.playPauseButton.setIcon(self.style().standardIcon(QStyle.SP_MediaPlay))
        self.playPauseButton.clicked.connect(self.toggle_play_pause)
        self.playPauseButton.setFocusPolicy(Qt.NoFocus)
        self.playPauseButton.setCursor(Qt.PointingHandCursor)
        self.playPauseButton.setEnabled(False)
        self.tooltip_manager.add_tooltip(self.playPauseButton, "Spacebar")
        self.playPauseButton.setStyleSheet(UIStyles.BUTTON_PLAY)
        self.playPauseButton.setFixedWidth(140)
        self.playPauseButton.setFixedHeight(35)
        self.thumb_pick_btn = QPushButton("📸 SET THUMBNAIL 📸")
        self.thumb_pick_btn.setObjectName("thumbPickBtn")
        self.thumb_pick_btn.setStyleSheet(UIStyles.BUTTON_STANDARD)
        self.thumb_pick_btn.setEnabled(False)
        self.tooltip_manager.add_tooltip(self.thumb_pick_btn, "Select Custom Thumbnail Picture For Sharing")
        self.thumb_pick_btn.setFocusPolicy(Qt.NoFocus)
        self.thumb_pick_btn.setCursor(Qt.PointingHandCursor)
        self.thumb_pick_btn.clicked.connect(self._pick_thumbnail_from_current_frame)
        self.thumb_pick_btn.setSizePolicy(QSizePolicy.Maximum, QSizePolicy.Fixed)
        self.start_minute_input = QSpinBox(); self.start_minute_input.setRange(0, 0)
        self.start_second_input = QSpinBox(); self.start_second_input.setRange(0, 59)
        self.start_ms_input = QSpinBox(); self.start_ms_input.setRange(0, 999); self.start_ms_input.setSingleStep(10)
        self.end_minute_input   = QSpinBox(); self.end_minute_input.setRange(0, 0)
        self.end_second_input   = QSpinBox(); self.end_second_input.setRange(0, 59)
        self.end_ms_input = QSpinBox(); self.end_ms_input.setRange(0, 999); self.end_ms_input.setSingleStep(10)
        for spin in (self.start_minute_input, self.start_second_input, self.start_ms_input, 
                     self.end_minute_input, self.end_second_input, self.end_ms_input):
            spin.setFixedHeight(35)
            spin.setMaximumWidth(48)
            spin.setCursor(Qt.PointingHandCursor)
            spin.setEnabled(False)
            spin.valueChanged.connect(self._on_trim_spin_changed)
        self.start_trim_button = QPushButton("SET START")
        self.start_trim_button.setObjectName("startTrimButton")
        self.start_trim_button.setStyleSheet(UIStyles.BUTTON_STANDARD)
        self.start_trim_button.clicked.connect(self.set_start_time)
        self.start_trim_button.setFocusPolicy(Qt.NoFocus)
        self.start_trim_button.setCursor(Qt.PointingHandCursor)
        self.start_trim_button.setEnabled(False)
        self.start_trim_button.setFixedWidth(105)
        self.tooltip_manager.add_tooltip(self.start_trim_button, "[")
        self.end_trim_button = QPushButton("SET END")
        self.end_trim_button.setObjectName("endTrimButton")
        self.end_trim_button.setStyleSheet(UIStyles.BUTTON_STANDARD)
        self.end_trim_button.clicked.connect(self.set_end_time)
        self.end_trim_button.setFocusPolicy(Qt.NoFocus)
        self.end_trim_button.setCursor(Qt.PointingHandCursor)
        self.end_trim_button.setEnabled(False)
        self.end_trim_button.setFixedWidth(105)
        self.tooltip_manager.add_tooltip(self.end_trim_button, "]")

        def _make_layout(lbl_txt, spin):
            l = QHBoxLayout(); l.setContentsMargins(0,0,0,0); l.setSpacing(0)
            lbl = QLabel(lbl_txt); lbl.setStyleSheet("font-size: 10px;")
            l.addWidget(lbl); l.addWidget(spin)
            return l
        start_group = QHBoxLayout(); start_group.setContentsMargins(0,0,0,0); start_group.setSpacing(0)
        start_group.addLayout(_make_layout("Start Min:", self.start_minute_input))
        start_group.addLayout(_make_layout("Sec:", self.start_second_input))
        self.start_ms_input.hide()
        end_group = QHBoxLayout(); end_group.setContentsMargins(0,0,0,0); end_group.setSpacing(0)
        end_group.addLayout(_make_layout("End Min:", self.end_minute_input))
        end_group.addLayout(_make_layout("Sec:", self.end_second_input))
        self.end_ms_input.hide()
        self.boss_hp_checkbox = QCheckBox("Boss HP")
        self.boss_hp_checkbox.setObjectName("bossHpCheckbox")
        self.tooltip_manager.add_tooltip(self.boss_hp_checkbox, "<p style='font-family: Arial; font-size: 13pt; font-weight: normal;'>For videos which you are the boss charachter in them</p>")
        self.boss_hp_checkbox.setStyleSheet("font-size: 10px; font-weight: normal;")
        self.boss_hp_checkbox.setChecked(False)
        self.boss_hp_checkbox.setEnabled(False)
        self.boss_hp_checkbox.toggled.connect(self._on_boss_hp_toggled)
        self.boss_hp_checkbox.setCursor(Qt.PointingHandCursor)
        self.trim_layout = QHBoxLayout()
        self.trim_layout.setContentsMargins(0, 0, 0, 0)
        self.trim_layout.setSpacing(14)
        self.trim_layout.addLayout(start_group)
        self.trim_layout.addWidget(self.start_trim_button)
        self.trim_layout.addSpacing(14)
        self.trim_layout.addWidget(self.playPauseButton)
        self.trim_layout.addSpacing(14)
        self.trim_layout.addWidget(self.end_trim_button)
        self.trim_layout.addLayout(end_group)

    def _init_process_controls(self):
        self.quality_label = QLabel("Output Quality")
        self.quality_label.setAlignment(Qt.AlignHCenter)
        self.quality_label.setStyleSheet("font-size: 11px; font-weight: bold; margin-left: 10px; margin-right: 10px; padding: 0;")
        
        from ui.widgets.spinning_wheel_slider import SpinningWheelSlider
        self.quality_slider = SpinningWheelSlider()
        self.quality_slider.setObjectName("qualitySlider")
        self.quality_slider.setRange(0, 4)
        self.quality_slider.setValue(2)
        self.quality_slider.setFixedSize(180, 35)
        self.quality_slider.setEnabled(False)
        self.tooltip_manager.add_tooltip(self.quality_slider, "Bad = 15MB\nOkay = 25MB\nStandard = 45MB\nGood = 90MB\nMaximum = Original Video Size")
        self.quality_value_label = QLabel("Standard")
        self.quality_value_label.setAlignment(Qt.AlignHCenter)
        self.quality_value_label.setObjectName("qualityValueLabel")
        self.quality_value_label.setStyleSheet("font-size: 10px; margin: 0; padding: 0;")
        fm = QFontMetrics(self.quality_value_label.font())
        fixed_w = fm.horizontalAdvance("Maximum (For Social Media)") + 16
        self.quality_value_label.setMinimumWidth(fixed_w)

        def _on_quality_changed(value: int):
            titles = ["Bad (Lightning Speed Shares)", "Okay (Easier to Share)", "Standard", "Good", "Maximum (For Social Media)"]
            idx = max(0, min(4, int(value)))
            self.quality_value_label.setText(titles[idx])
            self.logger.info(f"OPTION: Video Output Quality -> {titles[idx]}")
        self.quality_slider.valueChanged.connect(_on_quality_changed)
        q_vbox = QVBoxLayout()
        q_vbox.setContentsMargins(0,0,0,0); q_vbox.setSpacing(2); q_vbox.setAlignment(Qt.AlignHCenter)
        q_vbox.addWidget(self.quality_label, alignment=Qt.AlignHCenter)
        q_vbox.addWidget(self.quality_slider, alignment=Qt.AlignHCenter)
        q_vbox.addWidget(self.quality_value_label, alignment=Qt.AlignHCenter)
        self.quality_container = QWidget()
        self.quality_container.setObjectName("qualityContainer")
        self.quality_container.setLayout(q_vbox)
        self.tooltip_manager.add_tooltip(self.quality_container, "Bad = 15MB\nOkay = 25MB\nStandard = 45MB\nGood = 90MB\nMaximum = Original Video Size")
        self.quality_container.setMinimumWidth(0)
        self.quality_container.setMaximumWidth(340)
        self.quality_container.setSizePolicy(QSizePolicy.Expanding, QSizePolicy.Fixed)
        self.process_button = QPushButton("PROCESS VIDEO")
        self.process_button.setObjectName("processButton")
        self.process_button.setFixedSize(140, 50)
        self.process_button.setSizePolicy(QSizePolicy.Fixed, QSizePolicy.Fixed)
        self.tooltip_manager.add_tooltip(self.process_button, "Enter")
        self.process_button.setStyleSheet(UIStyles.BUTTON_PROCESS)
        self.process_button.setCursor(Qt.PointingHandCursor)
        self.process_button.clicked.connect(self._on_process_clicked)
        self.process_button.setEnabled(False)
        self.cancel_button = ClickableButton("CANCEL")
        self.cancel_button.setObjectName("cancelButton")
        self.cancel_button.setFixedSize(140, 50)
        self.cancel_button.setSizePolicy(QSizePolicy.Fixed, QSizePolicy.Fixed)
        self.cancel_button.setStyleSheet(UIStyles.BUTTON_CANCEL)
        self.cancel_button.setVisible(False)
        self.cancel_button.clicked.connect(self.cancel_processing)
        self.is_processing = False
        self._pulse_phase = 0
        self._pulse_timer = QTimer(self)
        self._pulse_timer.setSingleShot(False)
        self._pulse_timer.timeout.connect(self._update_process_button_text)
        btn_layout = QHBoxLayout()
        btn_layout.setContentsMargins(0, 0, 0, 0)
        btn_layout.setSpacing(20)
        btn_layout.addWidget(self.cancel_button)
        btn_layout.addWidget(self.process_button)
        self.speed_spinbox = ClickableSpinBox()
        self.speed_spinbox.setDecimals(1)
        self.speed_spinbox.setSingleStep(0.1)
        self.speed_spinbox.setRange(0.5, 3.1)
        self.speed_spinbox.setValue(1.1)
        self.speed_spinbox.setFixedWidth(55)
        self.speed_spinbox.setFixedHeight(35)
        self.speed_spinbox.setStyleSheet(UIStyles.SPINBOX)
        self.speed_spinbox.setCursor(Qt.PointingHandCursor)
        self.speed_spinbox.setEnabled(False)
        self.speed_spinbox.valueChanged.connect(self._on_speed_changed)
        self.speed_label = QLabel("Speed Multiplier")
        self.speed_label.setStyleSheet("font-size: 11px; font-weight: bold; margin: 0; padding: 0;")
        speed_layout = QVBoxLayout(); speed_layout.setContentsMargins(0,0,0,0); speed_layout.setSpacing(2); speed_layout.setAlignment(Qt.AlignHCenter)
        speed_layout.addWidget(self.speed_label, alignment=Qt.AlignHCenter)
        speed_layout.addWidget(self.speed_spinbox, alignment=Qt.AlignHCenter)
        speed_widget = QWidget(); speed_widget.setLayout(speed_layout)
        speed_widget.setSizePolicy(QSizePolicy.Minimum, QSizePolicy.Fixed)
        self.granular_checkbox = QCheckBox("Granular Speed")
        self.granular_checkbox.setStyleSheet(UIStyles.CHECKBOX)
        self.granular_checkbox.setCursor(Qt.PointingHandCursor)
        self.granular_checkbox.setEnabled(False)
        self.granular_checkbox.clicked.connect(self._handle_granular_click)
        self.tooltip_manager.add_tooltip(self.granular_checkbox, "Enable variable speed segments throughout the video.")
        self.mobile_checkbox = QCheckBox("Mobile Format (Portrait)")
        self.mobile_checkbox.setStyleSheet(UIStyles.CHECKBOX)
        self.mobile_checkbox.setCursor(Qt.PointingHandCursor)
        self.mobile_checkbox.setEnabled(False)
        self.mobile_checkbox.setChecked(bool(self.config_manager.config.get('mobile_checked', False)))
        
        from PyQt5.QtWidgets import QLineEdit
        self.portrait_text_input = QLineEdit()
        self.portrait_text_input.setPlaceholderText("Overlay Text (Hebrew/English)")
        self.portrait_text_input.setEnabled(False)
        self.portrait_text_input.setStyleSheet("QLineEdit { background-color: #4a667a; color: white; border: 1px solid #266b89; border-radius: 4px; padding: 4px; font-size: 14px; }")
        self.teammates_checkbox = QCheckBox("Show Teammates Healthbar")
        self.teammates_checkbox.setStyleSheet("font-size: 11px; margin-left: 15px; margin-right: 0px; padding: 0;")
        self.teammates_checkbox.setChecked(bool(self.config_manager.config.get('teammates_checked', False)))
        self.teammates_checkbox.setEnabled(False)
        self.teammates_checkbox.toggled.connect(lambda c: self.logger.info("OPTION: Show Teammates Healthbar -> %s", c))
        self.teammates_checkbox.setCursor(Qt.PointingHandCursor)
        is_mob = self.mobile_checkbox.isChecked()
        self.teammates_checkbox.setVisible(is_mob)
        self.teammates_checkbox.setEnabled(is_mob)
        self.portrait_text_input.setVisible(is_mob)
        if not is_mob: self.teammates_checkbox.setChecked(False)
        process_controls = QGridLayout()
        process_controls.setContentsMargins(0, 0, 0, 0)
        process_controls.setHorizontalSpacing(16)
        process_controls.setVerticalSpacing(0)
        left_group = QHBoxLayout()
        left_group.setContentsMargins(0, 0, 0, 0)
        left_group.setSpacing(10)
        left_group.addWidget(self.mobile_checkbox)
        left_group.addWidget(self.teammates_checkbox)
        _left_col = QVBoxLayout()
        _left_col.setContentsMargins(0, 0, 0, 0)
        _left_col.setSpacing(6)
        _left_col.addLayout(left_group)
        _left_col.addWidget(self.portrait_text_input)
        self.left_group_widget = QWidget()
        self.left_group_widget.setLayout(_left_col)
        self.left_group_widget.setSizePolicy(QSizePolicy.MinimumExpanding, QSizePolicy.Fixed)
        right_group = QHBoxLayout()
        right_group.setContentsMargins(0, 0, 0, 0)
        right_group.setAlignment(Qt.AlignVCenter)
        right_group.addWidget(self.granular_checkbox, 0)
        right_group.addWidget(speed_widget, 0)
        self.right_group_widget = QWidget(); self.right_group_widget.setLayout(right_group)
        self.right_group_widget.setSizePolicy(QSizePolicy.Expanding, QSizePolicy.Fixed)
        process_controls.setColumnStretch(0, 1)
        process_controls.setColumnStretch(1, 1)
        process_controls.setColumnStretch(2, 1)
        process_controls.setColumnStretch(3, 1)
        self._safe_add_to_grid(process_controls, self.left_group_widget,  0, 0, 1, 1, Qt.AlignLeft  | Qt.AlignVCenter)
        self._safe_add_to_grid(process_controls, self.quality_container, 0, 1, 1, 1, Qt.AlignLeft  | Qt.AlignVCenter)
        process_controls.addLayout(btn_layout, 0, 2, 1, 1, Qt.AlignCenter | Qt.AlignVCenter)
        self._safe_add_to_grid(process_controls, self.right_group_widget, 0, 3, 1, 1, Qt.AlignRight | Qt.AlignVCenter)
        self.center_btn_container = process_controls

    def _init_status_bar(self):
        self.progress_bar = QProgressBar()
        self.progress_bar.setObjectName("mainProgressBar")
        self.progress_bar.setStyleSheet(UIStyles.PROGRESS_BAR)
        self.progress_bar.setValue(0)
        self.status_container = QWidget()
        self.status_container.setFixedHeight(18)
        status_layout = QHBoxLayout(self.status_container)
        status_layout.setContentsMargins(10, 0, 10, 0)
        status_layout.setSpacing(0)
        self.hardware_status_label = QLabel("")
        self.hardware_status_label.setObjectName("hardwareStatusLabel")
        self.hardware_status_label.setStyleSheet(UIStyles.LABEL_STATUS)
        self.hardware_status_label.setAlignment(Qt.AlignLeft | Qt.AlignVCenter)
        self.left_separator = QLabel("|")
        self.left_separator.setStyleSheet(UIStyles.LABEL_SEPARATOR)
        self.left_separator.setAlignment(Qt.AlignCenter)
        self.resolution_label = QLabel("")
        self.resolution_label.setObjectName("resolutionLabel")
        self.resolution_label.setStyleSheet(UIStyles.LABEL_STATUS)
        self.resolution_label.setAlignment(Qt.AlignCenter)
        self.right_separator = QLabel("|")
        self.right_separator.setStyleSheet(UIStyles.LABEL_SEPARATOR)
        self.right_separator.setAlignment(Qt.AlignRight | Qt.AlignVCenter)
        status_layout.addWidget(self.hardware_status_label, 0)
        status_layout.addWidget(self.left_separator, 0)
        status_layout.addStretch(1)
        status_layout.addWidget(self.resolution_label, 0)
        status_layout.addStretch(1)
        status_layout.addWidget(self.right_separator, 0)
        if hasattr(self, 'status_bar') and self.status_bar:
            self.status_bar.setFixedHeight(20)
            self.status_bar.setStyleSheet(UIStyles.STATUS_BAR)
            self.status_bar.addPermanentWidget(self.status_container, 1)
        self.progress_update_signal.connect(self.on_progress)
        self.status_update_signal.connect(self.on_phase_update)
        self.process_finished_signal.connect(self.on_process_finished)

    def _build_right_panel(self):
        self.right_panel = QWidget()
        self.right_panel.setSizePolicy(QSizePolicy.Fixed, QSizePolicy.Expanding)
        right_col = QVBoxLayout(self.right_panel)
        right_col.setContentsMargins(0, 0, 0, 0)
        drop_slider_row = QHBoxLayout()
        drop_slider_row.setContentsMargins(0, 0, 0, 0)
        drop_slider_row.setSpacing(40)
        drop_slider_row.setAlignment(Qt.AlignLeft | Qt.AlignTop)
        self.drop_area = DropAreaFrame()
        self.drop_area.setObjectName("dropArea")
        self.drop_area.setFocusPolicy(Qt.NoFocus)
        self.drop_area.file_dropped.connect(self.handle_file_selection)
        self.drop_area.setCursor(Qt.PointingHandCursor)
        drop_layout = QVBoxLayout(self.drop_area)
        drop_layout.setContentsMargins(0, 0, 0, 0)
        self.drop_label = QLabel("Drag & Drop\r\na Video File Here:")
        self.drop_label.setStyleSheet("font-size: 11px; font-weight: bold; margin: 0; padding: 0;")
        self.drop_label.setAlignment(Qt.AlignCenter)
        drop_layout.addWidget(self.drop_label)
        self.drop_area.setSizePolicy(QSizePolicy.Fixed, QSizePolicy.Fixed)
        self.drop_area.setFixedHeight(180)
        self.drop_area.setFixedWidth(140)
        drop_slider_row.addWidget(self.drop_area)
        self.slider_vbox_layout = QVBoxLayout()
        self.slider_vbox_layout.setContentsMargins(0, 0, 0, 0)
        self.slider_vbox_layout.setSpacing(0)
        self.slider_vbox_layout.setAlignment(Qt.AlignTop | Qt.AlignLeft)
        self.music_volume_slider = QSlider(Qt.Vertical, self)
        self.music_volume_slider.setObjectName("musicVolumeSlider")
        self.music_volume_slider.setFixedWidth(40)
        self.music_volume_slider.setStyleSheet(UIStyles.SLIDER_MUSIC_VERTICAL_METALLIC)
        self.music_volume_slider.setRange(0, 100)
        self.music_volume_slider.setSingleStep(1)
        self.music_volume_slider.setPageStep(15)
        self.music_volume_slider.setTickInterval(1)
        self.music_volume_slider.setTickPosition(QSlider.TicksBothSides)
        self.music_volume_slider.setTracking(True)
        self.music_volume_slider.setInvertedAppearance(True)
        self.music_volume_slider.setVisible(True)
        self.music_volume_slider.setFocusPolicy(Qt.NoFocus)
        self.music_volume_slider.installEventFilter(self)
        self.music_volume_slider.setCursor(Qt.PointingHandCursor)
        self.music_volume_slider.setFixedHeight(170)
        self.music_volume_slider.setEnabled(False)
        self.music_volume_label = QLabel("80%")
        self.music_volume_label.setAlignment(Qt.AlignHCenter)
        self.music_volume_label.setVisible(False)
        self.music_volume_label.setStyleSheet("font-size: 11px; font-weight: bold; margin-top: 20px; margin-bottom: 10px;")
        init_eff = self._music_eff(int(self.music_volume_slider.value()))
        self.music_volume_label.setText(f"{init_eff}%")
        self.music_volume_badge = QLabel("100%", self)
        self.music_volume_badge.setObjectName("musicVolumeBadge")
        self.music_volume_badge.setStyleSheet("color: white; background: rgba(0,0,0,160); padding: 2px 6px; border-radius: 6px; font-weight: bold;")
        self.music_volume_badge.hide()
        self.slider_vbox_layout.addWidget(self.music_volume_slider)
        self.slider_vbox_layout.addWidget(self.music_volume_label)
        drop_slider_row.addLayout(self.slider_vbox_layout)
        right_col.addLayout(drop_slider_row)
        self.upload_button = QPushButton("📂 UPLOAD VIDEO FILE 📂")
        self.upload_button.clicked.connect(self.select_file)
        self.upload_button.setFixedHeight(55)
        self.upload_button.setStyleSheet("font-size: 11px; font-weight: bold; margin-top: 20px; padding: 7px;")
        self.upload_button.setCursor(Qt.PointingHandCursor)
        right_col.addWidget(self.upload_button)
        self.add_music_checkbox = QCheckBox("Add Background Music")
        self.add_music_checkbox.setToolTip("Toggle background MP3 mixing from the ./mp3 folder.")
        self.add_music_checkbox.setChecked(False)
        self.add_music_checkbox.setCursor(Qt.PointingHandCursor)
        self.add_music_checkbox.setEnabled(False)
        self.add_music_checkbox.setStyleSheet("font-size: 11px; font-weight: bold; margin-top: 20px; margin-bottom: 20px;")
        right_col.addWidget(self.add_music_checkbox)
        self.music_combo = QComboBox()
        self.music_combo.setFixedWidth(250)
        self.music_combo.setCursor(Qt.PointingHandCursor)
        self.music_combo.setVisible(False)
        self.music_combo.setEnabled(False)
        right_col.addWidget(self.music_combo)
        self.music_offset_input = QDoubleSpinBox()
        self.music_offset_input.setPrefix("Music Start (s): ")
        self.music_offset_input.setDecimals(2)
        self.music_offset_input.setSingleStep(0.5)
        self.music_offset_input.setRange(0.0, 0.0)
        self.music_offset_input.setValue(0.0)
        self.music_offset_input.setCursor(Qt.PointingHandCursor)
        self.music_offset_input.setVisible(False)
        self.music_offset_input.setEnabled(False)
        right_col.addWidget(self.music_offset_input)
        right_col.addStretch(1)
        self.no_fade_checkbox = QCheckBox("Disable Fade-In/Out", self)
        self.no_fade_checkbox.setChecked(False)
        self.no_fade_checkbox.setSizePolicy(QSizePolicy.Fixed, QSizePolicy.Fixed)
        self.no_fade_checkbox.setEnabled(False)
        self.no_fade_checkbox.toggled.connect(lambda c: self.logger.info("OPTION: Disable Fade-In/Out -> %s", c))
        self.no_fade_checkbox.setCursor(Qt.PointingHandCursor)
        bottom_box = QWidget(self.right_panel)
        bottom_box.setSizePolicy(QSizePolicy.Maximum, QSizePolicy.Maximum)
        bb = QVBoxLayout(bottom_box)
        bb.setSpacing(25)
        self.merge_btn = QPushButton("VIDEO MERGER")
        self.merge_btn.setStyleSheet(UIStyles.BUTTON_TOOL)
        self.merge_btn.setSizePolicy(QSizePolicy.Fixed, QSizePolicy.Fixed)
        self.merge_btn.setCursor(Qt.PointingHandCursor)
        self.merge_btn.clicked.connect(self.launch_video_merger)
        self.crop_tool_btn = QPushButton("CROP SETTING")
        self.crop_tool_btn.setStyleSheet(UIStyles.BUTTON_TOOL)
        self.crop_tool_btn.setSizePolicy(QSizePolicy.Fixed, QSizePolicy.Fixed)
        self.crop_tool_btn.setCursor(Qt.PointingHandCursor)
        self.crop_tool_btn.clicked.connect(self.launch_crop_tool)
        self.adv_editor_btn = QPushButton("ADVANCED\n VIDEO EDITOR")
        self.adv_editor_btn.setStyleSheet(UIStyles.BUTTON_TOOL)
        self.adv_editor_btn.setSizePolicy(QSizePolicy.Fixed, QSizePolicy.Fixed)
        self.adv_editor_btn.setCursor(Qt.PointingHandCursor)
        self.adv_editor_btn.clicked.connect(self.launch_advanced_editor)
        bb.addWidget(self.merge_btn, 0, Qt.AlignCenter)
        bb.addWidget(self.crop_tool_btn, 0, Qt.AlignCenter)
        bb.addWidget(self.adv_editor_btn, 0, Qt.AlignCenter)
        bb.addWidget(self.no_fade_checkbox, 0, Qt.AlignRight)
        right_col.addWidget(bottom_box, 0, Qt.AlignBottom | Qt.AlignRight)
        self.music_volume_slider.valueChanged.connect(self._on_music_volume_changed)
        self.music_volume_slider.valueChanged.connect(lambda _: self._update_music_badge())
        QTimer.singleShot(0, self._update_music_badge)
        self.add_music_checkbox.toggled.connect(self._on_add_music_toggled)
        self.music_combo.currentIndexChanged.connect(self._on_music_selected)
        self._populate_music_combo()

    def _update_ui_positions(self, checked):
        self.drop_area.setStyleSheet(UIStyles.get_drop_area_style(checked))
        s_top = 32 if checked else 32
        self.slider_vbox_layout.setContentsMargins(0, s_top, 0, 0)

    def _on_mobile_toggled(self, checked: bool):
        self.logger.info("OPTION: Mobile Format -> %s", checked)
        if hasattr(self, "teammates_checkbox"):
            self.teammates_checkbox.setVisible(checked)
            self.teammates_checkbox.setEnabled(checked)
            if not checked:
                self.teammates_checkbox.setChecked(False)
        if hasattr(self, 'portrait_text_input'):
            self.portrait_text_input.setVisible(checked)
            if not checked:
                self.portrait_text_input.clear()
        self._update_portrait_mask_overlay_state()

    def _update_portrait_mask_overlay_state(self):
        if not hasattr(self, 'portrait_mask_overlay') or not self.portrait_mask_overlay:
            return
        res = getattr(self, 'original_resolution', "1920x1080")
        if not res:
            res = "1920x1080"
        is_mobile = hasattr(self, "mobile_checkbox") and self.mobile_checkbox.isChecked()
        if is_mobile and self.input_file_path:
            if self.video_surface.isVisible():
                top_left = self.video_surface.mapToGlobal(self.video_surface.rect().topLeft())
                self.portrait_mask_overlay.setGeometry(
                    top_left.x(), top_left.y(),
                    self.video_surface.width(), self.video_surface.height()
                )
            self.portrait_mask_overlay.set_video_info(res, self.video_surface.size())
            self.portrait_mask_overlay.setVisible(True)
            self.portrait_mask_overlay.raise_()
        else:
            self.portrait_mask_overlay.setVisible(False)

    def set_resolution_text(self, res_str: str):
        if hasattr(self, 'resolution_label'):
            if res_str:
                self.resolution_label.setText(f"Video Resolution: {res_str}")
            else:
                self.resolution_label.setText("")