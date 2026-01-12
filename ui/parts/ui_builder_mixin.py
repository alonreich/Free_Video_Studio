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
        thumbnail frame. Validates that the frame is WITHIN the user's trim.
        """
        try:
            pos_ms = 0
            try:
                pos_ms = int(self.positionSlider.value())
            except Exception:
                pass
            if (not pos_ms) and getattr(self, 'vlc_player', None):
                pos_ms = int(self.vlc_player.get_time() or 0)
            pos_s = float(pos_ms) / 1000.0
            if pos_s <= 0.0 and self.original_duration <= 0.0:
                QMessageBox.information(self, "No Position", "Move the playhead to a frame first.")
                return
            start_s = (self.start_minute_input.value() * 60) + self.start_second_input.value()
            end_s   = (self.end_minute_input.value()   * 60) + self.end_second_input.value()
            self.selected_intro_abs_time = pos_s
            mm = int(self.selected_intro_abs_time // 60)
            ss = self.selected_intro_abs_time % 60.0
            label = f"📸 Thumbnail\n Set: {mm:02d}:{ss:05.2f}"
            self.thumb_pick_btn.setText(label)
            if hasattr(self, "logger"):
                self.logger.info("THUMB: user picked thumbnail at %.3fs (absolute timeline, within trim)", self.selected_intro_abs_time)
            self.status_update_signal.emit("Thumbnail frame selected from current position.")
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
                text = f"Processing{dots}"
                spinner = "◐◓◑◒"
                glyph = spinner[(self._pulse_phase // 2) % len(spinner)]
                px = 26
                pm = QPixmap(px, px)
                pm.fill(Qt.transparent)
                p = QPainter(pm)
                f = QFont(self.font())
                f.setPointSize(px)
                p.setFont(f)
                p.setPen(Qt.black)
                p.drawText(pm.rect(), Qt.AlignCenter, glyph)
                p.end()
                self.process_button.setText(text)
                self.process_button.setIcon(QIcon(pm))
                self.process_button.setIconSize(QSize(px, px))
            else:
                self.process_button.setText("Process Video")
                self.process_button.setIcon(QIcon())
        except Exception:
            pass

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
        """
        try:
            has_duration = (self.positionSlider.maximum() > 0) or (self.end_minute_input.maximum() > 0)
            if has_duration:
                if not self.process_button.isEnabled():
                    if hasattr(self, "logger"): self.logger.info("READY: Media loaded; enabling Process button")
                    self.process_button.setEnabled(True)
                    self._ensure_default_trim()
        except Exception:
            pass

    def launch_video_merger(self):
        try:
            self.logger.info("ACTION: Launching Video Merger…")
            utilities_dir = os.path.join(self.base_dir, 'utilities')
            merger_main_path = os.path.join(utilities_dir, 'video_merger.py')
            if not os.path.exists(merger_main_path):
                self.logger.critical(f"ERROR: Video Merger script not found at {merger_main_path}")
                QMessageBox.critical(self, "Error", "Video Merger script not found.")
                return
            command = [sys.executable, "-B", merger_main_path]
            subprocess.Popen(command, cwd=self.base_dir)
            self.close()
        except Exception as e:
            self.logger.critical(f"ERROR: Failed to launch Video Merger. Error: {e}")
            QMessageBox.critical(self, "Launch Failed", f"Could not launch Video Merger. Error: {e}")

    def eventFilter(self, obj, event):
        if event.type() == QEvent.Resize:
            if obj is getattr(self, "player_col_container", None):
                if hasattr(self, "_center_timer"):
                    self._center_timer.start(16)
            elif obj is getattr(self, "video_surface", None):
                if hasattr(self, 'portrait_mask_overlay'):
                    self._update_portrait_mask_overlay_state()
        return super().eventFilter(obj, event)

    def _recenter_process_controls(self):
        """
        Exact centering that respects the *current* side widths (including Mobile Format visibility)
        and applies a single safe nudge to eliminate 1–2px drift.
        """
        if getattr(self, "_centering", False):
            return
        self._centering = True
        try:
            grid = self.center_btn_container
            if not isinstance(grid, QGridLayout):
                return
            pc = self.player_col_container
            if pc is None:
                return
            container_w = pc.width() or pc.sizeHint().width()
            btn_w = self.process_button.width() or self.process_button.sizeHint().width()
            if container_w <= 0 or btn_w <= 0:
                return
            gap = grid.horizontalSpacing() or 0
            cx  = self.playPauseButton.mapTo(self.player_col_container,
                                            self.playPauseButton.rect().center()).x()

            def _minw(w):
                if not w or not w.isVisible():
                    return 0
                return w.sizeHint().width() + 8
            left_min  = _minw(self.left_group_widget)
            right_min = _minw(self.right_group_widget)
            L = max(left_min, int(round(cx - (btn_w / 2.0) - gap)))
            R = max(right_min, container_w - (L + btn_w + 2 * gap))
            if R < right_min:
                L = max(left_min, container_w - (btn_w + 2 * gap + right_min))
                R = max(right_min, container_w - (L + btn_w + 2 * gap))
            total = L + btn_w + 2 * gap + R
            if total > container_w:
                over = total - container_w
                takeL = min(over // 2, max(0, L - left_min)); L -= takeL; over -= takeL
                takeR = min(over,        max(0, R - right_min)); R -= takeR; over -= takeR
                if over > 0:
                    if R - right_min >= over:
                        R -= over
                    elif L - left_min >= over:
                        L -= over
            actual_cx = L + gap + (btn_w / 2.0)
            delta = (actual_cx - cx)
            if abs(delta) >= 0.5:
                if delta > 0:
                    shift = min(delta, max(0, L - left_min))
                    L -= shift; R += shift
                else:
                    shift = min(-delta, max(0, R - right_min))
                    L += shift; R -= shift
            curL = grid.columnMinimumWidth(0)
            curR = grid.columnMinimumWidth(2)
            if abs(L - curL) >= 2 or abs(R - curR) >= 2:
                self.setUpdatesEnabled(False)
                grid.setColumnStretch(0, 0); grid.setColumnStretch(1, 0); grid.setColumnStretch(2, 0)
                grid.setColumnMinimumWidth(0, max(0, L))
                grid.setColumnMinimumWidth(2, max(0, R))
                if hasattr(self, "quality_container"):
                    reserve_for_speed = 260
                    room = max(140, R - reserve_for_speed)
                    self.quality_container.setMaximumWidth(min(room, 340))
                self.setUpdatesEnabled(True)
        finally:
            self._centering = False

    def showEvent(self, e):
        super().showEvent(e)
        if hasattr(self, "_center_timer"):
            self._center_timer.start(0)

    def resizeEvent(self, e):
        super().resizeEvent(e)
        if hasattr(self, "_center_timer"):
            self._center_timer.start(16)
        if hasattr(self, 'portrait_mask_overlay'):
            self._update_portrait_mask_overlay_state()

    def init_ui(self):
        main_layout = QHBoxLayout()
        self.setContentsMargins(0, 0, 0, 0)
        left_layout = QVBoxLayout()
        left_layout.setContentsMargins(0, 0, 0, 0)
        left_layout.setSpacing(0)
        top_row = QHBoxLayout()
        top_row.setSpacing(12)
        self._top_row = top_row
        player_col = QVBoxLayout()
        player_col.setSpacing(6)
        video_and_volume_layout = QHBoxLayout()
        video_and_volume_layout.setContentsMargins(0, 0, 0, 0)
        video_and_volume_layout.setSpacing(6)
        volume_stack_layout = QVBoxLayout()
        volume_stack_layout.setContentsMargins(0, 0, 0, 0)
        volume_stack_layout.setSpacing(0)
        volume_stack_layout.setAlignment(Qt.AlignTop)
        self.volume_slider = QSlider(Qt.Vertical)
        self.volume_slider.setObjectName("volumeSlider")
        self.volume_slider.setRange(0, 100)
        self.volume_slider.setSingleStep(1)
        self.volume_slider.setPageStep(15)
        self.volume_slider.setTickInterval(10)
        self.volume_slider.setTickPosition(QSlider.TicksBothSides)
        self.volume_slider.setTracking(True)
        self.volume_slider.setInvertedAppearance(True)
        self.tooltip_manager.add_tooltip(self.volume_slider, "Adjust Volume: ↑ / ↓\nLarge Step: Shift + ↑ / ↓")
        try:
            eff = int(self.config_manager.config.get('last_volume', 100))
        except Exception:
            eff = 100
        raw = self.volume_slider.maximum() + self.volume_slider.minimum() - eff
        self.volume_slider.setValue(max(self.volume_slider.minimum(), min(self.volume_slider.maximum(), raw)))
        self.volume_slider.setStyleSheet("""
            QSlider#volumeSlider {
                padding: 0px;
                background: transparent;
                border: 0;
            }
            QSlider#volumeSlider::groove:vertical {
                margin: 0px;
                border: 1px solid #1f2a36;
                background: qlineargradient(x1:0, y1:1, x2:0, y2:0,
                    stop:0   #e64c4c,
                    stop:0.25 #f7a8a8,
                    stop:0.50 #f2f2f2,
                    stop:0.75 #7bcf43,
                    stop:1   #009b00);
                width: 20px;
                border-radius: 3px;
            }
            QSlider#volumeSlider::handle:vertical {
                height: 40px;
                width: 22px;
                margin: 0 -2px;
                border: 1px solid #1f2a36;
                border-radius: 4px;
                background: qlineargradient(x1:0, y1:0, x2:0, y2:1,
                    stop:0 #455A64,
                    stop:0.40 #455A64,
                    stop:0.42 #90A4AE, stop:0.44 #90A4AE,
                    stop:0.46 #455A64,
                    stop:0.48 #455A64,
                    stop:0.50 #90A4AE, stop:0.52 #90A4AE,
                    stop:0.54 #455A64,
                    stop:0.56 #455A64,
                    stop:0.58 #90A4AE, stop:0.60 #90A4AE,
                    stop:0.62 #455A64,
                    stop:1 #455A64);
            }
            QSlider#volumeSlider::handle:vertical:hover {
                border: 1px solid #90A4AE;
                background: qlineargradient(x1:0, y1:0, x2:0, y2:1,
                    stop:0 #546E7A,
                    stop:0.40 #546E7A, stop:0.42 #CFD8DC, stop:0.44 #CFD8DC,
                    stop:0.46 #546E7A, stop:0.48 #546E7A, stop:0.50 #CFD8DC, stop:0.52 #CFD8DC,
                    stop:0.54 #546E7A, stop:0.56 #546E7A, stop:0.58 #CFD8DC, stop:0.60 #CFD8DC,
                    stop:0.62 #546E7A, stop:1 #546E7A);
            }
            QSlider#volumeSlider::sub-page:vertical,
            QSlider#volumeSlider::add-page:vertical {
                background: transparent;
            }
        """)
        self.volume_slider.valueChanged.connect(self._on_master_volume_changed)
        self.volume_slider.sliderMoved.connect(lambda _: self._update_volume_badge())
        self.volume_slider.installEventFilter(self)
        self.volume_badge = QLabel("0%", self.volume_slider)
        self.volume_badge.setObjectName("volumeBadge")
        self.volume_badge.setStyleSheet("color: white; background: rgba(0,0,0,160); padding: 2px 6px; border-radius: 6px; font-weight: bold;")
        self.volume_badge.adjustSize()
        self.volume_badge.hide()
        volume_stack_layout.addWidget(self.volume_badge, 0, Qt.AlignHCenter)
        volume_stack_layout.addWidget(self.volume_slider, 1, Qt.AlignHCenter)
        video_and_volume_layout.addLayout(volume_stack_layout)
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
        _center_row.addWidget(self.video_surface)
        video_layout.addWidget(self.video_viewport_container, stretch=1)
        if PortraitMaskOverlay:
            self.portrait_mask_overlay = PortraitMaskOverlay(self.video_frame)
            self.portrait_mask_overlay.hide()
        else:
            self.portrait_mask_overlay = None
        self.video_surface.installEventFilter(self)
        video_and_volume_layout.addWidget(self.video_frame, stretch=1)
        player_col.addLayout(video_and_volume_layout)
        player_col.setStretch(0, 1)
        self.positionSlider = TrimmedSlider(self)
        self.positionSlider.setRange(0, 0)
        self.positionSlider.setFixedHeight(50)
        self.positionSlider.setSizePolicy(QSizePolicy.Expanding, QSizePolicy.Fixed)
        self.positionSlider.setObjectName("timelineSlider")
        player_col.addWidget(self.positionSlider)
        player_col.setStretch(1, 0)
        player_container = QWidget()
        player_container.setLayout(player_col)
        self.player_col_container = player_container
        self.player_col_container.installEventFilter(self)
        top_row.addWidget(player_container, stretch=6)
        left_layout.addLayout(top_row)
        main_layout.addLayout(left_layout)
        self.setFocusPolicy(Qt.StrongFocus)
        self.tooltip_manager.add_tooltip(self.positionSlider, "Seek: ← / →\nFast Seek: Shift + ← / →\nFine Seek: Ctrl + ← / →")
        self.positionSlider.sliderMoved.connect(self.set_vlc_position)
        self.positionSlider.rangeChanged.connect(lambda *_: self._maybe_enable_process())
        self.positionSlider.trim_times_changed.connect(self._on_slider_trim_changed)
        self.positionSlider.raise_()
        player_col.addWidget(self.positionSlider)
        self.playPauseButton = QPushButton("Play")
        self.playPauseButton.setObjectName("playPauseButton")
        self.playPauseButton.setIcon(self.style().standardIcon(QStyle.SP_MediaPlay))
        self.playPauseButton.clicked.connect(self.toggle_play_pause)
        self.playPauseButton.setFocusPolicy(Qt.NoFocus)
        self.tooltip_manager.add_tooltip(self.playPauseButton, "Spacebar")
        self.playPauseButton.setStyleSheet("""
            QPushButton {
                background-color: #59A06D;
                color: white;
                font-size: 14px;
                padding: 4px 8px;
                border-radius: 6px;
            }
            QPushButton:hover {
                background-color: #6fb57f;
            }
            QPushButton:pressed {
                background-color: #4a865a;
            }
        """)
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
        self.thumb_pick_btn = QPushButton("📸 Set Thumbnail 📸")
        self.thumb_pick_btn.setObjectName("thumbPickBtn")
        self.thumb_pick_btn.setStyleSheet(_std_btn_style)
        self.tooltip_manager.add_tooltip(self.thumb_pick_btn, "Select Custom Thumbnail Picture For Sharing")
        self.thumb_pick_btn.setFocusPolicy(Qt.NoFocus)
        self.thumb_pick_btn.clicked.connect(self._pick_thumbnail_from_current_frame)
        _left_col = QVBoxLayout()
        _left_col.setContentsMargins(0, 0, 0, 0)
        _left_col.setSpacing(6)
        left_group = QHBoxLayout()
        trim_layout = QHBoxLayout()
        trim_layout.setContentsMargins(0, 0, 0, 0)
        trim_layout.setSpacing(14)
        self.start_minute_input = QSpinBox(); self.start_minute_input.setRange(0, 0)
        self.start_minute_input.setFixedHeight(35)
        self.start_second_input = QDoubleSpinBox(); self.start_second_input.setRange(0.00, 59.99); self.start_second_input.setDecimals(2); self.start_second_input.setSingleStep(0.01)
        self.start_second_input.setFixedHeight(35)
        self.end_minute_input   = QSpinBox(); self.end_minute_input.setRange(0, 0)
        self.end_minute_input.setFixedHeight(35)
        self.end_second_input   = QDoubleSpinBox(); self.end_second_input.setRange(0.00, 59.99); self.end_second_input.setDecimals(2); self.end_second_input.setSingleStep(0.01)
        self.end_second_input.setFixedHeight(35)
        self.start_minute_input.valueChanged.connect(self._on_trim_spin_changed)
        self.start_second_input.valueChanged.connect(self._on_trim_spin_changed)
        self.end_minute_input.valueChanged.connect(self._on_trim_spin_changed)
        self.end_second_input.valueChanged.connect(self._on_trim_spin_changed)
        self.start_trim_button = QPushButton("Set Start")
        self.start_trim_button.setObjectName("startTrimButton")
        self.start_trim_button.setStyleSheet(_std_btn_style)
        self.start_trim_button.clicked.connect(self.set_start_time)
        self.start_trim_button.setFocusPolicy(Qt.NoFocus)
        self.tooltip_manager.add_tooltip(self.start_trim_button, "[")
        self.end_trim_button = QPushButton("Set End")
        self.end_trim_button.setObjectName("endTrimButton")
        self.end_trim_button.setStyleSheet(_std_btn_style)
        self.end_trim_button.clicked.connect(self.set_end_time)
        self.end_trim_button.setFocusPolicy(Qt.NoFocus)
        self.tooltip_manager.add_tooltip(self.end_trim_button, "]")
        for spin in (self.start_minute_input, self.start_second_input, self.end_minute_input, self.end_second_input):
            spin.setMaximumWidth(48)
        self.start_trim_button.setFixedWidth(90)
        self.end_trim_button.setFixedWidth(90)
        self.playPauseButton.setFixedWidth(140)
        self.playPauseButton.setFixedHeight(35)
        start_min_layout = QHBoxLayout(); start_min_layout.setContentsMargins(0,0,0,0); start_min_layout.setSpacing(0)
        start_sec_layout = QHBoxLayout(); start_sec_layout.setContentsMargins(0,0,0,0); start_sec_layout.setSpacing(0)
        end_min_layout   = QHBoxLayout(); end_min_layout.setContentsMargins(0,0,0,0); end_min_layout.setSpacing(0)
        end_sec_layout   = QHBoxLayout(); end_sec_layout.setContentsMargins(0,0,0,0); end_sec_layout.setSpacing(0)
        lbl_start_min = QLabel("Start Min:"); lbl_start_min.setStyleSheet("font-size: 10px;")
        lbl_start_sec = QLabel("Sec:");       lbl_start_sec.setStyleSheet("font-size: 10px;")
        lbl_end_min   = QLabel("End Min:");   lbl_end_min.setStyleSheet("font-size: 10px;")
        lbl_end_sec   = QLabel("Sec:");       lbl_end_sec.setStyleSheet("font-size: 10px;")
        start_min_layout.addWidget(lbl_start_min); start_min_layout.addWidget(self.start_minute_input)
        start_sec_layout.addWidget(lbl_start_sec); start_sec_layout.addWidget(self.start_second_input)
        end_min_layout.addWidget(lbl_end_min);     end_min_layout.addWidget(self.end_minute_input)
        end_sec_layout.addWidget(lbl_end_sec);     end_sec_layout.addWidget(self.end_second_input)
        start_group = QHBoxLayout(); start_group.setContentsMargins(0,0,0,0); start_group.setSpacing(0)
        start_group.addLayout(start_min_layout); start_group.addLayout(start_sec_layout)
        end_group = QHBoxLayout();   end_group.setContentsMargins(0,0,0,0);   end_group.setSpacing(0)
        end_group.addLayout(end_min_layout);     end_group.addLayout(end_sec_layout)
        trim_layout.addLayout(start_group)
        trim_layout.addWidget(self.start_trim_button)
        trim_layout.addSpacing(14)
        trim_layout.addWidget(self.playPauseButton)
        trim_layout.addSpacing(14)
        trim_layout.addWidget(self.end_trim_button)
        trim_layout.addLayout(end_group)
        self.merge_btn = QPushButton("Merge Multiple \nVideos Into One")
        self.merge_btn.setStyleSheet(
            "background-color: #bfa624; color: black; font-weight:600;"
            "padding:6px 12px; border-radius:6px;"
        )
        self.merge_btn.setSizePolicy(QSizePolicy.Fixed, QSizePolicy.Fixed)
        self.merge_btn.clicked.connect(self.launch_video_merger)
        self.boss_hp_checkbox = QCheckBox("Boss HP")
        self.boss_hp_checkbox.setObjectName("bossHpCheckbox")
        boss_hp_tooltip_text = "<p style='font-family: Arial; font-size: 13pt; font-weight: normal;'>For videos which you are the boss charachter in them</p>"
        self.tooltip_manager.add_tooltip(self.boss_hp_checkbox, boss_hp_tooltip_text)
        self.boss_hp_checkbox.setStyleSheet("font-size: 10px; font-weight: normal;")
        self.boss_hp_checkbox.setChecked(False)
        self.boss_hp_checkbox.toggled.connect(self._on_boss_hp_toggled)
        trim_container = QHBoxLayout()
        trim_container.setContentsMargins(0, 0, 0, 0)
        trim_container.addWidget(self.thumb_pick_btn, 0, Qt.AlignLeft)
        trim_container.addSpacing(30)
        trim_container.addWidget(self.boss_hp_checkbox, 0, Qt.AlignLeft)
        trim_container.addStretch(1)
        trim_container.addLayout(trim_layout)
        trim_container.addStretch(1)
        player_col.addLayout(trim_container)
        self.thumb_pick_btn.setSizePolicy(QSizePolicy.Maximum, QSizePolicy.Fixed)
        self.quality_label = QLabel("Output Quality")
        self.quality_label.setAlignment(Qt.AlignHCenter)
        self.quality_label.setStyleSheet("font-size: 11px; font-weight: bold; margin-left: 10px; margin-right: 10px; padding: 0;")
        self.quality_slider = QSlider(Qt.Horizontal)
        self.quality_slider.setRange(0, 4)
        self.quality_slider.setSingleStep(1)
        self.quality_slider.setPageStep(1)
        self.quality_slider.setTickInterval(1)
        self.quality_slider.setTickPosition(QSlider.TicksBelow)
        self.quality_slider.setValue(2)
        self.quality_slider.setFixedHeight(15)
        self.quality_slider.setMinimumWidth(150)
        self.quality_slider.setMaximumWidth(300)
        self.quality_slider.setSizePolicy(QSizePolicy.Expanding, QSizePolicy.Fixed)
        self.tooltip_manager.add_tooltip(self.quality_slider,
            "Bad = 15MB\n"
            "Okay = 25MB\n"
            "Standard = 45MB\n"
            "Good = 90MB\n"
            "Maximum = Original Video Size"
        )
        _knob = self.positionSlider.palette().highlight().color().name()
        self.quality_slider.setObjectName("qualitySlider")
        self.quality_slider.setStyleSheet(
            "QSlider#qualitySlider {"
            "  padding: 0px; border: 0; background: transparent;"
            "}"
            "QSlider#qualitySlider::groove:horizontal {"
            "  margin: 0px; border: 0px solid #bbb;"
            "  background: qlineargradient(x1:0, y1:0, x2:1, y2:0," 
            "    stop:0   #e64c4c, stop:0.25 #f7a8a8, stop:0.5  #f2f2f2," 
            "    stop:0.75 #7bcf43, stop:1   #009b00);"
            "  height: 20px; border-radius: 6px;"
            "}"
            "QSlider#qualitySlider::handle:horizontal {"
            "  border: 1px solid #1f2a36;"
            "  width: 30px; height: 26px; margin: 0px 0; border-radius: 4px;"
            "  background: qlineargradient(x1:0, y1:0, x2:1, y2:0,"
            "    stop:0 #546E7A, stop:0.33 #546E7A, stop:0.35 #90A4AE,"
            "    stop:0.37 #90A4AE, stop:0.39 #546E7A, stop:0.47 #546E7A,"
            "    stop:0.49 #90A4AE, stop:0.51 #90A4AE, stop:0.53 #546E7A,"
            "    stop:0.61 #546E7A, stop:0.63 #90A4AE, stop:0.65 #90A4AE,"
            "    stop:0.67 #546E7A, stop:1.0 #546E7A);"
            "}"
            "QSlider#qualitySlider::handle:horizontal:hover {"
            "  border: 1px solid #90A4AE;"
            "  background: qlineargradient(x1:0, y1:0, x2:1, y2:0,"
            "    stop:0 #546E7A, stop:0.33 #546E7A, stop:0.35 #90A4AE,"
            "    stop:0.37 #90A4AE, stop:0.39 #546E7A, stop:0.47 #546E7A,"
            "    stop:0.49 #90A4AE, stop:0.51 #90A4AE, stop:0.53 #546E7A,"
            "    stop:0.61 #546E7A, stop:0.63 #90A4AE, stop:0.65 #90A4AE,"
            "    stop:0.67 #546E7A, stop:1.0 #546E7A);"
            "}"
            "QSlider#qualitySlider::sub-page:horizontal, QSlider#qualitySlider::add-page:horizontal { background: transparent; }"
        )
        self.quality_value_label = QLabel("Standard")
        self.quality_value_label.setAlignment(Qt.AlignHCenter)
        self.quality_value_label.setStyleSheet("font-size: 10px; margin: 0; padding: 0;")
        fm = QFontMetrics(self.quality_value_label.font())
        fixed_w = fm.horizontalAdvance("Maximum (For Social Media)") + 16
        self.quality_value_label.setMinimumWidth(fixed_w)

        def _on_quality_changed(value: int):
            titles = [
                "Bad (Lightning Speed Shares)",
                "Okay (Easier to Share)",
                "Standard",
                "Good",
                "Maximum (For Social Media)",
            ]
            idx = max(0, min(4, int(value)))
            self.quality_value_label.setText(titles[idx])
            self.logger.info(f"OPTION: Video Output Quality -> {titles[idx]}")
        self.quality_slider.valueChanged.connect(_on_quality_changed)
        self.process_button = QPushButton("Process Video")
        self.process_button.setObjectName("processButton")
        self.process_button.setFixedSize(200, 70)
        self.tooltip_manager.add_tooltip(self.process_button, "Enter")
        self._original_process_btn_style = """
            QPushButton {
                background-color: #2ab22a;
                color: black;
                font-weight: bold;
                font-size: 16px;
                border-radius: 15px;
                margin-bottom: 6px;
            }
            QPushButton:hover { background-color: #c8f7c5; }
        """
        self.is_processing = False
        self._pulse_phase = 0
        self._pulse_timer = QTimer(self)
        self._pulse_timer.setSingleShot(False)
        self._pulse_timer.timeout.connect(self._update_process_button_text)
        self._pulse_timer.start(750)
        self.process_button.setStyleSheet(self._original_process_btn_style)
        self.process_button.clicked.connect(self._on_process_clicked)
        self.process_button.setEnabled(False)
        self.cancel_button = ClickableButton("Cancel")
        self.cancel_button.setObjectName("cancelButton")
        self.cancel_button.setFixedSize(200, 70)
        self.cancel_button.setStyleSheet("""
            QPushButton {
                background-color: #c0392b;
                color: white;
                font-weight: bold;
                font-size: 16px;
                border-radius: 15px;
                margin-bottom: 6px;
            }
            QPushButton:hover { background-color: #e74c3c; }
        """)
        self.cancel_button.setVisible(False)
        self.cancel_button.clicked.connect(self.cancel_processing)
        process_button_layout = QHBoxLayout()
        process_button_layout.setContentsMargins(0, 0, 0, 0)
        process_button_layout.setSpacing(20)
        process_button_layout.addWidget(self.cancel_button)
        process_button_layout.addWidget(self.process_button)
        self.speed_spinbox = ClickableSpinBox()
        self.speed_spinbox.setDecimals(1)
        self.speed_spinbox.setSingleStep(0.1)
        self.speed_spinbox.setRange(0.5, 3.1)
        self.speed_spinbox.setValue(1.1)
        self.speed_spinbox.setMinimumWidth(0)
        self.speed_spinbox.setStyleSheet("font-size: 11px;")
        self.speed_spinbox.valueChanged.connect(self._on_speed_changed)
        self.speed_label = QLabel("Speed Multiplier")
        self.speed_label.setStyleSheet("font-size: 11px; font-weight: bold; margin-left: 10px; padding: 0; margin-right: 10px; padding: 0;")
        self.speed_spinbox.setAlignment(Qt.AlignCenter)
        speed_layout = QVBoxLayout(); speed_layout.setContentsMargins(0,0,0,0); speed_layout.setSpacing(2); speed_layout.setAlignment(Qt.AlignHCenter)
        speed_layout.addWidget(self.speed_label, alignment=Qt.AlignHCenter)
        speed_layout.addWidget(self.speed_spinbox, alignment=Qt.AlignHCenter)
        speed_widget = QWidget(); speed_widget.setLayout(speed_layout)
        speed_widget.setMinimumWidth(0)
        speed_widget.setSizePolicy(QSizePolicy.Minimum, QSizePolicy.Fixed)
        self.mobile_checkbox = QCheckBox("Mobile Format (Portrait)")
        self.mobile_checkbox.setStyleSheet("font-size: 14px; font-weight: bold; margin-left: 0px; padding: 0; margin-right: 0px; padding: 0;")
        self.mobile_checkbox.setChecked(bool(self.config_manager.config.get('mobile_checked', False)))
        
        from PyQt5.QtWidgets import QLineEdit
        self.portrait_text_input = QLineEdit()
        self.portrait_text_input.setPlaceholderText("Overlay Text (Hebrew/English)")
        self.portrait_text_input.setStyleSheet("QLineEdit { background-color: #4a667a; color: white; border: 1px solid #266b89; border-radius: 4px; padding: 4px; font-size: 14px; }")
        self.teammates_checkbox = QCheckBox("Show Teammates Healthbar")
        self.teammates_checkbox.setStyleSheet("font-size: 11px; margin-left: 15px; margin-right: 0px; padding: 0;")
        self.teammates_checkbox.setChecked(bool(self.config_manager.config.get('teammates_checked', False)))
        self.teammates_checkbox.toggled.connect(lambda c: self.logger.info("OPTION: Show Teammates Healthbar -> %s", c))
        self.no_fade_checkbox = QCheckBox("Disable Fade-In/Out", self)
        self.no_fade_checkbox.setChecked(False)
        _fm = QFontMetrics(self.no_fade_checkbox.font())
        _minw = _fm.horizontalAdvance("Disable Fade-In/Out") + 26
        self.no_fade_checkbox.setSizePolicy(QSizePolicy.Fixed, QSizePolicy.Fixed)
        self.no_fade_checkbox.toggled.connect(
            lambda c: self.logger.info("OPTION: Disable Fade-In/Out -> %s", c)
        )
        is_mob = self.mobile_checkbox.isChecked()
        self.teammates_checkbox.setVisible(is_mob)
        self.teammates_checkbox.setEnabled(is_mob)
        self.portrait_text_input.setVisible(is_mob)
        if not is_mob:
            self.teammates_checkbox.setChecked(False)
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
        vbox = QVBoxLayout(); vbox.setContentsMargins(0,0,0,0); vbox.setSpacing(2); vbox.setAlignment(Qt.AlignHCenter)
        vbox.addWidget(self.quality_label, alignment=Qt.AlignHCenter)
        vbox.addWidget(self.quality_slider, alignment=Qt.AlignHCenter)
        vbox.addWidget(self.quality_value_label, alignment=Qt.AlignHCenter)
        self.quality_container = QWidget(); self.quality_container.setLayout(vbox)
        self.quality_container.setObjectName("qualityContainer")
        self.tooltip_manager.add_tooltip(self.quality_container,
            "Bad = 15MB\n"
            "Okay = 25MB\n"
            "Standard = 45MB\n"
            "Good = 90MB\n"
            "Maximum = Original Video Size"
        )
        self.quality_container.setMinimumWidth(0)
        self.quality_container.setMaximumWidth(340)
        self.quality_container.setSizePolicy(QSizePolicy.Expanding, QSizePolicy.Fixed)
        right_group = QHBoxLayout()
        right_group.setContentsMargins(0, 0, 0, 0)
        right_group.addWidget(self.quality_container, 1)
        right_group.addWidget(speed_widget, 0)
        self.right_group_widget = QWidget(); self.right_group_widget.setLayout(right_group)
        self.right_group_widget.setSizePolicy(QSizePolicy.MinimumExpanding, QSizePolicy.Fixed)
        process_controls.setColumnStretch(0, 1)
        process_controls.setColumnStretch(1, 0)
        process_controls.setColumnStretch(2, 1)
        process_controls.setColumnMinimumWidth(0, 0)
        process_controls.setColumnMinimumWidth(2, 0)
        process_controls.addLayout(process_button_layout,     0, 1, 1, 1, Qt.AlignHCenter | Qt.AlignVCenter)
        self._safe_add_to_grid(process_controls, self.left_group_widget,  0, 0, 1, 1, Qt.AlignLeft  | Qt.AlignVCenter)
        self._safe_add_to_grid(process_controls, self.right_group_widget, 0, 2, 1, 1, Qt.AlignRight | Qt.AlignVCenter)
        self.center_btn_container = process_controls
        player_col.addLayout(process_controls)
        QTimer.singleShot(0, self._recenter_process_controls)
        self.mobile_checkbox.toggled.connect(self._on_mobile_toggled)
        self.mobile_checkbox.toggled.connect(lambda _: QTimer.singleShot(0, self._recenter_process_controls))
        self.quality_slider.valueChanged.connect(lambda _: self._recenter_process_controls())
        self.speed_spinbox.valueChanged.connect(lambda _: self._recenter_process_controls())
        player_col.addSpacing(10)
        self.progress_bar = QProgressBar()
        self.progress_bar.setObjectName("mainProgressBar")
        self.progress_bar.setStyleSheet("""
            QProgressBar {
                border: 1px solid #266b89;
                border-radius: 5px;
                text-align: center;
                height: 18px;
                background-color: #34495e;
                color: #ecf0f1;
            }
            QProgressBar::chunk {
                background-color: #2ecc71;
                border-radius: 4px;
            }
        """)
        self.progress_bar.setValue(0)
        player_col.addWidget(self.progress_bar)
        self.progress_update_signal.connect(self.on_progress)
        self.status_update_signal.connect(self.on_phase_update)
        self.process_finished_signal.connect(self.on_process_finished)
        self.selected_intro_abs_time = None
        self.thumb_pick_btn.setText("📸 Set Thumbnail 📸")

        def _mirror_overlay_progress(pct: int):
            try:
                if getattr(self, "_overlay_progress", None):
                    self._overlay_progress.setValue(int(max(0, min(100, pct))))
            except Exception:
                pass
        self.progress_update_signal.connect(_mirror_overlay_progress)
        self.right_panel = QWidget()
        right_col = QVBoxLayout(self.right_panel)
        self.right_panel.setSizePolicy(QSizePolicy.Fixed, QSizePolicy.Expanding)
        right_col.setContentsMargins(0, 0, 0, 0)
        drop_slider_row = QHBoxLayout()
        drop_slider_row.setContentsMargins(0, 0, 0, 0)
        drop_slider_row.setSpacing(40)
        drop_slider_row.setAlignment(Qt.AlignLeft | Qt.AlignTop)
        self.drop_area = DropAreaFrame()
        self.drop_area.setObjectName("dropArea")
        self.drop_area.setFocusPolicy(Qt.NoFocus)
        self.drop_area.file_dropped.connect(self.handle_file_selection)
        drop_layout = QVBoxLayout(self.drop_area)
        drop_layout.setContentsMargins(0, 0, 0, 0)
        self.drop_label = QLabel("Drag & Drop\r\na Video File Here:")
        self.drop_label.setStyleSheet("font-size: 11px; font-weight: bold; margin-top: 0px; margin-right: 0px; margin-bottom: 0px; margin-left: 0px; padding: 0px;")
        self.drop_label.setAlignment(Qt.AlignCenter)
        drop_layout.addWidget(self.drop_label)
        self.drop_area.setSizePolicy(QSizePolicy.Fixed, QSizePolicy.Fixed)
        self.drop_area.setFixedHeight(180)
        self.drop_area.setFixedWidth(140)
        self.drop_area.setStyleSheet("font-size: 11px; font-weight: bold; margin-top: 10px; margin-right: 0px; margin-bottom: 0px; margin-left: 0px; padding: 5px;")
        drop_slider_row.addWidget(self.drop_area)
        slider_vbox = QVBoxLayout()
        slider_vbox.setContentsMargins(0, 0, 0, 0)
        slider_vbox.setSpacing(0)
        slider_vbox.setAlignment(Qt.AlignTop | Qt.AlignLeft)
        self.slider_vbox_layout = slider_vbox 
        self.music_volume_slider = QSlider(Qt.Vertical, self)
        self.music_volume_slider.setObjectName("musicVolumeSlider")
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
        self.music_volume_slider.setFixedHeight(170)
        eff_default = 100
        raw = self.music_volume_slider.maximum() + self.music_volume_slider.minimum() - eff_default
        _knob = self.positionSlider.palette().highlight().color().name()
        self.music_volume_slider.setStyleSheet(f"""
        QSlider#musicVolumeSlider::groove:vertical {{
            border: 1px solid #1f2a36;
            background: qlineargradient(x1:0, y1:1, x2:0, y2:0,
                stop:0   #e64c4c,
                stop:0.25 #f7a8a8,
                stop:0.50 #f2f2f2,
                stop:0.75 #7bcf43,
                stop:1   #009b00);
            width: 20px;
            border-radius: 3px;
        }}
        QSlider#musicVolumeSlider::handle:vertical {{
            background: qlineargradient(x1:0, y1:0, x2:0, y2:1,
                stop:0 #455A64,
                stop:0.40 #455A64,
                stop:0.42 #90A4AE, stop:0.44 #90A4AE,
                stop:0.46 #455A64,
                stop:0.48 #455A64,
                stop:0.50 #90A4AE, stop:0.52 #90A4AE,
                stop:0.54 #455A64,
                stop:0.56 #455A64,
                stop:0.58 #90A4AE, stop:0.60 #90A4AE,
                stop:0.62 #455A64, stop:1 #455A64);
            border: 1px solid #1f2a36;
            width: 22px; 
            height: 40px; 
            margin: 0 -2px;
            border-radius: 4px;
        }}
        QSlider#musicVolumeSlider::handle:vertical:hover {{
            border: 1px solid #90A4AE;
            background: qlineargradient(x1:0, y1:0, x2:0, y2:1,
                stop:0 #546E7A,
                stop:0.40 #546E7A, stop:0.42 #CFD8DC, stop:0.44 #CFD8DC,
                stop:0.46 #546E7A, stop:0.48 #546E7A, stop:0.50 #CFD8DC, stop:0.52 #CFD8DC,
                stop:0.54 #546E7A, stop:0.56 #546E7A, stop:0.58 #CFD8DC, stop:0.60 #CFD8DC,
                stop:0.62 #546E7A, stop:1 #546E7A);
        }}
        QSlider#musicVolumeSlider::sub-page:vertical,
        QSlider#musicVolumeSlider::add-page:vertical {{ background: transparent;
        }}
        """)
        self.music_volume_label = QLabel("80%")
        self.music_volume_label.setAlignment(Qt.AlignHCenter)
        self.music_volume_label.setVisible(False)
        self.music_volume_label.setStyleSheet("font-size: 11px; font-weight: bold; margin-top: 20px; margin-right: 0px; margin-bottom: 10px; margin-left: 0px; padding: 0px;")
        init_raw = int(self.music_volume_slider.value())
        init_eff = self._music_eff(init_raw)
        self.music_volume_label.setText(f"{init_eff}%")
        self.music_volume_badge = QLabel("100%", self)
        self.music_volume_badge.setObjectName("musicVolumeBadge")
        self.music_volume_badge.setStyleSheet(
            "color: white; background: rgba(0,0,0,160); padding: 2px 6px; "
            "border-radius: 6px; font-weight: bold;"
        )
        self.music_volume_badge.hide()
        slider_vbox.addWidget(self.music_volume_slider)
        slider_vbox.addWidget(self.music_volume_label)
        drop_slider_row.addLayout(slider_vbox)
        right_col.addLayout(drop_slider_row)
        self.upload_button = QPushButton("📂 Upload Video File 📂")
        self.upload_button.clicked.connect(self.select_file)
        self.upload_button.setFixedHeight(55)
        self.upload_button.setStyleSheet("font-size: 11px; font-weight: bold; margin-top: 20px; margin-right: 0px; margin-bottom: 0px; margin-left: 0px; padding: 7px;")
        right_col.addWidget(self.upload_button)
        self.add_music_checkbox = QCheckBox("Add Background Music")
        self.add_music_checkbox.setToolTip("Toggle background MP3 mixing from the ./mp3 folder.")
        self.add_music_checkbox.setChecked(False)
        self.add_music_checkbox.setStyleSheet("font-size: 11px; font-weight: bold; margin-top: 20px; margin-right: 0px; margin-bottom: 20px; margin-left: 0px; padding: 0px;")
        right_col.addWidget(self.add_music_checkbox)

        def _update_ui_positions(checked):
            d_top = 0 if checked else 10
            self.drop_area.setStyleSheet(f"font-size: 11px; font-weight: bold; margin-top: {d_top}px; margin-right: 0px; margin-bottom: 0px; margin-left: 0px; padding: 5px;")
            s_top = 32 if checked else 32
            self.slider_vbox_layout.setContentsMargins(0, s_top, 0, 0)
        self.add_music_checkbox.toggled.connect(_update_ui_positions)
        self.music_combo = QComboBox()
        self.music_combo.setFixedWidth(250)
        self.music_combo.setVisible(False)
        right_col.addWidget(self.music_combo)
        self.music_offset_input = QDoubleSpinBox()
        self.music_offset_input.setPrefix("Music Start (s): ")
        self.music_offset_input.setDecimals(2)
        self.music_offset_input.setSingleStep(0.5)
        self.music_offset_input.setRange(0.0, 0.0)
        self.music_offset_input.setValue(0.0)
        self.music_offset_input.setVisible(False)
        right_col.addWidget(self.music_offset_input)
        right_col.addStretch(1)
        bottom_box = QWidget(self.right_panel)
        bottom_box.setSizePolicy(QSizePolicy.Maximum, QSizePolicy.Maximum)
        bb = QVBoxLayout(bottom_box)
        bb.setSpacing(25)
        self.adv_editor_btn = QPushButton("Advanced\n Video Editor")
        self.adv_editor_btn.setStyleSheet(
            "background-color: #bfa624; color: black; font-weight:600;"
            "padding:6px 12px; border-radius:6px;"
        )
        self.adv_editor_btn.setSizePolicy(QSizePolicy.Fixed, QSizePolicy.Fixed)
        self.adv_editor_btn.clicked.connect(self.launch_advanced_editor)
        bb.addWidget(self.adv_editor_btn, 0, Qt.AlignCenter)
        bb.addWidget(self.merge_btn, 0, Qt.AlignCenter)
        bb.addWidget(self.no_fade_checkbox, 0, Qt.AlignRight)
        right_col.addWidget(bottom_box, 0, Qt.AlignBottom | Qt.AlignRight)
        self.music_volume_slider.valueChanged.connect(self._on_music_volume_changed)
        self.music_volume_slider.valueChanged.connect(lambda _: self._update_music_badge())
        QTimer.singleShot(0, self._update_music_badge)
        self.add_music_checkbox.toggled.connect(self._on_add_music_toggled)
        self.music_combo.currentIndexChanged.connect(self._on_music_selected)
        self._populate_music_combo()
        self._top_row.addWidget(self.right_panel, stretch=1)
        main_layout.addLayout(left_layout, stretch=1)
        self.setLayout(main_layout)
        self._ensure_overlay_widgets()
        self._hide_processing_overlay()
        old_resize = getattr(self, "resizeEvent", None)
        _update_ui_positions(self.add_music_checkbox.isChecked())

        def _resized(e):
            if callable(old_resize):
                old_resize(e)
            try:
                self._resize_overlay()
            except Exception:
                pass
        self.resizeEvent = _resized
        QTimer.singleShot(0, self._adjust_trim_margins)
        QTimer.singleShot(0, self.apply_master_volume)

    def _on_mobile_toggled(self, checked: bool):
        self.logger.info("OPTION: Mobile Format -> %s", checked)
        self.teammates_checkbox.setVisible(checked)
        self.teammates_checkbox.setEnabled(checked)
        if hasattr(self, 'portrait_text_input'):
            self.portrait_text_input.setVisible(checked)
        if not checked:
            self.teammates_checkbox.setChecked(False)
        QTimer.singleShot(0, self._recenter_process_controls)
        self._update_portrait_mask_overlay_state()

    def _update_portrait_mask_overlay_state(self):
        if not hasattr(self, 'portrait_mask_overlay'):
            return
        res = getattr(self, 'original_resolution', "1920x1080")
        if not res:
            res = "1920x1080"
        if hasattr(self, "mobile_checkbox") and self.mobile_checkbox.isChecked():
            if self.video_surface.isVisible():
                top_left = self.video_surface.mapToGlobal(self.video_surface.rect().topLeft())
                self.portrait_mask_overlay.setGeometry(
                    top_left.x(), top_left.y(), 
                    self.video_surface.width(), self.video_surface.height()
                )
            self.portrait_mask_overlay.set_video_info(res, self.video_surface.size())
            self.portrait_mask_overlay.setVisible(True)
            self.portrait_mask_overlay.raise_()
            self.portrait_mask_overlay.update()
        else:
            self.portrait_mask_overlay.setVisible(False)