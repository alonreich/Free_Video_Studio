"""
Unified Music Widget for improved UX flow.
Implements a single "Music" button with dropdown arrow and integrated controls.
"""

from PyQt5.QtWidgets import (
    QWidget, QPushButton, QVBoxLayout, QHBoxLayout, QLabel, 
    QComboBox, QSlider, QDoubleSpinBox, QFrame,
    QMenu, QAction, QToolButton, QToolTip
)

from PyQt5.QtCore import Qt, pyqtSignal, QPoint
from PyQt5.QtGui import QFont
import os
import json
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
        self._most_frequent_track = ""
        self._track_frequencies = {}
        self._current_track = ""
        self.setup_ui()
        self.setup_connections()
        self.load_track_history()
        
    def load_track_history(self):
        """Load track usage history from config."""
        self._track_settings = {}
        self._track_frequencies = {}
        self._most_frequent_track = ""
        self._last_used_track = ""
        
    def setup_ui(self):
        """Setup the unified music widget UI."""
        main_layout = QVBoxLayout(self)
        main_layout.setContentsMargins(0, 0, 0, 0)
        main_layout.setSpacing(0)
        button_row = QHBoxLayout()
        button_row.setContentsMargins(0, 0, 0, 0)
        button_row.setSpacing(2)
        self.toggle_button = QPushButton("Music")
        self.toggle_button.setObjectName("musicToggleButton")
        self.toggle_button.setCheckable(True)
        self.toggle_button.setChecked(False)
        self.toggle_button.setFixedSize(100, 34)
        self.toggle_button.setCursor(Qt.PointingHandCursor)
        self.toggle_button.setToolTip("Toggle background music on/off with last used settings")
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
        self.dropdown_panel.setMaximumHeight(300)
        panel_layout = QVBoxLayout(self.dropdown_panel)
        panel_layout.setContentsMargins(12, 12, 12, 12)
        panel_layout.setSpacing(8)
        track_row = QHBoxLayout()
        track_row.setSpacing(8)
        track_label = QLabel("Track:")
        track_label.setFixedWidth(50)
        self.track_combo = QComboBox()
        self.track_combo.setObjectName("musicTrackCombo")
        self.track_combo.setMinimumWidth(250)
        self.track_combo.setCursor(Qt.PointingHandCursor)
        self.track_combo.setContextMenuPolicy(Qt.CustomContextMenu)
        track_row.addWidget(track_label)
        track_row.addWidget(self.track_combo, 1)
        volume_row = QHBoxLayout()
        volume_row.setSpacing(8)
        volume_label = QLabel("Volume:")
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
        self.advanced_button = QPushButton("Advanced...")
        self.advanced_button.setObjectName("musicAdvancedButton")
        self.advanced_button.setFixedSize(100, 24)
        self.advanced_button.setCursor(Qt.PointingHandCursor)
        self.advanced_button.setToolTip("Open timeline selection dialog")
        panel_layout.addLayout(track_row)
        panel_layout.addLayout(volume_row)
        panel_layout.addLayout(offset_row)
        panel_layout.addWidget(self.advanced_button, 0, Qt.AlignRight)
        main_layout.addLayout(button_row)
        main_layout.addWidget(self.dropdown_panel)
        self.setup_styles()
        
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
            }
            QPushButton#musicToggleButton:checked {
                background-color: #43b581;
                border: 1px solid #3aa371;
            }
            QPushButton#musicToggleButton:hover {
                background-color: #5b6eae;
            }
            QPushButton#musicToggleButton:checked:hover {
                background-color: #3aa371;
            }
            QToolButton {
                background-color: #7289da;
                color: white;
                font-weight: bold;
                border: 1px solid #5b6eae;
                border-radius: 4px;
            }
            QToolButton:hover {
                background-color: #5b6eae;
            }
            QFrame#musicDropdownPanel {
                background-color: #2c2f33;
                border: 1px solid #7289da;
                border-radius: 6px;
                margin-top: 4px;
            }
            QPushButton#musicAdvancedButton {
                background-color: #4a4d52;
                color: white;
                border: 1px solid #5b6eae;
                border-radius: 3px;
                padding: 2px 8px;
            }
            QPushButton#musicAdvancedButton:hover {
                background-color: #5b6eae;
            }
            QSlider#musicVolumeSlider::groove:horizontal {
                border: 1px solid #1f2a36;
                background: qlineargradient(x1:0, y1:0, x2:1, y2:0,
                    stop:0   #e64c4c,
                    stop:0.25 #f7a8a8,
                    stop:0.50 #f2f2f2,
                    stop:0.75 #7bcf43,
                    stop:1   #009b00);
                height: 8px;
                border-radius: 4px;
            }
            QSlider#musicVolumeSlider::handle:horizontal {
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
                width: 18px;
                height: 18px;
                margin: -5px 0;
                border-radius: 9px;
            }
        """)
        
    def setup_connections(self):
        """Setup signal connections."""
        self.toggle_button.toggled.connect(self.on_toggle_changed)
        self.dropdown_button.clicked.connect(self.toggle_dropdown)
        self.track_combo.currentTextChanged.connect(self.on_track_selected)
        self.track_combo.customContextMenuRequested.connect(self.show_track_context_menu)
        self.volume_slider.valueChanged.connect(self.on_volume_changed)
        self.offset_spin.valueChanged.connect(self.on_offset_changed)
        self.advanced_button.clicked.connect(self.advanced_requested.emit)
        self.volume_slider.enterEvent = self.on_volume_slider_enter
        self.volume_slider.leaveEvent = self.on_volume_slider_leave
        
    def on_toggle_changed(self, checked):
        """Handle music toggle state change."""
        self.music_toggled.emit(checked)
        if checked and self._last_used_track and self._last_used_track in self.get_track_names():
            self.select_track(self._last_used_track)
            
    def toggle_dropdown(self):
        """Toggle dropdown panel visibility."""
        self._is_expanded = not self._is_expanded
        self.dropdown_panel.setVisible(self._is_expanded)
        self.dropdown_button.setText("▲" if self._is_expanded else "▼")
        self.dropdown_button.setToolTip("Hide music settings" if self._is_expanded else "Show music settings")
        
    def on_track_selected(self, track_name):
        """Handle track selection."""
        if not track_name:
            return
        self._current_track = track_name
        self._track_frequencies[track_name] = self._track_frequencies.get(track_name, 0) + 1
        if self._track_frequencies[track_name] > self._track_frequencies.get(self._most_frequent_track, 0):
            self._most_frequent_track = track_name
        if track_name in self._track_settings:
            settings = self._track_settings[track_name]
            self.volume_slider.setValue(settings.get('volume', 25))
            self.offset_spin.setValue(settings.get('offset', 0.0))
        track_path = self.get_track_path(track_name)
        if track_path:
            self.track_selected.emit(track_path)
            self._last_used_track = track_name
            
    def on_volume_changed(self, value):
        """Handle volume change."""
        self.volume_label.setText(f"{value}%")
        self.volume_changed.emit(value)
        if self._current_track:
            if self._current_track not in self._track_settings:
                self._track_settings[self._current_track] = {}
            self._track_settings[self._current_track]['volume'] = value
            
    def on_offset_changed(self, value):
        """Handle offset change."""
        self.offset_changed.emit(value)
        if self._current_track:
            if self._current_track not in self._track_settings:
                self._track_settings[self._current_track] = {}
            self._track_settings[self._current_track]['offset'] = value
            
    def show_track_context_menu(self, position):
        """Show context menu for track selection."""
        menu = QMenu(self)
        use_default_action = QAction("Use this track with default settings", self)
        use_default_action.triggered.connect(self.use_track_with_defaults)
        edit_settings_action = QAction("Edit track settings...", self)
        edit_settings_action.triggered.connect(self.edit_track_settings)
        preview_action = QAction("Preview track", self)
        preview_action.triggered.connect(self.preview_track)
        menu.addAction(use_default_action)
        menu.addAction(edit_settings_action)
        menu.addAction(preview_action)
        menu.exec_(self.track_combo.mapToGlobal(position))
        
    def use_track_with_defaults(self):
        """Use selected track with default settings."""
        track_name = self.track_combo.currentText()
        if track_name:
            self.volume_slider.setValue(25)
            self.offset_spin.setValue(0.0)
            if track_name in self._track_settings:
                del self._track_settings[track_name]
                
    def edit_track_settings(self):
        """Edit settings for current track."""
        if not self._is_expanded:
            self.toggle_dropdown()
            
    def preview_track(self):
        """Preview selected track."""
        track_name = self.track_combo.currentText()
        if track_name and hasattr(self.parent, 'preview_music_track'):
            track_path = self.get_track_path(track_name)
            if track_path:
                self.parent.preview_music_track(track_path)
                
    def on_volume_slider_enter(self, event):
        """Show numeric value tooltip on hover."""
        QToolTip.showText(
            self.volume_slider.mapToGlobal(QPoint(0, -20)),
            f"Volume: {self.volume_slider.value()}%"
        )
        super().enterEvent(event)
        
    def on_volume_slider_leave(self, event):
        """Hide tooltip on leave."""
        QToolTip.hideText()
        super().leaveEvent(event)
        
    def load_tracks(self, mp3_folder):
        """Load tracks from MP3 folder."""
        self.track_combo.clear()
        if not os.path.exists(mp3_folder):
            return
        tracks = []
        for file in os.listdir(mp3_folder):
            if file.lower().endswith('.mp3'):
                tracks.append(file)
        tracks.sort()
        self.track_combo.addItem("No music", "")
        for track in tracks:
            self.track_combo.addItem(track, os.path.join(mp3_folder, track))
        if self._most_frequent_track and self._most_frequent_track in tracks:
            self.select_track(self._most_frequent_track)
            
    def select_track(self, track_name):
        """Select a specific track by name."""
        index = self.track_combo.findText(track_name)
        if index >= 0:
            self.track_combo.setCurrentIndex(index)
            
    def get_track_path(self, track_name):
        """Get file path for track name."""
        index = self.track_combo.findText(track_name)
        if index >= 0:
            return self.track_combo.itemData(index)
        return None
        
    def get_track_names(self):
        """Get list of available track names."""
        tracks = []
        for i in range(self.track_combo.count()):
            if i > 0:
                tracks.append(self.track_combo.itemText(i))
        return tracks
        
    def get_selected_track(self):
        """Get currently selected track path."""
        if self.toggle_button.isChecked():
            return self.track_combo.currentData()
        return None
        
    def get_volume(self):
        """Get current volume."""
        return self.volume_slider.value() if self.toggle_button.isChecked() else 0
        
    def get_offset(self):
        """Get current offset."""
        return self.offset_spin.value() if self.toggle_button.isChecked() else 0.0
        
    def set_music_enabled(self, enabled):
        """Set music enabled state."""
        self.toggle_button.setChecked(enabled)
        
    def is_music_enabled(self):
        """Check if music is enabled."""
