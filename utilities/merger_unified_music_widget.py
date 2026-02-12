"""Unified music widget with lightweight playlist and coverage guidance."""

from PyQt5.QtWidgets import QWidget, QPushButton, QVBoxLayout, QLabel
from PyQt5.QtCore import Qt, pyqtSignal
import os
from pathlib import Path
from utilities.merger_ui_style import MergerUIStyle

class UnifiedMusicWidget(QWidget):
    """Simplified music widget that launches the selection wizard."""
    music_toggled = pyqtSignal(bool)
    
    def __init__(self, parent=None):
        super().__init__(parent)
        self.parent_window = parent
        self._wizard_tracks = [] 
        self._video_total_sec = 0.0
        self.setup_ui()
        
    def setup_ui(self):
        main_layout = QVBoxLayout(self)
        main_layout.setContentsMargins(0, 0, 0, 0)
        self.toggle_button = QPushButton("♪  PICK BACKGROUND MUSIC")
        self.toggle_button.setFixedHeight(50)
        self.toggle_button.setStyleSheet(MergerUIStyle.BUTTON_STANDARD)
        self.toggle_button.setCursor(Qt.PointingHandCursor)
        self.toggle_button.clicked.connect(self.launch_wizard)
        main_layout.addWidget(self.toggle_button)
        self.lbl_summary = QLabel("No music selected")
        self.lbl_summary.setStyleSheet("font-size: 11px; color: #95a5a6; margin-top: 5px;")
        main_layout.addWidget(self.lbl_summary)

    def launch_wizard(self):
        if hasattr(self.parent_window, "music_dialog_handler"):
            self.parent_window.music_dialog_handler.open_music_wizard()

    def set_wizard_tracks(self, tracks):
        self._wizard_tracks = tracks
        n = len(tracks)
        if n == 0:
            self.lbl_summary.setText("No music selected")
            self.toggle_button.setText("♪  PICK BACKGROUND MUSIC")
            self.toggle_button.setStyleSheet(MergerUIStyle.BUTTON_STANDARD)
        else:
            total_dur = sum(t[2] for t in tracks)
            self.lbl_summary.setText(f"{n} track(s) selected ({total_dur:.1f}s)")
            self.toggle_button.setText("♪  MUSIC READY")
            self.toggle_button.setStyleSheet(MergerUIStyle.BUTTON_MERGE)

    def get_selected_tracks(self):
        return [t[0] for t in self._wizard_tracks]

    def get_wizard_tracks(self):
        return self._wizard_tracks

    def get_offset(self):
        return self._wizard_tracks[0][1] if self._wizard_tracks else 0.0

    def get_volume(self):
        return 80

    def isChecked(self):
        return len(self._wizard_tracks) > 0

    def clear_playlist(self):
        self.set_wizard_tracks([])

    def set_video_total_seconds(self, seconds: float):
        self._video_total_sec = max(0.0, float(seconds or 0.0))

    def update_coverage_guidance(self, video_total_sec: float, probe_duration_fn=None):
        self._video_total_sec = max(0.0, float(video_total_sec or 0.0))

    def export_state(self) -> dict:
        try:
            return {
                "tracks": [list(t) for t in self._wizard_tracks],
                "video_total_sec": self._video_total_sec
            }
        except Exception:
            return {}

    def apply_state(self, state: dict):
        if not isinstance(state, dict): return
        try:
            tracks = state.get("tracks", [])
            if isinstance(tracks, list):
                self.set_wizard_tracks([tuple(t) for t in tracks])
            self._video_total_sec = float(state.get("video_total_sec", 0.0))
        except Exception:
            pass
