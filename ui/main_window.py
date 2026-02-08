import faulthandler
import logging
import os
import signal
import subprocess
import sys
import threading
import traceback
from logging.handlers import RotatingFileHandler
import vlc
from PyQt5.QtCore import pyqtSignal, QTimer, QUrl, Qt, QCoreApplication
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
from ui.parts.events_mixin import EventsMixin
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
        try:
            msg = self.format(record)
            if hasattr(self.ui, "live_log_signal"):
                self.ui.live_log_signal.emit(msg)
        except Exception:
            pass

class CleanupWorker(QObject):
    """Worker to clean up old temporary files in the background."""

    def run(self):
        try:
            import time
            temp_dir = tempfile.gettempdir()
            patterns = [
                "core-*.mp4", "intro-*.mp4", "ffmpeg2pass-*.log", 
                "drawtext-*.txt", "filter_complex-*.txt", "concat-*.txt",
                "thumb-*.jpg", "snapshot-*.png"
            ]
            now = time.time()
            limit_seconds = 6 * 3600 
            for pattern in patterns:
                for old_file in glob.glob(os.path.join(temp_dir, pattern)):
                    try:
                        if os.path.isfile(old_file):
                            if now - os.path.getmtime(old_file) > limit_seconds:
                                os.remove(old_file)
                    except OSError:
                        pass
        except Exception:
            pass

