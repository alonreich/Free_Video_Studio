import os
import tempfile
import json
from PyQt5.QtCore import QTimer

def cleanup_temp_snapshots():
    """Removes temporary snapshot files from the temp directory."""
    temp_dir = tempfile.gettempdir()
    for ext in [".png", ".jpg", ".jpeg"]:
        garbage_path = os.path.join(temp_dir, f"snapshot{ext}")
        if os.path.exists(garbage_path):
            try:
                os.remove(garbage_path)
            except OSError as e:
                print(f"Error removing temp file {garbage_path}: {e}")

class PersistentWindowMixin:
    """A mixin to provide common window geometry persistence."""

    def setup_persistence(self, config_path, settings_key, default_geo, title_info_provider):
        self.config_path = config_path
        self.settings_key = settings_key
        self.default_geo = default_geo
        self.title_info_provider = title_info_provider
        self._save_timer = QTimer(self)
        self._save_timer.setSingleShot(True)
        self._save_timer.setInterval(500)
        self._save_timer.timeout.connect(self.save_geometry)
        self.load_geometry()

    def load_geometry(self):
        try:
            with open(self.config_path, 'r') as f:
                settings = json.load(f)
            geom = settings.get(self.settings_key)
            if geom:
                self.move(geom['x'], geom['y'])
                self.resize(geom.get('w', self.default_geo.get('w')), 
                           geom.get('h', self.default_geo.get('h')))
            else:
                self._apply_default_center()
            if self.settings_key == 'window_geometry' and 'last_directory' in settings:
                self.last_dir = settings['last_directory']
            self.update_title()
            return
        except (FileNotFoundError, json.JSONDecodeError):
            self._apply_default_center()
            self.update_title()

    def _apply_default_center(self):
        """Centers window with 100px padding on the current screen."""
        screen_geo = QApplication.desktop().screenGeometry()
        avail_w = screen_geo.width() - 200
        avail_h = screen_geo.height() - 200
        w = max(self.default_geo['w'], min(avail_w, 1600))
        h = max(self.default_geo['h'], min(avail_h, 900))
        x = screen_geo.x() + (screen_geo.width() - w) // 2
        y = screen_geo.y() + (screen_geo.height() - h) // 2
        self.move(x, y)
        self.resize(w, h)

    def save_geometry(self):
        config_dir = os.path.dirname(self.config_path)
        if not os.path.exists(config_dir):
            os.makedirs(config_dir, exist_ok=True)
        try:
            with open(self.config_path, 'r') as f:
                settings = json.load(f)
        except (FileNotFoundError, json.JSONDecodeError):
            settings = {}
        geo = self.frameGeometry()
        settings[self.settings_key] = {'x': geo.x(),'y': geo.y(),'w': self.width(),'h': self.height()}
        if self.settings_key == 'window_geometry' and hasattr(self, 'last_dir') and self.last_dir:
            settings['last_directory'] = self.last_dir
        try:
            with open(self.config_path, 'w') as f:
                json.dump(settings, f, indent=4)
        except Exception as e:
            print(f"Error saving settings: {e}")

    def update_title(self):
        if self.title_info_provider:
            self.setWindowTitle(self.title_info_provider())

    def moveEvent(self, event):
        super().moveEvent(event)
        self.update_title()
        self._save_timer.start()

    def resizeEvent(self, event):
        super().resizeEvent(event)
        self.update_title()
        self._save_timer.start()

    def closeEvent(self, event):
        self.save_geometry()
        super().closeEvent(event)
