import os
import tempfile
import json
from PyQt5.QtCore import QTimer
from PyQt5.QtWidgets import QApplication

def get_snapshot_dir():
    """Returns a dedicated directory for temporary snapshots."""
    temp_dir = os.path.join(tempfile.gettempdir(), "FVS_Snapshots")
    try:
        os.makedirs(temp_dir, exist_ok=True)
    except Exception:
        pass
    return temp_dir

def cleanup_temp_snapshots():
    """Removes temporary snapshot files from the dedicated directory."""

    import glob
    snapshot_dir = get_snapshot_dir()
    patterns = ["*.png", "*.jpg", "*.jpeg"]
    for pattern in patterns:
        for garbage_path in glob.glob(os.path.join(snapshot_dir, pattern)):
            try:
                if os.path.isfile(garbage_path):
                    os.remove(garbage_path)
            except OSError:
                pass

def cleanup_old_backups(max_age_seconds: int = 86400):
    """[FIX #4] Purges transaction backups and app state files older than max_age."""

    import time
    import glob
    now = time.time()
    temp_dir = tempfile.gettempdir()
    patterns = ["*.backup.*"]
    for pattern in patterns:
        for backup_path in glob.glob(os.path.join(temp_dir, pattern)):
            try:
                if (now - os.path.getmtime(backup_path)) > max_age_seconds:
                    os.remove(backup_path)
            except Exception:
                pass
    state_dir = os.path.join(temp_dir, 'fortnite_video_state')
    if os.path.exists(state_dir):
        for state_file in glob.glob(os.path.join(state_dir, "app_state_*.json")):
            try:
                if (now - os.path.getmtime(state_file)) > max_age_seconds:
                    os.remove(state_file)
            except Exception:
                pass

class PersistentWindowMixin:
    """A mixin to provide common window geometry persistence."""

    def setup_persistence(self, config_path, settings_key, default_geo, title_info_provider, extra_data_provider=None, config_manager=None):
        self.config_path = config_path
        self.settings_key = settings_key
        self.default_geo = default_geo
        self.title_info_provider = title_info_provider
        self.extra_data_provider = extra_data_provider
        self.external_config_manager = config_manager
        self._loading_persistence = True
        self._save_timer = QTimer(self)
        self._save_timer.setSingleShot(True)
        self._save_timer.setInterval(1000)
        self._save_timer.timeout.connect(self.save_geometry)
        self.load_geometry()
        self._loading_persistence = False

    def load_geometry(self):
        try:
            if self.external_config_manager:
                manager = self.external_config_manager
            else:
                from config_manager import get_config_manager
                manager = get_config_manager(self.config_path)
            settings = manager.load_config()
            geom = settings.get(self.settings_key)
            app_instance = QApplication.instance()
            screen_geo = app_instance.primaryScreen().availableGeometry() if app_instance else None
            if geom and 'x' in geom and 'y' in geom:
                def_w = self.default_geo.get('w', 1200)
                def_h = self.default_geo.get('h', 800)
                w = geom.get('w', def_w)
                h = geom.get('h', def_h)
                x = geom['x']
                y = geom['y']
                if screen_geo:
                    if not screen_geo.intersects(QRect(x, y, w, h)):
                        self._apply_default_center()
                        return
                    w = min(w, screen_geo.width())
                    h = min(h, screen_geo.height())
                self.move(x, y)
                self.resize(w, h)
            else:
                self._apply_default_center()
            if 'last_directory' in settings:
                self.last_dir = settings['last_directory']
            self.update_title()
        except Exception as e:
            if hasattr(self, 'logger') and self.logger:
                self.logger.error(f"Persistence load failed: {e}")
            self._apply_default_center()
            self.update_title()

    def _apply_default_center(self):
        """Centers window based on default_geo, ensuring it fits on the current screen."""
        app_instance = QApplication.instance()
        if app_instance is None:
            return
        screen_geo = app_instance.primaryScreen().availableGeometry()
        w = self.default_geo.get('w', 1200)
        h = self.default_geo.get('h', 800)
        w = min(w, screen_geo.width())
        h = min(h, int(screen_geo.height() * 0.98))
        x = screen_geo.x() + (screen_geo.width() - w) // 2
        y = max(screen_geo.top(), screen_geo.y() + (screen_geo.height() - h) // 2 - 25)
        self.resize(w, h)
        self.move(x, y)

    def save_geometry(self):
        if getattr(self, '_loading_persistence', False):
            return
        try:
            if self.external_config_manager:
                manager = self.external_config_manager
            else:
                from config_manager import get_config_manager
                manager = get_config_manager(self.config_path)
            settings = manager.load_config()
            p = self.pos()
            settings[self.settings_key] = {
                'x': p.x(), 
                'y': p.y(), 
                'w': self.width(), 
                'h': self.height()
            }
            if hasattr(self, 'last_dir') and self.last_dir:
                settings['last_directory'] = self.last_dir
            if self.extra_data_provider:
                try:
                    extra_data = self.extra_data_provider()
                    if isinstance(extra_data, dict):
                        settings.update(extra_data)
                except Exception:
                    pass
            manager.save_config(settings)
        except Exception as e:
            pass

    def update_title(self):
        if hasattr(self, 'title_info_provider') and self.title_info_provider:
            try:
                self.setWindowTitle(self.title_info_provider())
            except:
                pass

    def handle_persistence_event(self):
        """Called by resize/move events to trigger save."""
        if not getattr(self, '_loading_persistence', False):
            self.update_title()
            if hasattr(self, '_save_timer'):
                self._save_timer.start()

    def moveEvent(self, event):
        self.handle_persistence_event()

    def resizeEvent(self, event):
        self.handle_persistence_event()

    def closeEvent(self, event):
        self.save_geometry()