class VideoCompressorApp(QMainWindow, UiBuilderMixin, PhaseOverlayMixin, EventsMixin, PlayerMixin, VolumeMixin, TrimMixin, MusicMixin, FfmpegMixin, KeyboardMixin, PersistentWindowMixin):
    progress_update_signal = pyqtSignal(int)
    status_update_signal = pyqtSignal(str)
    process_finished_signal = pyqtSignal(bool, str)
    live_log_signal = pyqtSignal(str)
    video_ended_signal = pyqtSignal()

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
            current_ms = 0
            if self.vlc_player:
                if self.vlc_player.is_playing():
                    self.vlc_player.pause()
                current_ms = max(0, self.vlc_player.get_time())
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
                vlc_instance=self.vlc_instance,
                volume=current_volume
            )
            result = dlg.exec_()
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
            self.vlc_player.set_time(int(resume_time))
            self.positionSlider.setValue(int(resume_time))
            self.is_playing = False
            self.playPauseButton.setText("PLAY")
            self.playPauseButton.setIcon(self.style().standardIcon(QStyle.SP_MediaPlay))
        except Exception as e:
            self.logger.critical(f"CRITICAL: Error in Granular Speed Dialog: {e}\n{traceback.format_exc()}")
            QMessageBox.critical(self, "Error", f"An error occurred opening the editor:\n{e}")
            self.granular_checkbox.setChecked(False)

    def on_hardware_scan_finished(self, detected_mode: str):
        """Receives the result from the background hardware scan."""
        if not hasattr(self, 'status_bar'):
            return
        self.hardware_strategy = detected_mode
        self.scan_complete = True
        self.logger.info(f"Hardware Strategy finalized: {self.hardware_strategy}")
        if self.hardware_strategy == "CPU":
            self.show_status_warning("⚠️ No compatible GPU detected. Running in slower CPU-only mode.")
        else:
            if hasattr(self, 'hardware_status_label'):
                self.hardware_status_label.setText(f"✅ Hardware Acceleration Enabled ({self.hardware_strategy})")
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
            if getattr(self, "vlc_player", None):
                self.vlc_player.stop()
            if getattr(self, "vlc_music_player", None):
                self.vlc_music_player.stop()
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

    def log_overlay_sink(self, line: str):
        """Thread-safe slot to receive log messages."""
        try:
            self._append_live_log(line)
        except Exception:
            pass

    def _on_speed_changed(self, value):
        self.playback_rate = value
        if self.vlc_player and self.vlc_player.is_playing():
            self.vlc_player.pause()
            self.playPauseButton.setText("PLAY")
            self.playPauseButton.setIcon(self.style().standardIcon(QStyle.SP_MediaPlay))
            self.is_playing = False
            if self.timer.isActive():
                self.timer.stop()
        self.logger.info(f"Playback speed changed to {value}x. Player paused.")

    def __init__(self, file_path=None, hardware_strategy="CPU"):
        super().__init__()
        self.script_dir = os.path.dirname(sys.executable) if getattr(sys, 'frozen', False) else os.path.dirname(os.path.abspath(__file__))
        self.base_dir = os.path.abspath(os.path.join(self.script_dir, os.pardir))
        self.bin_dir = os.path.join(self.base_dir, 'binaries')
        self.logger = setup_logger(self.base_dir, "Fortnite_Video_Software.log", "Main_App")
        self.config_manager = ConfigManager(os.path.join(self.base_dir, 'config', 'main_app.conf'))
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
        self.volume_shortcut_target = 'main'
        self._phase_is_processing = False
        self._phase_dots = 1
        self._base_title = "Fortnite Video Compressor"
        self._music_files = []
        self.scan_complete = False
        self.set_style()
        self.setWindowTitle(self._base_title)
        session_data = StateTransfer.load_state()
        if session_data:
            self.logger.info("Session data found. Restoring state...")
            if 'input_file' in session_data:
                file_path = session_data['input_file']
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
        try:
            if not any(isinstance(h, _QtLiveLogHandler) for h in self.logger.handlers):
                qt_handler = _QtLiveLogHandler(self) 
                qt_handler.setLevel(logging.INFO)
                self.logger.addHandler(qt_handler)
        except Exception:
            pass
        self.logger.info("=== Application started ===")
        self.logger.info(f"Initialized with Hardware Strategy: {self.hardware_strategy}")
        self._setup_vlc()
        self.timer = QTimer(self)
        self.timer.setInterval(100)
        self.timer.timeout.connect(self.update_player_state)
        self.setup_persistence(
            config_path=os.path.join(self.base_dir, 'config', 'main_app.conf'),
            settings_key='window_geometry',
            default_geo={'x': 200, 'y': 200, 'w': 1150, 'h': 700},
            title_info_provider=lambda: f"{self._base_title}  —  {self.width()}x{self.height()}"
        )
        self.setMinimumSize(1150, 575)
        self._scan_mp3_folder()
        self._update_window_size_in_title()

        def _seek_shortcut(offset_ms):
            if getattr(self, "input_file_path", None):
                self.seek_relative_time(offset_ms)
        QShortcut(QKeySequence(Qt.Key_Left), self, lambda: _seek_shortcut(-250))
        QShortcut(QKeySequence(Qt.Key_Right), self, lambda: _seek_shortcut(250))
        QShortcut(QKeySequence(Qt.CTRL | Qt.Key_Left), self, lambda: _seek_shortcut(-5))
        QShortcut(QKeySequence(Qt.CTRL | Qt.Key_Right), self, lambda: _seek_shortcut(5))
        self.positionSlider.trim_times_changed.connect(self._on_slider_trim_changed)
        self.positionSlider.music_trim_changed.connect(self._on_music_trim_changed)
        if file_path:
            self.handle_file_selection(file_path)
        self.cleanup_thread = QThread()
        self.cleanup_worker = CleanupWorker()
        self.cleanup_worker.moveToThread(self.cleanup_thread)
        self.cleanup_thread.started.connect(self.cleanup_worker.run)
        self.cleanup_thread.finished.connect(self.cleanup_worker.deleteLater)
        self.cleanup_thread.finished.connect(self.cleanup_thread.deleteLater)
        self.cleanup_thread.start()
    @property
    def original_duration(self):
        """Return original duration in seconds (float)."""
        return self.original_duration_ms / 1000.0 if self.original_duration_ms else 0.0

    def _setup_vlc(self):
        """Initializes the VLC instance and player."""
        if hasattr(os, 'add_dll_directory'):
            try:
                os.add_dll_directory(self.bin_dir)
            except Exception: pass
        plugin_path = os.path.join(self.bin_dir, "plugins")
        vlc_args = [
            '--no-xlib', '--no-video-title-show', '--no-plugins-cache',
            '--file-caching=1000', '--audio-time-stretch', '--verbose=-1',
        ]
        if os.path.exists(plugin_path):
            vlc_args.append(f"--plugin-path={plugin_path.replace('\\', '/')}")
            os.environ["VLC_PLUGIN_PATH"] = plugin_path
        os.environ["PATH"] = self.bin_dir + os.pathsep + os.environ["PATH"]
        self.vlc_player = None
        try:
            self.vlc_instance = vlc.Instance(vlc_args)
            if self.vlc_instance:
                self.vlc_player = self.vlc_instance.media_player_new()
            else:
                self.logger.error("VLC Instance creation returned None.")
        except Exception as e:
            self.logger.error(f"CRITICAL: VLC Failed to initialize. Error: {e}")
            self.vlc_instance = None
        if self.vlc_player:
            try:
                em = self.vlc_player.event_manager()
                if em:
                    em.event_attach(vlc.EventType.MediaPlayerEndReached, self._on_vlc_end_reached)
                    em.event_attach(vlc.EventType.MediaPlayerMediaChanged, self._on_duration_changed)
                    em.event_attach(vlc.EventType.MediaDurationChanged, self._on_duration_changed)
                else:
                    self.logger.warning("Could not get VLC event manager.")
            except Exception as e:
                self.logger.error("Failed to attach VLC event handlers: %s", e)
            try:
                self.vlc_player.audio_set_mute(False)
                self.apply_master_volume()
            except Exception: pass
    
    def _on_duration_changed(self, event, player=None):
        """Event handler for when media duration becomes available."""
        try:
            player = player or self.vlc_player
            duration_ms = player.get_media().get_duration()
            if duration_ms > 0:
                self.positionSlider.setRange(0, duration_ms)
                self.positionSlider.set_duration_ms(duration_ms)
                self.logger.info(f"VLC Event: Duration changed to {duration_ms}ms.")
        except Exception as e:
            self.logger.error(f"Error in _on_duration_changed event handler: {e}")

    def keyPressEvent(self, event):
        """Handle key presses for shortcuts."""
        if event.key() == Qt.Key_F11:
            self.launch_advanced_editor()
        elif event.key() == Qt.Key_F12:
            self.launch_crop_tool()
        else:
            super().keyPressEvent(event)

    def launch_crop_tool(self):
        """Launches the crop tool application with a heartbeat check."""
        try:
            state = {
                "input_file": self.input_file_path,
                "trim_start": self.trim_start_ms,
                "trim_end": self.trim_end_ms,
                "speed_segments": self.speed_segments,
                "hardware_mode": getattr(self, "hardware_strategy", "CPU")
            }
            StateTransfer.save_state(state)
            if getattr(self, "input_file_path", None) or self.speed_segments:
                 pass
            self.logger.info("ACTION: Launching Crop Tool via F12...")
            script_path = os.path.join(self.base_dir, 'developer_tools', 'crop_tools.py')
            if not os.path.exists(script_path):
                raise FileNotFoundError(f"Crop Tool script not found at: {script_path}")
            if self.vlc_player:
                self.vlc_player.stop()
            command = [sys.executable, "-B", script_path]
            if self.input_file_path:
                command.append(self.input_file_path)
            env = os.environ.copy()
            env["PYTHONPATH"] = self.base_dir + os.pathsep + env.get("PYTHONPATH", "")
            self.logger.info(f"Command: {command}")
            proc = subprocess.Popen(
                command, 
                cwd=self.base_dir, 
                env=env,
                stderr=subprocess.PIPE,
                stdout=subprocess.PIPE,
                text=True
            )
            QTimer.singleShot(3000, lambda: self._finalize_switch(proc))
        except Exception as e:
            self.logger.critical(f"ERROR: Failed to launch Crop Tool. Error: {e}")
            QMessageBox.critical(self, "Launch Failed", f"Could not launch Crop Tool.\n\nError: {e}")

    def _finalize_switch(self, proc):
        """Checks if child process is still alive before closing parent."""
        if proc.poll() is None:
            self.logger.info("Crop Tool launched successfully (PID: %s). Closing Main App.", proc.pid)
            self.close()
        else:
            error_output = "No output captured."
            try:
                error_output = proc.stderr.read()
                if not error_output:
                    error_output = proc.stdout.read()
            except Exception as e:
                error_output = f"Could not read stderr/stdout: {e}"
            self.logger.error(f"Crop Tool crashed on startup (Exit Code: {proc.poll()}).\nOutput:\n{error_output}")
            QMessageBox.critical(self, "Launch Error", f"The Crop Tool crashed immediately after starting.\n\nReason: {error_output}")

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
            if self.vlc_player:
                self.vlc_player.stop()
            subprocess.Popen(command, cwd=self.base_dir)
            self.logger.info("Advanced Editor process started. Closing main app.")
            self.close()
        except Exception as e:
            self.logger.critical(f"ERROR: Failed to launch Advanced Editor. Error: {e}")
            QMessageBox.critical(self, "Launch Failed", f"Could not launch Advanced Editor. Error: {e}")

    def _on_music_trim_changed(self, start_ms, end_ms):
        """Handles music timeline bar changes from the slider (in ms)."""
        self.music_timeline_start_ms = start_ms
        self.music_timeline_end_ms = end_ms
        if hasattr(self, 'vlc_player') and self.vlc_player.is_playing():
            self.set_vlc_position(self.vlc_player.get_time(), sync_only=True)
        self.logger.info(f"MUSIC: Timeline updated to start={start_ms/1000.0:.2f}s, end={end_ms/1000.0:.2f}s")

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
            self._layout_volume_slider()
            self._update_volume_badge()
        except Exception:
            pass

    def _on_slider_trim_changed(self, start_ms, end_ms):
        """Handles trim time changes from the custom slider (in ms)."""
        self.trim_start_ms = start_ms
        self.trim_end_ms = end_ms
        if self.music_timeline_start_ms > 0 and self.music_timeline_end_ms > 0 and self.add_music_checkbox.isChecked():
            video_dur_ms = end_ms - start_ms
            music_dur_ms = self.music_timeline_end_ms - self.music_timeline_start_ms
            if music_dur_ms > video_dur_ms:
                music_dur_ms = video_dur_ms
            new_music_start_ms = max(start_ms, self.music_timeline_start_ms)
            if new_music_start_ms + music_dur_ms > end_ms:
                new_music_start_ms = end_ms - music_dur_ms
            new_music_end_ms = new_music_start_ms + music_dur_ms
            if (self.music_timeline_start_ms != new_music_start_ms or self.music_timeline_end_ms != new_music_end_ms):
                self.music_timeline_start_ms = new_music_start_ms
                self.music_timeline_end_ms = new_music_end_ms
                self.positionSlider.set_music_times(new_music_start_ms, new_music_end_ms)
                self.logger.info(f"MUSIC: Timeline auto-adjusted to fit new video trim: start={new_music_start_ms/1000.0:.2f}s, end={new_music_end_ms/1000.0:.2f}s")
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
                border: none;
                padding: 10px 18px;
                border-radius: 8px;
                font-weight: bold;
            }
            QPushButton:hover { background-color: #2980b9; }
            QPushButton#WhatsappButton { background-color: #25D366; }
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

    def select_file(self):
        try:
            if getattr(self, "vlc_player", None) and self.vlc_player.is_playing():
                self.vlc_player.set_pause(1)
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
            self.handle_file_selection(file_to_load)
        else:
            self.logger.info("FILE: dialog canceled")

    def handle_file_selection(self, file_path):
        """Loads a file, starts playback, and initiates duration polling."""
        try:
            player = getattr(self, "vlc_player", None)
            if player:
                if player.is_playing():
                    player.stop()
                current_media = player.get_media()
                if current_media:
                    current_media.release()
                    player.set_media(None)
            timer = getattr(self, "timer", None)
            if timer and timer.isActive():
                timer.stop()
        except Exception as stop_err:
            self.logger.error("Error stopping existing player/media: %s", stop_err)
        try:
            self.reset_app_state()
        except Exception as reset_err:
            self.logger.error("Error during UI reset: %s", reset_err)
        self.logger.info("FILE: loading for playback: %s", file_path)
        self.input_file_path = file_path
        self.drop_label.setWordWrap(True)
        self.drop_label.setText(os.path.basename(self.input_file_path))
        dir_path = os.path.dirname(file_path)
        if os.path.isdir(dir_path):
            self.last_dir = dir_path
        p = os.path.abspath(str(file_path))
        if not os.path.isfile(p):
            self.logger.error("Selected file not found: %s", p)
            return
        if self.vlc_instance and self.vlc_player:
            media = self.vlc_instance.media_new_path(p)
            if media is None:
                try:
                    mrl = QUrl.fromLocalFile(p).toString()
                except Exception:
                    mrl = "file:///" + p.replace("\\\\", "/")
                media = self.vlc_instance.media_new(mrl)
            if media is None:
                self.logger.error("Failed to open media: %s", p)
                return
            self.vlc_player.set_media(media)
            try:
                if sys.platform.startswith('win'):
                    self.vlc_player.set_hwnd(int(self.video_surface.winId()))
            except Exception as hwnd_err:
                self.logger.error("Failed to set HWND for player: %s", hwnd_err)
            self.vlc_player.play()
            if hasattr(self, "_on_mobile_toggled"):
                QTimer.singleShot(150, lambda: self._on_mobile_toggled(self.mobile_checkbox.isChecked()))
        else:
            self.logger.warning("VLC not available. Skipping playback. (CPU Mode)")
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
            if self.add_music_checkbox.isChecked():
                self.add_music_checkbox.setChecked(False)
            else:
                self._reset_music_player()
        except AttributeError:
            pass
        self.drop_label.setText("Drag & Drop\r\nVideo File Here:")
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
        if getattr(self, "is_processing", False) and hasattr(self, "process_thread"):
            self.logger.warning("App closing during process. Killing ffmpeg...")
            self.process_thread.cancel()
        try:
            if getattr(self, "vlc_player", None):
                self.vlc_player.stop()
            if getattr(self, "vlc_music_player", None):
                self.vlc_music_player.stop()
        except Exception as e:
            self.logger.error("Failed to stop VLC players on close: %s", e)
        self._save_app_state_and_config()
        QCoreApplication.instance().quit()

    def resizeEvent(self, event):
        """[FIX #1 & #24] Handles window resizing with throttling for overlay smoothness."""
        self._update_window_size_in_title()
        if hasattr(self, '_resize_timer'):
            self._resize_timer.start()
        else:
            self._delayed_resize_event()
        super().resizeEvent(event)

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

    def share_via_whatsapp(self):
        url = "https://web.whatsapp.com"
        try:
            if sys.platform == 'win32':
                os.startfile(url)
            elif sys.platform == 'darwin':
                subprocess.Popen(['open', url])
            else:
                subprocess.Popen(['xdg-open', url])
        except Exception as e:
            self.show_message("Error", f"Failed to open WhatsApp. Please visit {url} manually. Error: {e}")