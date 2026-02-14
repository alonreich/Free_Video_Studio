import os
from PyQt5.QtWidgets import QDialog, QApplication, QMessageBox
from PyQt5.QtCore import Qt
from utilities.merger_music_offset_dialog import MergerMusicOffsetDialog
try:
    import vlc as _vlc_mod
except Exception:
    _vlc_mod = None

class MusicDialogHandler:
    def __init__(self, parent):
        self.parent = parent
        self.logger = parent.logger

    def _ensure_vlc_instance(self) -> bool:
        if getattr(self.parent, "vlc_instance", None) is not None:
            return True
        if _vlc_mod is None:
            return False
        try:
            bin_dir = getattr(self.parent, "bin_dir", "") or ""
            vlc_args = ['--no-xlib', '--no-video-title-show', '--no-plugins-cache']
            plugin_path = os.path.join(bin_dir, "plugins")
            if plugin_path and os.path.exists(plugin_path):
                vlc_args.append(f"--plugin-path={plugin_path.replace('\\', '/')}")
                os.environ["VLC_PLUGIN_PATH"] = plugin_path
            if bin_dir and os.path.isdir(bin_dir):
                os.environ["PATH"] = bin_dir + os.pathsep + os.environ.get("PATH", "")
            self.parent.vlc_instance = _vlc_mod.Instance(vlc_args)
            return self.parent.vlc_instance is not None
        except Exception:
            self.parent.vlc_instance = None
            return False

    def open_music_wizard(self):
        from utilities.merger_music_wizard import MergerMusicWizard
        if not self._ensure_vlc_instance():
            self.logger.warning("WIZARD: VLC initialization failed. Preview will be unavailable.")
        total_sec = self.parent.estimate_total_duration_seconds()
        if total_sec <= 0:
            QMessageBox.warning(self.parent, "No Videos", "Please add at least one video first!")
            return
        mp3_dir = os.path.join(self.parent.base_dir, "mp3")
        wizard = MergerMusicWizard(self.parent, self.parent.vlc_instance, self.parent.bin_dir, mp3_dir, total_sec)
        if wizard.exec_() == QDialog.Accepted:
            if hasattr(self.parent, "unified_music_widget"):
                m_vol = wizard.music_vol_slider.value()
                v_vol = wizard.video_vol_slider.value()
                self.parent.unified_music_widget.set_wizard_tracks(wizard.selected_tracks, music_vol=m_vol, video_vol=v_vol)
                self.parent.set_status_message("✅ Music selection applied!", "color: #43b581; font-weight: bold;", 3000, force=True)

    def show_music_offset_dialog(self, path):
        if not self._ensure_vlc_instance():
            QMessageBox.warning(self.parent, "Preview unavailable", "Could not initialize VLC audio preview engine.")
        initial_offset = self.parent.unified_music_widget.get_offset()
        dlg = MergerMusicOffsetDialog(self.parent, self.parent.vlc_instance, path, initial_offset, self.parent.bin_dir)
        dlg.setWindowModality(Qt.ApplicationModal)
        saved_geo = self.parent.config_manager.config.get("music_dialog_geometry")
        if saved_geo and len(saved_geo) == 4:
            try: dlg.setGeometry(*saved_geo)
            except Exception: pass
        if dlg.exec_() == QDialog.Accepted:
            try:
                self.parent.unified_music_widget.set_primary_offset(float(dlg.selected_offset))
            except Exception as ex:
                self.logger.error(f"Failed applying selected offset: {ex}")
        g = dlg.geometry()
        cfg = dict(self.parent.config_manager.config)
        cfg["music_dialog_geometry"] = [g.x(), g.y(), g.width(), g.height()]
        self.parent.config_manager.save_config(cfg)
