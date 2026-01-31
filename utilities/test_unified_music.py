"""
Test script for UnifiedMusicWidget.
Run this to verify the unified music widget works correctly.
"""

import sys
import os
from PyQt5.QtWidgets import QApplication, QMainWindow, QVBoxLayout, QWidget
from PyQt5.QtCore import Qt
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from merger_unified_music_widget import UnifiedMusicWidget

class TestWindow(QMainWindow):
    def __init__(self):
        super().__init__()
        self.setWindowTitle("Unified Music Widget Test")
        self.resize(800, 400)
        central_widget = QWidget()
        self.setCentralWidget(central_widget)
        layout = QVBoxLayout(central_widget)
        layout.setContentsMargins(20, 20, 20, 20)
        layout.setSpacing(20)
        self.music_widget = UnifiedMusicWidget(self)
        layout.addWidget(self.music_widget)
        self.setup_test_tracks()
        self.music_widget.music_toggled.connect(self.on_music_toggled)
        self.music_widget.track_selected.connect(self.on_track_selected)
        self.music_widget.volume_changed.connect(self.on_volume_changed)
        self.music_widget.offset_changed.connect(self.on_offset_changed)
        self.music_widget.advanced_requested.connect(self.on_advanced_requested)

        from PyQt5.QtWidgets import QLabel
        self.status_label = QLabel("Ready. Click the Music button to test.")
        self.status_label.setAlignment(Qt.AlignCenter)
        layout.addWidget(self.status_label)
        
    def setup_test_tracks(self):
        """Setup test tracks for demonstration."""
        test_mp3_folder = os.path.join(os.path.dirname(__file__), "test_mp3")
        os.makedirs(test_mp3_folder, exist_ok=True)
        test_tracks = [
            "test_track_1.mp3",
            "test_track_2.mp3", 
            "test_track_3.mp3",
            "background_music.mp3",
            "epic_soundtrack.mp3"
        ]
        for track in test_tracks:
            track_path = os.path.join(test_mp3_folder, track)
            if not os.path.exists(track_path):
                with open(track_path, 'wb') as f:
                    f.write(b'')
        self.music_widget.load_tracks(test_mp3_folder)
        
    def on_music_toggled(self, enabled):
        self.status_label.setText(f"Music toggled: {'ON' if enabled else 'OFF'}")
        print(f"Music toggled: {enabled}")
        
    def on_track_selected(self, track_path):
        track_name = os.path.basename(track_path) if track_path else "No music"
        self.status_label.setText(f"Track selected: {track_name}")
        print(f"Track selected: {track_path}")
        
    def on_volume_changed(self, volume):
        self.status_label.setText(f"Volume changed: {volume}%")
        print(f"Volume changed: {volume}%")
        
    def on_offset_changed(self, offset):
        self.status_label.setText(f"Offset changed: {offset}s")
        print(f"Offset changed: {offset}s")
        
    def on_advanced_requested(self):
        self.status_label.setText("Advanced dialog requested")
        print("Advanced dialog requested")

        from PyQt5.QtWidgets import QMessageBox
        QMessageBox.information(self, "Advanced", "Advanced music dialog would open here.")

def main():
    app = QApplication(sys.argv)
    window = TestWindow()
    window.show()
    sys.exit(app.exec_())
if __name__ == "__main__":
    main()