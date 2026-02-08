"""Unified music widget with lightweight playlist and coverage guidance."""

from PyQt5.QtWidgets import (
    QWidget, QPushButton, QVBoxLayout, QHBoxLayout, QLabel,
    QComboBox, QSlider, QDoubleSpinBox, QFrame,
    QToolButton, QFileDialog, QListWidget, QListWidgetItem, QMessageBox
)

from PyQt5.QtCore import Qt, pyqtSignal
import os
from pathlib import Path

class UnifiedMusicWidget(QWidget):
    """Unified music widget with dropdown and integrated controls."""
    music_toggled = pyqtSignal(bool)
    track_selected = pyqtSignal(str)
    volume_changed = pyqtSignal(int)
    offset_changed = pyqtSignal(float)
    advanced_requested = pyqtSignal()
    
    def __init__(self, parent=None):
        super().__init__(parent)
        self.parent_window = parent
        self._is_expanded = False
        self._last_used_track = ""
        self._track_settings = {}
        self._current_track = ""
        self._playlist_paths: list[str] = []
        self._video_total_sec = 0.0
        self._track_duration_cache: dict[str, float] = {}
        self.setup_ui()
        self.setup_connections()
        self.load_track_history()
        
    def load_track_history(self):
        """Load only volume and offset settings from config, not playlist or enabled state."""
        try:
            cfg = getattr(self.parent_window, "_cfg", {}) or {}
            state = cfg.get("music_widget", {}) if isinstance(cfg, dict) else {}
            if isinstance(state, dict):
                volume = state.get("volume")
                if volume is not None:
                    try:
                        self.volume_slider.setValue(int(volume))
                    except (ValueError, TypeError):
                        pass
                offset = state.get("offset")
                if offset is not None:
                    try:
                        self.offset_spin.setValue(float(offset))
                    except (ValueError, TypeError):
                        pass
        except Exception:
            pass
        self.toggle_button.setChecked(False)
        if self._is_expanded:
            self.toggle_dropdown()
        
    def setup_ui(self):
        """Setup the unified music widget UI."""
        main_layout = QVBoxLayout(self)
        main_layout.setContentsMargins(0, 0, 0, 0)
        main_layout.setSpacing(0)
        button_row = QHBoxLayout()
        button_row.setContentsMargins(0, 0, 0, 0)
        button_row.setSpacing(2)
        self.toggle_button = QPushButton("Music: Off")
        self.toggle_button.setObjectName("musicToggleButton")
        self.toggle_button.setCheckable(True)
        self.toggle_button.setChecked(False)
        self.toggle_button.setFixedSize(120, 34)
        self.toggle_button.setCursor(Qt.PointingHandCursor)
        self.toggle_button.setToolTip("Toggle background music on/off")
        self.dropdown_button = QToolButton()
        self.dropdown_button.setText("▼")
        self.dropdown_button.setFixedSize(24, 34)
        self.dropdown_button.setCursor(Qt.PointingHandCursor)
        self.dropdown_button.setToolTip("Show music settings")
        button_row.addWidget(self.toggle_button)
        button_row.addWidget(self.dropdown_button)
        self.dropdown_panel = QFrame()
        self.dropdown_panel.setObjectName("musicDropdownPanel")
        self.dropdown_panel.setFrameStyle(QFrame.StyledPanel | QFrame.Raised)
        self.dropdown_panel.setVisible(False)
        self.dropdown_panel.setMaximumHeight(420)
        panel_layout = QVBoxLayout(self.dropdown_panel)
        panel_layout.setContentsMargins(12, 12, 12, 12)
        panel_layout.setSpacing(8)
        track_row = QHBoxLayout()
        track_row.setSpacing(8)
        track_label = QLabel("Track:")
        track_label.setFixedWidth(50)
        self.track_combo = QComboBox()
        self.track_combo.setObjectName("musicTrackCombo")
        self.track_combo.setMinimumWidth(220)
        self.track_combo.setCursor(Qt.PointingHandCursor)
        self.add_track_btn = QPushButton("Add")
        self.add_track_btn.setFixedWidth(55)
        self.add_track_btn.setCursor(Qt.PointingHandCursor)
        self.add_many_btn = QPushButton("Add files")
        self.add_many_btn.setFixedWidth(85)
        self.add_many_btn.setCursor(Qt.PointingHandCursor)
        track_row.addWidget(track_label)
        track_row.addWidget(self.track_combo, 1)
        track_row.addWidget(self.add_track_btn)
        track_row.addWidget(self.add_many_btn)
        self.playlist_list = QListWidget()
        self.playlist_list.setObjectName("musicPlaylist")
        self.playlist_list.setFixedHeight(95)
        self.playlist_list.setSelectionMode(QListWidget.SingleSelection)
        playlist_actions = QHBoxLayout()
        playlist_actions.setSpacing(8)
        self.btn_remove_track = QPushButton("Remove")
        self.btn_remove_track.setCursor(Qt.PointingHandCursor)
        self.btn_clear_tracks = QPushButton("Clear")
        self.btn_clear_tracks.setCursor(Qt.PointingHandCursor)
        playlist_actions.addWidget(self.btn_remove_track)
        playlist_actions.addWidget(self.btn_clear_tracks)
        playlist_actions.addStretch(1)
        self.coverage_label = QLabel("Coverage: add songs")
        self.coverage_label.setWordWrap(True)
        volume_row = QHBoxLayout()
        volume_row.setSpacing(8)
        volume_label = QLabel("Vol:")
        volume_label.setFixedWidth(50)
        self.volume_slider = QSlider(Qt.Horizontal)
        self.volume_slider.setObjectName("musicVolumeSlider")
        self.volume_slider.setRange(0, 100)
        self.volume_slider.setValue(25)
        self.volume_slider.setCursor(Qt.PointingHandCursor)
        self.volume_label = QLabel("25%")
        self.volume_label.setFixedWidth(40)
        self.volume_label.setAlignment(Qt.AlignRight | Qt.AlignVCenter)
        volume_row.addWidget(volume_label)
        volume_row.addWidget(self.volume_slider, 1)
        volume_row.addWidget(self.volume_label)
        offset_row = QHBoxLayout()
        offset_row.setSpacing(8)
        offset_label = QLabel("Start:")
        offset_label.setFixedWidth(50)
        self.offset_spin = QDoubleSpinBox()
        self.offset_spin.setObjectName("musicOffsetSpin")
        self.offset_spin.setSuffix(" s")
        self.offset_spin.setDecimals(1)
        self.offset_spin.setSingleStep(0.1)
        self.offset_spin.setRange(0.0, 3600.0)
        self.offset_spin.setValue(0.0)
        self.offset_spin.setMinimumWidth(100)
        self.offset_spin.setCursor(Qt.PointingHandCursor)
        offset_row.addWidget(offset_label)
        offset_row.addWidget(self.offset_spin)
        offset_row.addStretch(1)
        self.advanced_button = QPushButton("Preview / Edit")
        self.advanced_button.setObjectName("musicAdvancedButton")
        self.advanced_button.setFixedSize(120, 24)
        self.advanced_button.setCursor(Qt.PointingHandCursor)
        self.advanced_button.setToolTip("Preview track or select specific segment")
        panel_layout.addLayout(track_row)
        panel_layout.addWidget(self.playlist_list)
        panel_layout.addLayout(playlist_actions)
        panel_layout.addWidget(self.coverage_label)
        panel_layout.addLayout(volume_row)
        panel_layout.addLayout(offset_row)
        panel_layout.addWidget(self.advanced_button, 0, Qt.AlignRight)
        main_layout.addLayout(button_row)
        main_layout.addWidget(self.dropdown_panel)
        self.setup_styles()
        self._set_controls_enabled(False)
        
    def setup_styles(self):
        """Setup widget styles."""
        self.setStyleSheet("""
            QPushButton#musicToggleButton {
                background-color: #7289da;
                color: white;
                font-weight: bold;
                border: 1px solid #5b6eae;
                border-radius: 4px;
                padding: 6px 12px;
                text-align: left;
            }
            QPushButton#musicToggleButton:checked {
                background-color: #43b581;
                border: 1px solid #3aa371;
            }
            QToolButton {
                background-color: #7289da;
                color: white;
                font-weight: bold;
                border: 1px solid #5b6eae;
                border-radius: 4px;
            }
            QFrame#musicDropdownPanel {
                background-color: #2c2f33;
                border: 1px solid #7289da;
                border-radius: 6px;
                margin-top: 4px;
            }
            QSlider::groove:horizontal {
                height: 6px;
                background: #40444b;
                border-radius: 3px;
            }
            QSlider::handle:horizontal {
                background: #7289da;
                width: 16px;
                height: 16px;
                margin: -5px 0;
                border-radius: 8px;
            }
        """)
        
    def setup_connections(self):
        self.toggle_button.toggled.connect(self.on_toggle_changed)
        self.dropdown_button.clicked.connect(self.toggle_dropdown)
        self.track_combo.currentIndexChanged.connect(self.on_combo_index_changed)
        self.add_track_btn.clicked.connect(self.add_current_track_to_playlist)
        self.add_many_btn.clicked.connect(self.add_many_tracks)
        self.btn_remove_track.clicked.connect(self.remove_selected_playlist_track)
        self.btn_clear_tracks.clicked.connect(self.clear_playlist)
        self.volume_slider.valueChanged.connect(self.on_volume_changed)
        self.offset_spin.valueChanged.connect(self.on_offset_changed)
        self.advanced_button.clicked.connect(self.on_advanced_button_clicked)
        
    def on_advanced_button_clicked(self):
        """Handle advanced button click - show music offset dialog for preview."""
        if not self.parent_window:
            return
        track = self.get_selected_track()
        if not track:
            QMessageBox.warning(self, "No Track", "Please select a track to preview.")
            return
        self.advanced_requested.emit()
        try:
            if hasattr(self.parent_window, 'music_dialog_handler'):
                self.parent_window.music_dialog_handler.show_music_offset_dialog(track)
        except Exception as e:
            try:
                from PyQt5.QtCore import QUrl
                from PyQt5.QtGui import QDesktopServices
                QDesktopServices.openUrl(QUrl.fromLocalFile(track))
            except Exception:
                pass
        
    def on_toggle_changed(self, checked):
        self.music_toggled.emit(checked)
        self._set_controls_enabled(checked)
        self.update_button_text()
            
    def update_button_text(self):
        if self.toggle_button.isChecked():
            n = len(self._playlist_paths)
            if n <= 0:
                self.toggle_button.setText("♪ Music On")
            elif n == 1:
                track = self.get_track_name_safe()
                self.toggle_button.setText(f"♪ {track[:10]}..." if len(track) > 10 else f"♪ {track}")
            else:
                self.toggle_button.setText(f"♪ {n} tracks")
        else:
            self.toggle_button.setText("Music: Off")

    def toggle_dropdown(self):
        self._is_expanded = not self._is_expanded
        self.dropdown_panel.setVisible(self._is_expanded)
        self.dropdown_button.setText("▲" if self._is_expanded else "▼")

    def _set_controls_enabled(self, enabled: bool):
        self.track_combo.setEnabled(enabled)
        self.add_track_btn.setEnabled(enabled)
        self.add_many_btn.setEnabled(enabled)
        self.playlist_list.setEnabled(enabled)
        self.btn_remove_track.setEnabled(enabled and len(self._playlist_paths) > 0)
        self.btn_clear_tracks.setEnabled(enabled and len(self._playlist_paths) > 0)
        self.volume_slider.setEnabled(enabled)
        self.offset_spin.setEnabled(enabled)
        self.advanced_button.setEnabled(enabled)

    def on_combo_index_changed(self, index):
        text = self.track_combo.itemText(index)
        if text == "Browse...":
            self.browse_music_file()
        else:
            self._current_track = text
            self.update_button_text()
            path = self.track_combo.itemData(index)
            if path:
                self.track_selected.emit(path)

    def add_current_track_to_playlist(self):
        path = self.track_combo.currentData()
        if not path or path == "BROWSE_ACTION":
            return
        self._add_track_to_playlist(path)

    def add_many_tracks(self):
        files, _ = QFileDialog.getOpenFileNames(
            self,
            "Select Music Files",
            str(Path.home() / "Music"),
            "Audio Files (*.mp3 *.wav *.aac *.m4a *.flac *.ogg)",
        )
        if not files:
            return
        for p in files:
            self._add_track_to_playlist(p)

    def _add_track_to_playlist(self, path: str):
        if not path:
            return
        if not os.path.isfile(path):
            QMessageBox.warning(self, "Missing file", "Selected track file no longer exists.")
            return
        if path in self._playlist_paths:
            QMessageBox.information(self, "Already added", "This track is already in your playlist.")
            return
        self._playlist_paths.append(path)
        self._track_duration_cache.pop(path, None)
        item = QListWidgetItem(os.path.basename(path))
        item.setToolTip(path)
        item.setData(Qt.UserRole, path)
        self.playlist_list.addItem(item)
        self._refresh_controls_after_playlist_change()

    def remove_selected_playlist_track(self):
        row = self.playlist_list.currentRow()
        if row < 0 or row >= len(self._playlist_paths):
            return
        old_path = self._playlist_paths[row]
        self.playlist_list.takeItem(row)
        del self._playlist_paths[row]
        self._track_duration_cache.pop(old_path, None)
        self._refresh_controls_after_playlist_change()

    def clear_playlist(self):
        self.playlist_list.clear()
        self._playlist_paths = []
        self._track_duration_cache.clear()
        self._refresh_controls_after_playlist_change()

    def _refresh_controls_after_playlist_change(self):
        self.btn_remove_track.setEnabled(self.toggle_button.isChecked() and len(self._playlist_paths) > 0)
        self.btn_clear_tracks.setEnabled(self.toggle_button.isChecked() and len(self._playlist_paths) > 0)
        self.update_button_text()
        self.update_coverage_guidance(self._video_total_sec)

    def browse_music_file(self):
        path, _ = QFileDialog.getOpenFileName(
            self, "Select Music File", str(Path.home() / "Music"), 
            "Audio Files (*.mp3 *.wav *.aac *.m4a *.flac *.ogg)"
        )
        if path:
            filename = os.path.basename(path)
            idx = self.track_combo.count() - 1
            self.track_combo.insertItem(idx, filename, path)
            self.track_combo.setCurrentIndex(idx)
            self._add_track_to_playlist(path)
        else:
            self.track_combo.setCurrentIndex(0)
            
    def on_volume_changed(self, value):
        self.volume_label.setText(f"{value}%")
        self.volume_changed.emit(value)
            
    def on_offset_changed(self, value):
        self.offset_changed.emit(value)

    def get_track_name_safe(self):
        return self.track_combo.currentText()
        
    def load_tracks(self, mp3_folder):
        self.track_combo.blockSignals(True)
        self.track_combo.clear()
        self.track_combo.addItem("No Track Selected", "")
        if os.path.exists(mp3_folder):
            for file in sorted(os.listdir(mp3_folder)):
                if file.lower().endswith(('.mp3', '.wav', '.aac', '.m4a')):
                    self.track_combo.addItem(file, os.path.join(mp3_folder, file))
        self.track_combo.addItem("Browse...", "BROWSE_ACTION")
        self.track_combo.blockSignals(False)

    def set_video_total_seconds(self, seconds: float):
        self._video_total_sec = max(0.0, float(seconds or 0.0))
        self.update_coverage_guidance(self._video_total_sec)

    def update_coverage_guidance(self, video_total_sec: float, probe_duration_fn=None):
        self._video_total_sec = max(0.0, float(video_total_sec or 0.0))
        music_total = 0.0
        for p in self._playlist_paths:
            d = self._track_duration_cache.get(p)
            if d is None and probe_duration_fn is not None:
                try:
                    d = max(0.0, float(probe_duration_fn(p) or 0.0))
                except Exception:
                    d = 0.0
                self._track_duration_cache[p] = d
            music_total += max(0.0, float(d or 0.0))

        def _t(sec: float) -> str:
            s = max(0, int(round(sec)))
            return f"{s//3600:02}:{(s%3600)//60:02}:{s%60:02}"
        if len(self._playlist_paths) == 0:
            self.coverage_label.setText("Coverage: no songs selected yet.")
            self.coverage_label.setStyleSheet("color: #ff6b6b;")
            return
        if self._video_total_sec <= 0:
            self.coverage_label.setText(f"Coverage: songs total {_t(music_total)}. Add videos to calculate target.")
            self.coverage_label.setStyleSheet("color: #7289da;")
            return
        if music_total >= self._video_total_sec:
            self.coverage_label.setText(
                f"Coverage: {_t(music_total)} / {_t(self._video_total_sec)} ✅ Enough music."
            )
            self.coverage_label.setStyleSheet("color: #43b581;")
        else:
            need = self._video_total_sec - music_total
            self.coverage_label.setText(
                f"Coverage: {_t(music_total)} / {_t(self._video_total_sec)} ⚠ Add about {_t(need)} more music."
            )
            self.coverage_label.setStyleSheet("color: #ffa500;")

    def get_selected_track(self):
        row = self.playlist_list.currentRow()
        if 0 <= row < len(self._playlist_paths):
            return self._playlist_paths[row]
        if self._playlist_paths:
            return self._playlist_paths[0]
        return None

    def get_selected_tracks(self):
        if self.toggle_button.isChecked():
            return list(self._playlist_paths)
        return []

    def get_crossfade_seconds(self) -> float:
        return 3.0

    def get_fadeout_lead_seconds(self) -> float:
        return 7.0

    def get_volume(self):
        return int(self.volume_slider.value())

    def get_offset(self):
        return float(self.offset_spin.value())

    def export_state(self) -> dict:
        try:
            return {
                "volume": int(self.volume_slider.value()),
                "offset": float(self.offset_spin.value()),
            }
        except Exception:
            return {}

    def apply_state(self, state: dict):
        if not isinstance(state, dict):
            return
        try:
            playlist = state.get("playlist", [])
            self.playlist_list.clear()
            self._playlist_paths = []
            self._track_duration_cache.clear()
            missing_count = 0
            if isinstance(playlist, list):
                for p in playlist:
                    if isinstance(p, str) and p and os.path.isfile(p):
                        self._add_track_to_playlist(p)
                    elif isinstance(p, str) and p:
                        missing_count += 1
            self.volume_slider.setValue(int(state.get("volume", self.volume_slider.value())))
            self.offset_spin.setValue(float(state.get("offset", self.offset_spin.value())))
            self.toggle_button.setChecked(bool(state.get("enabled", False)))
            expanded = bool(state.get("expanded", False))
            if expanded != self._is_expanded:
                self.toggle_dropdown()
            self._refresh_controls_after_playlist_change()
            if missing_count > 0:
                self.coverage_label.setText(f"Coverage: {missing_count} saved song(s) were missing and removed.")
                self.coverage_label.setStyleSheet("color: #ffa500;")
        except Exception:
            pass