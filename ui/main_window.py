import faulthandler
import logging
import os
import signal
import subprocess
import sys
import time
import threading
import traceback
from logging.handlers import RotatingFileHandler
try:
    import mpv
except Exception:
    mpv = None

from PyQt5.QtCore import pyqtSignal, QTimer, QUrl, Qt, QCoreApplication, QEvent, QRect, QPoint
from PyQt5.QtGui import QKeySequence
from PyQt5.QtWidgets import (QMainWindow, QWidget, QStyle, QFileDialog, 
                             QMessageBox, QShortcut, QStatusBar, QLabel, QDialog)

from PyQt5.QtCore import QObject, QThread
import tempfile, glob
from system.config import ConfigManager
from system.logger import setup_logger
from system.state_transfer import StateTransfer
from processing.system_utils import kill_process_tree
from ui.widgets.tooltip_manager import ToolTipManager
from ui.widgets.custom_file_dialog import CustomFileDialog
from ui.parts.ui_builder_mixin import UiBuilderMixin
from ui.parts.phase_overlay_mixin import PhaseOverlayMixin
from ui.parts.player_mixin import PlayerMixin
from ui.parts.volume_mixin import VolumeMixin
from ui.parts.trim_mixin import TrimMixin
from ui.parts.music_mixin import MusicMixin
from ui.parts.ffmpeg_mixin import FfmpegMixin
from ui.parts.keyboard_mixin import KeyboardMixin
try:
    from developer_tools.utils import PersistentWindowMixin
except ImportError:
    import sys
    sys.path.append(os.path.join(os.path.dirname(__file__), '..', 'developer_tools'))

    from utils import PersistentWindowMixin

class _QtLiveLogHandler(logging.Handler):
    def __init__(self, ui_owner):
        super().__init__()
        self.ui = ui_owner

    def emit(self, record):
        if not QCoreApplication.instance() or getattr(self.ui, "_switching_app", False):
            return
        try:
            msg = self.format(record)
            self.ui.live_log_signal.emit(msg)
        except:
            pass

