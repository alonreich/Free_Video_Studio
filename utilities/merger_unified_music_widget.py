"""
Unified Music Widget for improved UX flow.
Implements a single "Music" button with dropdown arrow and integrated controls.
"""

from PyQt5.QtWidgets import (
    QWidget, QPushButton, QVBoxLayout, QHBoxLayout, QLabel, 
    QComboBox, QSlider, QDoubleSpinBox, QFrame,
    QMenu, QAction, QToolButton, QToolTip, QFileDialog
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
        self._current_track = ""
        self.setup_ui()
        self.setup_connections()
        self.load_track_history()
        
    def load_track_history(self):
        """Load track usage history from config."""
        self._track_settings = {}
        self._last_used_track = ""
        # Could integrate with parent config here
        
    def setup_ui(self):
        """Setup the unified music widget UI."""
        main_layout = QVBoxLayout(self)
        main_layout.setContentsMargins(0, 0, 0, 0)
        main_layout.setSpacing(0)
        button_row = QHBoxLayout()
        button_row.setContentsMargins(0, 0, 0, 0)
        button_row.setSpacing(2)
        
        # Main Toggle Button
        self.toggle_button = QPushButton("Music: Off")
        self.toggle_button.setObjectName("musicToggleButton")
        self.toggle_button.setCheckable(True)
        self.toggle_button.setChecked(False)
        self.toggle_button.setFixedSize(120, 34)
        self.toggle_button.setCursor(Qt.PointingHandCursor)
        self.toggle_button.setToolTip("Toggle background music on/off")
        
        # Dropdown Arrow
        self.dropdown_button = QToolButton()
        self.dropdown_button.setText("▼")
        self.dropdown_button.setFixedSize(24, 34)
        self.dropdown_button.setCursor(Qt.PointingHandCursor)
        self.dropdown_button.setToolTip("Show music settings")
        
        button_row.addWidget(self.toggle_button)
        button_row.addWidget(self.dropdown_button)
        
        # Dropdown Panel
        self.dropdown_panel = QFrame()
        self.dropdown_panel.setObjectName("musicDropdownPanel")
        self.dropdown_panel.setFrameStyle(QFrame.StyledPanel | QFrame.Raised)
        self.dropdown_panel.setVisible(False)
        self.dropdown_panel.setMaximumHeight(300)
        
        panel_layout = QVBoxLayout(self.dropdown_panel)
        panel_layout.setContentsMargins(12, 12, 12, 12)
        panel_layout.setSpacing(8)
        
        # Track Selection
        track_row = QHBoxLayout()
        track_row.setSpacing(8)
        track_label = QLabel("Track:")
        track_label.setFixedWidth(50)
        self.track_combo = QComboBox()
        self.track_combo.setObjectName("musicTrackCombo")
        self.track_combo.setMinimumWidth(250)
        self.track_combo.setCursor(Qt.PointingHandCursor)
        track_row.addWidget(track_label)
        track_row.addWidget(self.track_combo, 1)
        
        # Volume Control
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
        
        # Offset Control
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
        
        # Advanced Button
        self.advanced_button = QPushButton("Preview / Edit")
        self.advanced_button.setObjectName("musicAdvancedButton")
        self.advanced_button.setFixedSize(120, 24)
        self.advanced_button.setCursor(Qt.PointingHandCursor)
        self.advanced_button.setToolTip("Preview track or select specific segment")
        
        panel_layout.addLayout(track_row)
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
        self.volume_slider.valueChanged.connect(self.on_volume_changed)
        self.offset_spin.valueChanged.connect(self.on_offset_changed)
        self.advanced_button.clicked.connect(self.advanced_requested.emit)
        
    def on_toggle_changed(self, checked):
        self.music_toggled.emit(checked)
        self._set_controls_enabled(checked)
        
        # Logic Fix #19: Update button text
        self.update_button_text()
        
        # UX Fix #4: Don't auto-close dropdown if we just enabled it
        if checked and not self._is_expanded:
            self.toggle_dropdown()
            
    def update_button_text(self):
        if self.toggle_button.isChecked():
            track = self.get_track_name_safe()
            self.toggle_button.setText(f"♪ {track[:10]}..." if len(track) > 10 else f"♪ {track}")
        else:
            self.toggle_button.setText("Music: Off")

    def toggle_dropdown(self):
        self._is_expanded = not self._is_expanded
        self.dropdown_panel.setVisible(self._is_expanded)
        self.dropdown_button.setText("▲" if self._is_expanded else "▼")

    def _set_controls_enabled(self, enabled: bool):
        self.track_combo.setEnabled(enabled)
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

    def browse_music_file(self):
        # Fix #10: Unvalidated Music Files - restrict extensions
        path, _ = QFileDialog.getOpenFileName(
            self, "Select Music File", str(Path.home() / "Music"), 
            "Audio Files (*.mp3 *.wav *.aac *.m4a *.flac *.ogg)"
        )
        if path:
            filename = os.path.basename(path)
            # Insert before "Browse..."
            idx = self.track_combo.count() - 1
            self.track_combo.insertItem(idx, filename, path)
            self.track_combo.setCurrentIndex(idx)
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

    def get_selected_track(self):
        if self.toggle_button.isChecked():
            data = self.track_combo.currentData()
            return data if data and data != "BROWSE_ACTION" else None
        return None