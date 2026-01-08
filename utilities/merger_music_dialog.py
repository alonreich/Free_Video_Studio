from PyQt5.QtWidgets import QDialog, QApplication
from PyQt5.QtCore import Qt
from ui.widgets.music_offset_dialog import MusicOffsetDialog

class MusicDialogHandler:

    def __init__(self, parent):
        self.parent = parent
        self.logger = parent.logger

    def show_music_offset_dialog(self, path):

        def _configure_dialog_player(vlc_player):
            try:
                vlc_player.audio_output_set('directsound')
                volume = self.parent.music_handler._music_eff()
                vlc_player.audio_set_volume(volume)
                return None
            except Exception as e:
                self.logger.error("Failed to patch VLC player with directsound/volume: %s", e)
                return None
        setattr(self.parent, "_vlc_setup_hook", _configure_dialog_player)
        import ui.widgets.music_offset_dialog as _mod_mdlg
        _orig_lead = getattr(_mod_mdlg, "PREVIEW_VISUAL_LEAD_MS", 0)
        try:
            _mod_mdlg.PREVIEW_VISUAL_LEAD_MS = 0
            initial_offset = self.parent.music_offset_input.value()
            parent_window = self.parent.window() if callable(getattr(self.parent, "window", None)) else self.parent
            dlg = MusicOffsetDialog(parent_window, self.parent.vlc_instance, path, initial_offset, self.parent.bin_dir, self.parent.config_manager)
            dlg.setWindowModality(Qt.ApplicationModal)
            saved_geo = self.parent._cfg.get("music_dialog_geometry")
            if saved_geo and len(saved_geo) == 4:
                try:
                    dlg.setGeometry(*saved_geo)
                except Exception:
                    pass
            dlg.show()
            dlg.raise_()
            dlg.activateWindow()
            QApplication.processEvents()
            res = dlg.exec_()
            g = dlg.geometry()
            self.parent._cfg["music_dialog_geometry"] = [g.x(), g.y(), g.width(), g.height()]
            self.parent.save_config()
            if res == QDialog.Accepted:
                self.parent.music_offset_input.setValue(dlg.selected_offset)
        except Exception as e:
            self.logger.exception("Failed to open MusicOffsetDialog: %s", e)
        finally:
            _mod_mdlg.PREVIEW_VISUAL_LEAD_MS = _orig_lead
            if hasattr(self.parent, "_vlc_setup_hook"):
                delattr(self.parent, "_vlc_setup_hook")