class VideoCompressorApp(QMainWindow, UiBuilderMixin, PhaseOverlayMixin, PlayerMixin, VolumeMixin, TrimMixin, MusicMixin, FfmpegMixin, KeyboardMixin, PersistentWindowMixin):
    progress_update_signal = pyqtSignal(int)
    status_update_signal = pyqtSignal(str)
    process_finished_signal = pyqtSignal(bool, str)
    live_log_signal = pyqtSignal(str)
    video_ended_signal = pyqtSignal()
    duration_changed_signal = pyqtSignal(int)

    def open_granular_speed_dialog(self):
        """Opens the Granular Speed Editor dialog."""
        try:
            if not self.input_file_path:
                 self.granular_checkbox.blockSignals(True)
                 self.granular_checkbox.setChecked(False)
                 self.granular_checkbox.blockSignals(False)
                 QMessageBox.warning(self, "No Video", "Please load a video first.")
                 return
            is_turning_off = not self.granular_checkbox.isChecked()
            if is_turning_off:
                if self.speed_segments:
                    reply = QMessageBox.question(self, "Disable Granular Speed", 
                        "This will clear your speed segments. Continue?", QMessageBox.Yes | QMessageBox.No)
                    if reply == QMessageBox.No:
                        self.granular_checkbox.blockSignals(True)
                        self.granular_checkbox.setChecked(True)
                        self.granular_checkbox.blockSignals(False)
                        return
                self.speed_segments = []
                self.status_bar.showMessage("Granular Speed disabled.", 3000)
                return
            else:
                pass
            self._opening_granular_dialog = True
            self._ignore_mpv_end_until = time.time() + 2.0
            current_ms = 0
            if self.player:
                is_paused = getattr(self.player, "pause", True)
                if not is_paused:
                    self.player.pause = True
                current_ms = max(0, int((getattr(self.player, 'time-pos', 0) or 0) * 1000))
            self.playPauseButton.setText("PLAY")
            self.playPauseButton.setIcon(self.style().standardIcon(QStyle.SP_MediaPlay))
            self.is_playing = False
            if self.timer.isActive():
                self.timer.stop()

            from ui.widgets.granular_speed_editor import GranularSpeedEditor
            current_base_speed = self.speed_spinbox.value()
            current_volume = self._vol_eff()
            dlg = GranularSpeedEditor(
                self.input_file_path, 
                self, 
                self.speed_segments, 
                base_speed=current_base_speed, 
                start_time_ms=current_ms,
                mpv_instance=self.player,
                volume=current_volume
            )
            result = dlg.exec_()
            self._opening_granular_dialog = False
            self._ignore_mpv_end_until = time.time() + 0.6
            self._bind_main_player_output()
            self.timer.start()
            if result == QDialog.Accepted:
                self.speed_segments = sorted(dlg.speed_segments, key=lambda x: x['start'])
                if self.speed_segments:
                    self.logger.info(f"Granular Speed: Received {len(self.speed_segments)} segments from editor (Sorted).")
                    for i, seg in enumerate(self.speed_segments):
                        self.logger.info(f"  Segment {i}: {seg['start']}-{seg['end']} @ {seg['speed']}x")
                    self.status_bar.showMessage(f"Granular Speed Active: {len(self.speed_segments)} segments", 5000)
                    self.granular_checkbox.blockSignals(True)
                    self.granular_checkbox.setChecked(True)
                    self.granular_checkbox.blockSignals(False)
                else:
                    self.logger.info("Granular Speed: Editor returned empty list. Disabling.")
                    self.granular_checkbox.blockSignals(True)
                    self.granular_checkbox.setChecked(False)
                    self.granular_checkbox.blockSignals(False) 
            else:
                should_be_checked = bool(self.speed_segments)
                self.granular_checkbox.blockSignals(True)
                self.granular_checkbox.setChecked(should_be_checked)
                self.granular_checkbox.blockSignals(False)
            resume_time = dlg.last_position_ms
            if resume_time < 0: resume_time = 0
            if getattr(self, "player", None):
                self.player.seek(resume_time / 1000.0, reference='absolute', precision='exact')
            self.positionSlider.setValue(int(resume_time))
            self.is_playing = False
            self.playPauseButton.setText("PLAY")
            self.playPauseButton.setIcon(self.style().standardIcon(QStyle.SP_MediaPlay))
        except Exception as e:
            self.logger.critical(f"CRITICAL: Error in Granular Speed Dialog: {e}\n{traceback.format_exc()}")
            QMessageBox.critical(self, "Error", f"An error occurred opening the editor:\n{e}")
            self.granular_checkbox.setChecked(False)
            self._opening_granular_dialog = False
            self._ignore_mpv_end_until = time.time() + 0.6

    def _bind_main_player_output(self):
        """Rebind MPV video output to main preview surface to avoid black/frozen preview."""
        try:
            if not getattr(self, "player", None) or not getattr(self, "video_surface", None):
                return
            wid = int(self.video_surface.winId())
            try:
                self.player.wid = wid
            except Exception:
                try:
                    self.player.command("set", "wid", wid)
                except Exception:
                    pass
        except Exception:
            pass

    def on_hardware_scan_finished(self, detected_mode: str):
        """Receives the result from the background hardware scan."""
        if not hasattr(self, 'status_bar'):
            return
        prev_mode = str(getattr(self, "hardware_strategy", "Scanning...") or "Scanning...")
        if getattr(self, "scan_complete", False) and prev_mode != "Scanning...":
            if prev_mode != "CPU" and str(detected_mode) == "CPU":
                self.logger.warning(
                    "Ignoring stale hardware callback: requested CPU after finalized %s",
                    prev_mode,
                )
                return
            if prev_mode == str(detected_mode):
                return
        self.hardware_strategy = detected_mode
        self.scan_complete = True
        self.logger.info(f"Hardware Strategy finalized: {self.hardware_strategy}")
        try:
            cfg = self.config_manager.config
            cfg["last_hardware_strategy"] = detected_mode
            self.config_manager.save_config(cfg)
        except Exception as e:
            self.logger.error(f"Failed to save hardware strategy to config: {e}")
        if hasattr(self, 'hardware_status_label'):
            mode = self.hardware_strategy
            badge_icon = "🚀" if mode in ["NVIDIA", "AMD", "INTEL"] else "⚠️"
            self.hardware_status_label.setText(f"{badge_icon} {mode} Mode")
            self.hardware_status_label.setStyleSheet("color: #43b581; font-weight: bold;" if mode != "CPU" else "color: #ffa500; font-weight: bold;")
            self.hardware_status_label.show()
        if self.hardware_strategy == "CPU":
            self.show_status_warning("⚠️ No compatible GPU detected. Running in slower CPU-only mode.")
        else:
            self.status_bar.showMessage(f"✅ Hardware Acceleration Enabled ({self.hardware_strategy})", 5000)
        self._maybe_enable_process()

    def show_status_warning(self, message: str):
        """Displays a temporary warning message in the status bar."""
        try:
            if not hasattr(self, 'status_bar_warning_label'):
                self.status_bar_warning_label = QLabel(message)
                self.status_bar_warning_label.setStyleSheet("color: #f39c12; font-weight: bold; padding-left: 10px;")
                self.status_bar.addPermanentWidget(self.status_bar_warning_label)
            self.status_bar_warning_label.setText(message)
            self.status_bar_warning_label.show()
            self.logger.warning(f"StatusBar NOTIFICATION: {message}")
            QTimer.singleShot(10000, self.status_bar_warning_label.hide)
        except Exception as e:
            self.logger.error(f"Failed to show status bar warning: {e}")

    def on_phase_update(self, phase: str) -> None:
        """
        Keeps the overlay, pulsing button text, and 'is_processing' flag in sync
        when the pipeline reports a phase/status change.
        """
        try:
            if hasattr(self, "_set_overlay_phase"):
                self._set_overlay_phase(phase)
            p = (phase or "").lower()
            if any(x in p for x in ("processing", "step", "encode", "intro", "core", "concat")):
                self.is_processing = True
                if hasattr(self, "_pulse_timer"):
                    self._pulse_timer.start(250)
            elif any(x in p for x in ("done", "idle", "error", "failed")):
                self.is_processing = False
                if hasattr(self, "_pulse_timer"):
                    self._pulse_timer.start(750)
            if hasattr(self, "_update_process_button_text"):
                self._update_process_button_text()
        except Exception:
            pass

    def _handle_video_end(self):
        """
        Slot to handle the video_ended_signal. Runs on the main Qt thread.
        Safely stops the player and resets the UI.
        """
        try:
            if bool(getattr(self, "_opening_granular_dialog", False)):
                return
            if time.time() < float(getattr(self, "_ignore_mpv_end_until", 0.0) or 0.0):
                return
            if bool(getattr(self, "_handling_video_end", False)):
                return
            self._handling_video_end = True
            if getattr(self, "player", None):
                try:
                    self.player.pause = True
                except Exception:
                    pass
            self.positionSlider.blockSignals(True)
            self.positionSlider.setValue(self.positionSlider.maximum())
            self.positionSlider.blockSignals(False)
            if getattr(self, "playPauseButton", None):
                self.playPauseButton.setText("PLAY")
                self.playPauseButton.setIcon(self.style().standardIcon(QStyle.SP_MediaPlay))
            self.is_playing = False
            self.wants_to_play = False
            self.timer.stop()
        except Exception as e:
            if hasattr(self, "logger"):
                self.logger.exception("End-of-media handler failed: %s", e)
        finally:
            self._handling_video_end = False

    def log_overlay_sink(self, line: str):
        """Thread-safe slot to receive log messages."""
        try:
            self._append_live_log(line)
        except Exception:
            pass

    def _sync_main_timeline_badges(self):
        """[FIX] Positions floating time badges above/below the main slider playhead."""
        try:
            slider = getattr(self, "positionSlider", None)
            top_b = getattr(self, "_main_time_badge_top", None)
            bot_b = getattr(self, "_main_time_badge_bottom", None)
            if not slider or not top_b or not bot_b:
                return
            is_active = (getattr(slider, "_hovering_handle", None) == 'playhead' or 
                         getattr(slider, "_dragging_handle", None) == 'playhead' or
                         slider.isSliderDown())
            if not is_active or not slider.isEnabled():
                top_b.hide()
                bot_b.hide()
                return
            cx = slider._map_value_to_pos(slider.value())
            badge_pos = slider.mapTo(self, QPoint(cx, 0))
            x_win = badge_pos.x()
            y_win = badge_pos.y()
            time_str = slider._fmt(slider.value())
            top_b.setText(time_str)
            bot_b.setText(time_str)
            top_b.adjustSize()
            bot_b.adjustSize()
            bw = top_b.width()
            bh = top_b.height()
            top_b.move(x_win - bw // 2, y_win - bh - 8)
            bot_b.move(x_win - bw // 2, y_win + slider.height() + 8)
            top_b.show()
            bot_b.show()
            top_b.raise_()
            bot_b.raise_()
        except Exception as e:
            self.logger.debug(f"DEBUG: Floating badge sync failed: {e}")

    def _on_speed_changed(self, value):
        self.playback_rate = value
        if self.player:
            is_paused = getattr(self.player, "pause", True)
            if not is_paused:
                self.player.pause = True
                self.playPauseButton.setText("PLAY")
                self.playPauseButton.setIcon(self.style().standardIcon(QStyle.SP_MediaPlay))
                self.is_playing = False
                if self.timer.isActive():
                    self.timer.stop()
        self.logger.info(f"Playback speed changed to {value}x. Player paused.")

    def __init__(self, file_path=None, hardware_strategy="CPU"):
        super().__init__()
        self._scrub_lock = threading.RLock()
        self.script_dir = os.path.dirname(sys.executable) if getattr(sys, 'frozen', False) else os.path.dirname(os.path.abspath(__file__))
        self.base_dir = os.path.abspath(os.path.join(self.script_dir, os.pardir))
        self.bin_dir = os.path.join(self.base_dir, 'binaries')
        self.logger = setup_logger(self.base_dir, "main_app.log", "Main_App")
        self.config_manager = ConfigManager(os.path.join(self.base_dir, 'config', 'main_app', 'main_app.conf'))
        self.status_bar = QStatusBar()
        self.setStatusBar(self.status_bar)
        self.central_widget = QWidget()
        self.setCentralWidget(self.central_widget)
        self.tooltip_manager = ToolTipManager(self)
        self._resize_timer = QTimer(self)
        self._resize_timer.setSingleShot(True)
        self._resize_timer.setInterval(50)
        self._resize_timer.timeout.connect(self._delayed_resize_event)
        self.init_ui()
        self.playback_rate = 1.1
        self.speed_segments = []
        self.hardware_strategy = hardware_strategy
        try:
            self._video_volume_pct = int(self.config_manager.config.get('video_mix_volume', 100))
            self._music_volume_pct = int(self.config_manager.config.get('music_mix_volume', 80))
        except:
            self._video_volume_pct = 100
            self._music_volume_pct = 80
        self.last_dir = self.config_manager.config.get('last_directory', os.path.expanduser('~'))
        self.trim_start_ms = 0
        self.trim_end_ms = 0
        self.trim_start = 0.0
        self.trim_end = 0.0
        self.music_timeline_start_ms = 0
        self.music_timeline_end_ms = 0
        self.input_file_path = None
        self.original_duration_ms = 0
        self.original_resolution = ""
        self.is_playing = False
        self.is_processing = False
        self.wants_to_play = False
        self._is_seeking_from_end = False
        self._suspend_volume_sync = True
        self._opening_granular_dialog = False
        self._ignore_mpv_end_until = 0.0
        self._handling_video_end = False
        self._last_mpv_end_emit = 0.0
        self.volume_shortcut_target = 'main'
        self._phase_is_processing = False
        self._phase_dots = 1
        self._base_title = "Fortnite Video Compressor"
        self._music_files = []
        self.scan_complete = False
        self.set_style()
        self.setWindowTitle(self._base_title)
        try:
            StateTransfer.clear_state()
        except Exception as state_err:
            self.logger.debug("Could not clear startup session state: %s", state_err)
        if self.hardware_strategy == "Scanning...":
            if hasattr(self, 'hardware_status_label'):
                self.hardware_status_label.setText("🔎 Scanning for compatible hardware...")
            else:
                self.status_bar.showMessage("🔎 Scanning for compatible hardware...")
        elif self.hardware_strategy == "CPU":
            self.show_status_warning("⚠️ No compatible GPU detected. Running in slower CPU-only mode.")
            self.scan_complete = True
        else:
            self.status_bar.showMessage("Ready.", 5000)
            self.scan_complete = True
        self.live_log_signal.connect(self.log_overlay_sink)
        self.video_ended_signal.connect(self._handle_video_end)
        self.duration_changed_signal.connect(self._safe_handle_duration_changed)
        try:
            self.logger.handlers = [h for h in self.logger.handlers if not isinstance(h, _QtLiveLogHandler)]
            qt_handler = _QtLiveLogHandler(self)
            qt_handler.setLevel(logging.INFO)
            self.logger.addHandler(qt_handler)
        except Exception:
            pass
        self.logger.info("=== Application started ===")
        self.logger.info(f"Initialized with Hardware Strategy: {self.hardware_strategy}")
        self.timer = QTimer(self)
        self.timer.setInterval(40)
        self.timer.timeout.connect(self.update_player_state)
        self.setup_persistence(
            config_path=os.path.join(self.base_dir, 'config', 'main_app', 'main_app.conf'),
            settings_key='window_geometry',
            default_geo={'x': 0, 'y': 0, 'w': 1300, 'h': 750},
            title_info_provider=lambda: f"{self._base_title}  —  {self.width()}x{self.height()}",
            config_manager=self.config_manager
        )
        self.setMinimumSize(1000, 600)
        self._scan_mp3_folder()
        self._update_window_size_in_title()
        self.installEventFilter(self)

        def _seek_shortcut(offset_ms):
            if getattr(self, "input_file_path", None):
                self.seek_relative_time(offset_ms)
        QShortcut(QKeySequence(Qt.Key_Left), self, lambda: _seek_shortcut(-250))
        QShortcut(QKeySequence(Qt.Key_Right), self, lambda: _seek_shortcut(250))
        QShortcut(QKeySequence(Qt.CTRL | Qt.Key_Left), self, lambda: _seek_shortcut(-5))
        QShortcut(QKeySequence(Qt.CTRL | Qt.Key_Right), self, lambda: _seek_shortcut(5))
        self._init_upload_hint_blink()
        if not file_path:
            self._set_upload_hint_active(True)
        else:
            self._set_upload_hint_active(False)
        if file_path:
            self.handle_file_selection(file_path)
        QTimer.singleShot(10, self._setup_mpv)
    @property
    def original_duration(self):
        """Return original duration in seconds (float)."""
        return self.original_duration_ms / 1000.0 if self.original_duration_ms else 0.0

    def _setup_mpv(self):
        """Initializes the MPV instance and player."""
        log_dir = os.path.join(self.base_dir, "logs")
        os.makedirs(log_dir, exist_ok=True)
        self.player = None
        if mpv:
            try:
                wid = int(self.video_surface.winId())
                self.logger.info(f"UI: Initializing MPV with window ID {wid}")
                mpv_kwargs = {
                    'wid': wid,
                    'osc': False,
                    'hr_seek': 'yes',
                    'hwdec': 'auto',
                    'keep_open': 'yes',
                    'ytdl': False,
                    'demuxer_max_bytes': '500M',
                    'demuxer_max_back_bytes': '100M'
                }
                if sys.platform == 'win32':
                    mpv_kwargs['vo'] = 'gpu'
                    mpv_kwargs['gpu-context'] = 'd3d11'
                    os.environ["LC_NUMERIC"] = "C" 
                self.player = mpv.MPV(**mpv_kwargs)
                if self.player:
                    self.player.loglevel = 'info'
                    self.player.volume = 100
                    try:
                        self.player.speed = float(getattr(self, "playback_rate", 1.1) or 1.1)
                    except Exception:
                        pass
                    self._bind_main_player_output()
                    try:
                        @self.player.event_callback('end-file')
                        def handle_end_file(_event):
                            QTimer.singleShot(0, self._on_mpv_end_reached)
                        self._mpv_end_file_cb = handle_end_file
                    except Exception as cb_err:
                        self.logger.warning(f"MPV end-file callback registration failed: {cb_err}")
                        try:
                            @self.player.property_observer('eof-reached')
                            def handle_eof_prop(_name, value):
                                if bool(value):
                                    QTimer.singleShot(0, self._on_mpv_end_reached)
                            self._mpv_eof_prop_cb = handle_eof_prop
                        except Exception as obs_err:
                            self.logger.warning(f"MPV eof-reached property observer registration failed: {obs_err}")
            except Exception as e:
                self.logger.error(f"CRITICAL: MPV Failed to initialize. Error: {e}")
                self.player = None
        if self.player:
            try:
                self._suspend_volume_sync = True

                def _enable_volume_sync_after_bootstrap():
                    if not getattr(self, "player", None):
                        return
                    self._suspend_volume_sync = False
                QTimer.singleShot(600, _enable_volume_sync_after_bootstrap)
            except Exception:
                pass
    
    def _safe_handle_duration_changed(self, duration_ms: int):
        """Slot to safely update UI with duration. (Main thread)"""
        try:
            self.positionSlider.setRange(0, duration_ms)
            self.positionSlider.set_duration_ms(duration_ms)
            self.logger.info(f"UI: Duration updated to {duration_ms}ms via signal.")
        except Exception as e:
            self.logger.error("Error updating UI duration: %s", e)

    def keyPressEvent(self, event):
        """Handle key presses for shortcuts."""
        if event.key() == Qt.Key_F11:
            self.launch_advanced_editor()
        elif event.key() == Qt.Key_F12:
            self.launch_crop_tool()
        else:
            super().keyPressEvent(event)

    def mousePressEvent(self, event):
        """Restore focus to the main window so keyboard shortcuts remain reliable."""
        try:
            if event.button() == Qt.LeftButton:
                self.setFocus(Qt.MouseFocusReason)
        except Exception as e:
            self.logger.error("MousePress error: %s", e)
        super().mousePressEvent(event)

    def eventFilter(self, obj, event):
        """
        Centralized event filter.
        - Keeps keyboard shortcut handling active.
        - Handles resize/move overlay maintenance without duplicate wiring.
        """
        try:
            if event.type() == QEvent.KeyPress:
                if KeyboardMixin.eventFilter(self, obj, event):
                    return True
        except Exception as e:
            self.logger.error("Keyboard eventFilter error: %s", e)
        if obj in (self, getattr(self, "video_frame", None), getattr(self, "video_surface", None)):
            if event.type() in (QEvent.Resize, QEvent.Move):
                try:
                    if hasattr(self, '_update_upload_hint_responsive'):
                        self._update_upload_hint_responsive()
                    self._update_volume_badge()
                    if hasattr(self, "portrait_mask_overlay") and self.portrait_mask_overlay and hasattr(self, "video_surface"):
                        r = self.video_surface.rect()
                        top_left = self.video_surface.mapToGlobal(r.topLeft())
                        self.portrait_mask_overlay.setGeometry(QRect(top_left, r.size()))
                        self._update_portrait_mask_overlay_state()
                except Exception as e:
                    self.logger.error("EventFilter resize/move error: %s", e)
                return False
        return super().eventFilter(obj, event)

    def launch_crop_tool(self):
        """Launches the crop tool application and closes the main app."""
        self.logger.info("TRIGGER: launch_crop_tool called.")
        try:
            self.setEnabled(False)
            if hasattr(self, 'statusBar'):
                self.statusBar().showMessage("🚀 Switching to Crop Tool...", 5000)
            QCoreApplication.processEvents()
            root_dir = os.path.abspath(self.base_dir)
            dev_tools_dir = os.path.join(root_dir, 'developer_tools')
            script_path = os.path.join(dev_tools_dir, 'crop_tools.py')
            if not os.path.exists(script_path):
                raise FileNotFoundError(f"Crop Tool script not found at: {script_path}")
            state = {
                "input_file": self.input_file_path,
                "trim_start": self.trim_start_ms,
                "trim_end": self.trim_end_ms,
                "speed_segments": self.speed_segments,
                "hardware_mode": getattr(self, "hardware_strategy", "CPU"),
                "resolution": getattr(self, "original_resolution", None)
            }
            StateTransfer.save_state(state)
            if self.player:
                self.player.stop()
            env = os.environ.copy()
            norm_root = os.path.normpath(root_dir)
            norm_dev = os.path.normpath(dev_tools_dir)
            current_pythonpath = env.get("PYTHONPATH", "")
            env["PYTHONPATH"] = os.pathsep.join(filter(None, [
                norm_dev,
                norm_root,
                current_pythonpath
            ]))
            cmd = [sys.executable, "-B", script_path]
            if self.input_file_path:
                cmd.append(self.input_file_path)
            self.logger.info(f"ACTION: Launching detached Crop Tool: {' '.join(cmd)}")
            creation_flags = 0
            if sys.platform == "win32":
                creation_flags = 0x00000008 | 0x00000200
            subprocess.Popen(
                cmd, 
                cwd=norm_dev, 
                env=env,
                creationflags=creation_flags,
                close_fds=True,
                shell=False
            )
            self._switching_app = True
            self.logger.info("Crop Tool launched successfully. Closing parent.")
            self.close()
        except Exception as e:
            self.setEnabled(True)
            self.logger.critical(f"ERROR: Failed to launch Crop Tool. Error: {e}\n{traceback.format_exc()}")
            QMessageBox.critical(self, "Launch Failed", f"Could not launch Crop Tool.\n\nError: {e}")

    def launch_advanced_editor(self):
        """Launches the advanced video editor application."""
        try:
            state = {
                "input_file": self.input_file_path,
                "hardware_mode": getattr(self, "hardware_strategy", "CPU")
            }
            StateTransfer.save_state(state)
            if getattr(self, "input_file_path", None) or self.speed_segments:
                 pass
            self.logger.info("ACTION: Launching Advanced Video Editor via F11...")
            command = [sys.executable, os.path.join(self.base_dir, 'advanced', 'advanced_video_editor.py')]
            if self.input_file_path:
                command.append(self.input_file_path)
            if self.player:
                self.player.stop()
            subprocess.Popen(command, cwd=self.base_dir)
            self.logger.info("Advanced Editor process started. Closing main app.")
            self.close()
        except Exception as e:
            self.logger.critical(f"ERROR: Failed to launch Advanced Editor. Error: {e}")
            QMessageBox.critical(self, "Launch Failed", f"Could not launch Advanced Editor. Error: {e}")

    def _update_window_size_in_title(self):
        self.setWindowTitle(f"{self._base_title}  —  {self.width()}x{self.height()}")

    def _adjust_trim_margins(self):
        """Center the rows under the video by adjusting left/right margins."""
        try:
            player_container = getattr(self, 'player_col_container', None)
            trim_container   = getattr(self, 'trim_container', None)
            center_container = getattr(self, 'center_btn_container', None)
            if player_container is not None and self.video_frame is not None:
                pw = player_container.width()
                vw = self.video_frame.width()
                pad = max(0, (pw - vw) // 2)
                if trim_container is not None:
                    trim_container.setContentsMargins(pad, 0, pad, 0)
                if center_container is not None:
                    center_container.setContentsMargins(pad, 0, pad, 0)
        except Exception:
            pass

    def showEvent(self, e):
        super().showEvent(e)
        try:
            self._bind_main_player_output()
            self._layout_volume_slider()
            self._update_volume_badge()
        except Exception:
            pass

    def _on_slider_trim_changed(self, start_ms, end_ms):
        """[FIX #4] Handles trim time changes with 'Magnetic' music following and strict clamping."""
        old_start = self.trim_start_ms
        self.trim_start_ms = start_ms
        self.trim_end_ms = end_ms
        if hasattr(self, "_wizard_tracks") and self._wizard_tracks:
            delta_start = start_ms - old_start
            music_dur = self.music_timeline_end_ms - self.music_timeline_start_ms
            new_m_start = self.music_timeline_start_ms + delta_start
            new_m_end = new_m_start + music_dur
            if new_m_start < start_ms:
                new_m_start = start_ms
                new_m_end = new_m_start + music_dur
            if new_m_end > end_ms:
                new_m_end = end_ms
                new_m_start = max(start_ms, new_m_end - music_dur)
            if new_m_start != self.music_timeline_start_ms or new_m_end != self.music_timeline_end_ms:
                self.music_timeline_start_ms = new_m_start
                self.music_timeline_end_ms = new_m_end
                self.positionSlider.set_music_times(new_m_start, new_m_end)
                self.logger.info(f"MUSIC: Robustly shifted to {new_m_start}-{new_m_end}ms following video trim.")
        self._update_trim_widgets_from_trim_times()

    def set_style(self):
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
            QFrame#dropArea {
                border: 3px dashed #266b89;
                border-radius: 10px;
                background-color: #34495e;
                padding: 20px;
            }
            QSpinBox, QDoubleSpinBox {
                background-color: #4a667a;
                border: 1px solid #266b89;
                border-radius: 5px;
                padding: 10px;
                color: #ecf0f1;
                font-size: 13px;
            }
            QPushButton {
                background-color: #266b89;
                color: #ffffff;
                border-style: solid;
                border-top: 1px solid rgba(255, 255, 255, 0.2);
                border-left: 1px solid rgba(255, 255, 255, 0.2);
                border-bottom: 1px solid rgba(0, 0, 0, 0.6);
                border-right: 1px solid rgba(0, 0, 0, 0.6);
                padding: 10px 18px;
                border-radius: 8px;
                font-weight: bold;
            }
            QPushButton:hover:!disabled {
                border: 2px solid #7DD3FC;
            }
            QPushButton:pressed:!disabled {
                border-top: 1px solid rgba(0, 0, 0, 0.7);
                border-left: 1px solid rgba(0, 0, 0, 0.7);
                border-bottom: 1px solid rgba(255, 255, 255, 0.1);
                border-right: 1px solid rgba(255, 255, 255, 0.1);
                padding-top: 11px;
                padding-left: 19px;
                padding-bottom: 9px;
                padding-right: 17px;
            }
            QPushButton#DoneButton { background-color: #e74c3c; }
            QProgressBar { border: 1px solid #266b89; border-radius: 5px; text-align: center; height: 18px; }
            QProgressBar::chunk { background-color: #2ecc71; }
            QToolTip {
                font-family: Arial;
                font-size: 13pt;
                font-weight: normal;
                border: 1px solid #ecf0f1;
                background-color: #34495e;
                color: #ecf0f1;
                padding: 5px;
            }
        ''')

    def refresh_ui_styles(self):
        """
        Nuclear option: Forces a deep style refresh on the window and all children.
        This fixes buttons losing color after dialogs close.
        """
        try:
            self.set_style()
            self.style().unpolish(self)
            self.style().polish(self)
            for widget in self.findChildren(QWidget):
                widget.style().unpolish(widget)
                widget.style().polish(widget)
            self.update()
        except Exception:
            pass

    def _init_upload_hint_blink(self):
        """[FIX #30] Hardware-accelerated smooth breath-like glow animation."""
        if not hasattr(self, 'hint_group_container'):
            return
            
        from PyQt5.QtWidgets import QGraphicsOpacityEffect
        from PyQt5.QtCore import QPropertyAnimation, QSequentialAnimationGroup, QEasingCurve
        self._hint_opacity_effect = QGraphicsOpacityEffect(self.hint_group_container)
        self.hint_group_container.setGraphicsEffect(self._hint_opacity_effect)
        anim_in = QPropertyAnimation(self._hint_opacity_effect, b"opacity")
        anim_in.setDuration(1200)
        anim_in.setStartValue(0.15)
        anim_in.setEndValue(1.0)
        anim_in.setEasingCurve(QEasingCurve.InOutSine)
        anim_out = QPropertyAnimation(self._hint_opacity_effect, b"opacity")
        anim_out.setDuration(1200)
        anim_out.setStartValue(1.0)
        anim_out.setEndValue(0.15)
        anim_out.setEasingCurve(QEasingCurve.InOutSine)
        self._hint_group = QSequentialAnimationGroup(self)
        self._hint_group.addAnimation(anim_in)
        self._hint_group.addAnimation(anim_out)
        self._hint_group.setLoopCount(-1)

    def _set_upload_hint_active(self, active):
        """[FIX #12 & #30] Starts or stops the upload hint fading animation."""
        target = getattr(self, 'hint_overlay_widget', None)
        if not target or not hasattr(self, '_hint_group'):
            return
        if active:
            self._update_upload_hint_responsive()
            target.show()
            target.raise_()
            self._hint_group.start()
        else:
            self._hint_group.stop()
            target.hide()

    def _update_upload_hint_responsive(self):
        """
        [FIX #12] Proportional scaling for the Upload Hint overlay.
        Calculates relative sizes based on current window height and width to prevent overlap.
        """
        if not hasattr(self, 'upload_hint_container'):
            return
            
        from PyQt5.QtGui import QPainter, QPixmap, QColor, QPolygon
        from PyQt5.QtCore import Qt, QPoint
        curr_w = self.width()
        curr_h = self.height()
        ref_h = 750.0
        scale = max(0.5, min(1.5, curr_h / ref_h))
        box_w = int(580 * scale)
        box_h = int(100 * scale)
        font_size = int(24 * scale)
        self.upload_hint_container.setFixedSize(box_w, box_h)
        self.upload_hint_container.setStyleSheet(f"""
            #uploadHintContainer {{
                background-color: #000000;
                border: {max(2, int(3*scale))}px solid #7DD3FC;
                border-radius: {int(12*scale)}px;
            }}
        """)
        self.upload_hint_label.setStyleSheet(f"""
            color: #7DD3FC;
            font-family: Arial;
            font-size: {font_size}px;
            font-weight: bold;
            background: transparent;
            border: none;
        """)
        gap = int(15 * scale)
        self.hint_group_layout.setSpacing(gap)
        offset_x = int(150 * scale)
        arrow_l_base = int(350 * scale)
        right_safety = int(60 * scale)
        available_space = curr_w - offset_x - box_w - gap - right_safety
        if curr_w < 1277:
            width_ratio = max(0.1, (curr_w - offset_x - box_w - gap - right_safety) / (1277.0 - offset_x - box_w - gap - right_safety))
            arrow_l = int(arrow_l_base * width_ratio)
        else:
            arrow_l = arrow_l_base
        arrow_l = max(30, min(arrow_l, available_space))
        if (offset_x + box_w + gap + arrow_l + right_safety) > curr_w:
             arrow_l = max(10, curr_w - offset_x - box_w - gap - right_safety)
        arrow_s = int(35 * scale)
        c_w, c_h = arrow_l + 20, arrow_s + 40
        self.upload_hint_arrow.setFixedSize(c_w, c_h)
        pix = QPixmap(c_w, c_h)
        pix.fill(Qt.transparent)
        p = QPainter(pix)
        p.setRenderHint(QPainter.Antialiasing)
        p.setBrush(QColor("#7DD3FC"))
        p.setPen(Qt.NoPen)
        center_y_arrow = c_h // 2
        body_h = int(16 * scale)
        head_w = int(min(45 * scale, arrow_l * 0.4))
        p.drawRect(5, center_y_arrow - (body_h // 2), arrow_l - head_w, body_h)
        tip_x = 5 + arrow_l
        base_x = tip_x - head_w
        points = [
            QPoint(base_x, center_y_arrow - (arrow_s // 2)),
            QPoint(tip_x, center_y_arrow),
            QPoint(base_x, center_y_arrow + (arrow_s // 2))
        ]
        p.drawPolygon(QPolygon(points))
        p.end()
        self.upload_hint_arrow.setPixmap(pix)
        try:
            btn_pos = self.upload_button.mapToGlobal(self.upload_button.rect().center())
            local_pos = self.hint_overlay_widget.mapFromGlobal(btn_pos)
            target_y = local_pos.y() - (max(box_h, c_h) // 2)
            self.hint_centering_layout.setContentsMargins(offset_x, target_y, 0, 0)
        except Exception:
            pass

    def select_file(self):
        self._set_upload_hint_active(False)
        had_existing_media = bool(getattr(self, "input_file_path", None))
        was_playing_before_dialog = False
        try:
            if getattr(self, "player", None):
                was_playing_before_dialog = not bool(getattr(self.player, "pause", True))
                self.player.pause = True
            if getattr(self, "timer", None) and self.timer.isActive():
                self.timer.stop()
        except Exception as e:
            try:
                self.logger.error("FILE: failed to pause before dialog: %s", e)
            except Exception:
                pass
        dialog = CustomFileDialog(
            None, 
            "Select Video File(s)",
            self.last_dir,
            "Video Files (*.mp4 *.mkv *.mov *.avi)",
            config=self.config_manager,
        )
        dialog.setWindowModality(Qt.ApplicationModal)
        file_paths = []
        if hasattr(self, "portrait_mask_overlay") and self.portrait_mask_overlay:
            self.portrait_mask_overlay.hide()
        if dialog.exec_():
            file_paths = dialog.selectedFiles()
        if hasattr(self, "_update_portrait_mask_overlay_state"):
            self._update_portrait_mask_overlay_state()
        self.refresh_ui_styles()
        if file_paths:
            file_to_load = file_paths[0]
            if len(file_paths) > 1:
                self.logger.warning(
                    f"User selected {len(file_paths)} files. "
                    f"Loading only the first one as per design: {os.path.basename(file_to_load)}"
                )
                QMessageBox.information(
                    self,
                    "Multiple Files Selected",
                    f"You selected {len(file_paths)} files. Only the first file, '{os.path.basename(file_to_load)}', will be loaded."
                )
            self.logger.info("FILE: selected via dialog: %s", file_to_load)
            self._set_upload_hint_active(False)
            self.handle_file_selection(file_to_load)
        else:
            self.logger.info("FILE: dialog canceled")
            if had_existing_media:
                try:
                    if getattr(self, "player", None):
                        self.player.pause = not was_playing_before_dialog
                    if was_playing_before_dialog:
                        self.is_playing = True
                        self.wants_to_play = True
                        if hasattr(self, "playPauseButton"):
                            self.playPauseButton.setText("PAUSE")
                            self.playPauseButton.setIcon(self.style().standardIcon(QStyle.SP_MediaPause))
                        if hasattr(self, "timer") and not self.timer.isActive():
                            self.timer.start(40)
                    else:
                        self.is_playing = False
                        self.wants_to_play = False
                        if hasattr(self, "playPauseButton"):
                            self.playPauseButton.setText("PLAY")
                            self.playPauseButton.setIcon(self.style().standardIcon(QStyle.SP_MediaPlay))
                except Exception as restore_err:
                    self.logger.debug("FILE: failed restoring playback state after cancel: %s", restore_err)
            if not getattr(self, 'input_file_path', None):
                self._set_upload_hint_active(True)

    def handle_file_selection(self, file_path):
        """Loads a file, starts playback, and initiates duration polling."""
        try:
            if self.player:
                is_paused = getattr(self.player, "pause", True)
                if not is_paused:
                    self.player.stop()
            timer = getattr(self, "timer", None)
            if timer and timer.isActive():
                timer.stop()
        except Exception as stop_err:
            self.logger.error("Error stopping existing player: %s", stop_err)
        try:
            self.reset_app_state()
        except Exception as reset_err:
            self.logger.error("Error during UI reset: %s", reset_err)
        self.logger.info("FILE: loading for playback: %s", file_path)
        self.input_file_path = file_path
        self._set_upload_hint_active(False)
        self.drop_label.setWordWrap(True)
        self.drop_label.setText(os.path.basename(self.input_file_path))
        dir_path = os.path.dirname(file_path)
        if os.path.isdir(dir_path):
            self.last_dir = dir_path
        p = os.path.abspath(str(file_path))
        if not os.path.isfile(p):
            self.logger.error("Selected file not found: %s", p)
            return
        if self.player:
            try:
                self._bind_main_player_output()
                self.player.command("loadfile", p, "replace")
                try:
                    current_rate = float(self.speed_spinbox.value()) if hasattr(self, "speed_spinbox") else float(getattr(self, "playback_rate", 1.1) or 1.1)
                    self.playback_rate = current_rate
                    self.player.speed = current_rate
                except Exception as rate_err:
                    self.logger.debug(f"FILE: speed apply skipped: {rate_err}")
                self.player.pause = False

                def _poll_dur():
                    if not self.player: return
                    dur = getattr(self.player, 'duration', 0)
                    if dur and dur > 0:
                        self.duration_changed_signal.emit(int(dur * 1000))
                    else:
                        QTimer.singleShot(500, _poll_dur)
                QTimer.singleShot(500, _poll_dur)
                if hasattr(self, "apply_master_volume"):
                    self._suspend_volume_sync = False
                    self.apply_master_volume()
                    QTimer.singleShot(400, self.apply_master_volume)
                if hasattr(self, "_on_mobile_toggled"):
                    QTimer.singleShot(150, lambda: self._on_mobile_toggled(self.mobile_checkbox.isChecked()))
            except Exception as e:
                self.logger.error("Failed to play media with MPV: %s", e)
        else:
            self.logger.warning("MPV not available. Skipping playback. (CPU Mode)")
            pass
        self.get_video_info()
        self._update_portrait_mask_overlay_state()
        self._set_video_controls_enabled(True)

    def reset_app_state(self):
        """Resets the UI and state so a new file can be loaded fresh."""
        self.input_file_path = None
        self.original_resolution = None
        if hasattr(self, 'set_resolution_text'):
            self.set_resolution_text("")
        elif hasattr(self, 'resolution_label'):
            self.resolution_label.setText("")
        self.original_duration_ms = 0
        self.trim_start_ms = 0
        self.trim_end_ms = 0
        self.speed_segments = []
        if hasattr(self, 'granular_checkbox'):
            self.granular_checkbox.blockSignals(True)
            self.granular_checkbox.setChecked(False)
            self.granular_checkbox.blockSignals(False)
        self.process_button.setEnabled(False)
        self._set_video_controls_enabled(False)
        self.progress_update_signal.emit(0)
        self.on_phase_update("Please upload a new video file.")
        try:
            self.positionSlider.setRange(0, 0)
            self.positionSlider.setValue(0)
            self.positionSlider.set_duration_ms(0)
            self.positionSlider.set_trim_times(0, 0)
            self.positionSlider.reset_music_times()
        except AttributeError:
            pass
        try:
            self._reset_music_player()
        except Exception:
            pass
        self.drop_label.setText("Drag & Drop\r\nVideo File Here:")
        if hasattr(self, 'portrait_mask_overlay') and self.portrait_mask_overlay:
            self.portrait_mask_overlay.hide()
        self._update_portrait_mask_overlay_state()

    def handle_new_file(self):
        """Clear state and immediately open file picker."""
        self.reset_app_state()
        self.select_file()

    def _save_app_state_and_config(self):
        """Encapsulates all logic for saving application state to disk."""
        cfg = self.config_manager.config
        try:
            cfg['mobile_checked'] = bool(self.mobile_checkbox.isChecked())
        except Exception:
            pass
        try:
            cfg['teammates_checked'] = bool(self.teammates_checkbox.isChecked())
        except Exception:
            pass
        try:
            cfg['last_directory'] = self.last_dir
        except Exception:
            pass
        self.config_manager.save_config(cfg)
        self.logger.info("CONFIG: Saved current state to disk.")

    def cleanup_and_exit(self):
        """Centralized cleanup and exit logic."""
        self.logger.info("=== Application shutting down ===")
        self.blockSignals(True)
        if hasattr(self, "timer") and self.timer.isActive():
            self.timer.stop()
        if getattr(self, "is_processing", False) and hasattr(self, "process_thread"):
            self.logger.warning("App closing during process. Killing ffmpeg...")
            self.process_thread.cancel()
            if self.process_thread.isRunning():
                self.process_thread.wait(3000)
        try:
            if getattr(self, "player", None):
                self.player.terminate()
                self.player = None
        except Exception as e:
            self.logger.error("Failed to safely stop MPV on close: %s", e)

        from system.utils import ProcessManager
        ProcessManager.cleanup_temp_files()
        self._save_app_state_and_config()
        QCoreApplication.instance().quit()

    def resizeEvent(self, event):
        """[FIX #1 & #24] Handles window resizing with throttling for overlay smoothness."""
        if hasattr(self, 'handle_persistence_event'):
            self.handle_persistence_event()
        if hasattr(self, '_resize_timer'):
            self._resize_timer.start()
        else:
            self._delayed_resize_event()
        super().resizeEvent(event)

    def moveEvent(self, event):
        """[FIX] Track window movements for persistence."""
        if hasattr(self, 'handle_persistence_event'):
            self.handle_persistence_event()
        super().moveEvent(event)

    def _delayed_resize_event(self):
        """Executed after resize throttle."""
        try:
            if hasattr(self, "_update_volume_badge"):
                self._update_volume_badge()
            if hasattr(self, "_resize_overlay"):
                self._resize_overlay()
            if hasattr(self, "_adjust_trim_margins"):
                self._adjust_trim_margins()
            if hasattr(self, "_update_portrait_mask_overlay_state"):
                self._update_portrait_mask_overlay_state()
        except Exception:
            pass

    def closeEvent(self, event):
        """[FIX #3] Ensures all background encoding processes are killed before exit."""
        if hasattr(self, 'save_geometry'):
            self.save_geometry()
        if getattr(self, "is_processing", False):
            reply = QMessageBox.question(self, "Quit During Processing",
                "A video is currently being processed. Closing now will cancel all progress. Quit anyway?",
                QMessageBox.Yes | QMessageBox.No, QMessageBox.No)
            if reply == QMessageBox.No:
                event.ignore()
                return
        if getattr(self, "_switching_app", False):
            self.cleanup_and_exit()
            super().closeEvent(event)
            return
        try:
            import psutil
            current_process = psutil.Process()
            children = current_process.children(recursive=True)
            for child in children:
                try:
                    self.logger.info(f"EXIT: Killing child process {child.pid} ({child.name()})")
                    child.kill()
                except: pass
        except: pass
        self.cleanup_and_exit()
        super().closeEvent(event)

    def show_message(self, title, message):
        """
        Displays a custom message box instead of alert().
        """
        msg_box = QMessageBox(self)
        msg_box.setIcon(QMessageBox.Information)
        msg_box.setWindowTitle(title)
        msg_box.setText(message)
        msg_box.exec_()

    def open_folder(self, path):
        """
        Opens the specified folder using the default file explorer.
        """
        if os.path.exists(path):
            try:
                if sys.platform == 'win32':
                    os.startfile(path, 'explore')
                elif sys.platform == 'darwin':
                    subprocess.Popen(['open', path])
                else:
                    subprocess.Popen(['xdg-open', path])
            except Exception as e:
                self.show_message("Error", f"Failed to open folder. Please navigate to {path} manually. Error: {e}")
