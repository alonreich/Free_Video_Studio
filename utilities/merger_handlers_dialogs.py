from PyQt5.QtWidgets import QDialog, QVBoxLayout, QLabel, QGridLayout, QPushButton, QApplication
from PyQt5.QtCore import QUrl, Qt, QPropertyAnimation
from PyQt5.QtGui import QDesktopServices
from pathlib import Path
import os
import sys
import subprocess
from utilities.merger_utils import _human

class MergerHandlersDialogsMixin:
    def _dialog_button_style(self, color: str, pressed: str, *, font_size: int = 11) -> str:
        return f"""
            QPushButton {{
                background: qlineargradient(x1:0, y1:0, x2:0, y2:1, stop:0 {color}, stop:1 {pressed});
                color: white;
                font-weight: bold;
                font-size: {font_size}px;
                border-radius: 8px;
                border: 1px solid rgba(0,0,0,0.45);
                padding: 8px 16px;
            }}
            QPushButton:hover {{ border: 1px solid #7DD3FC; }}
            QPushButton:pressed {{
                background: qlineargradient(x1:0, y1:0, x2:0, y2:1, stop:0 {pressed}, stop:1 {color});
            }}
        """

    def open_folder(self, path: str):
        folder_path = str(Path(path))
        if not folder_path or not os.path.isdir(folder_path):
            self.logger.warning("OPEN_FOLDER: Path is not a directory or does not exist: %s", folder_path)
            return
        try:
            if os.name == "nt":
                os.startfile(folder_path, "explore")
            elif sys.platform == "darwin":
                subprocess.Popen(["open", folder_path])
            else:
                subprocess.Popen(["xdg-open", folder_path])
            self.logger.info("OPEN_FOLDER: Opened %s", folder_path)
        except Exception as e:
            self.logger.error("OPEN_FOLDER: Failed to open folder %s | Error: %s", folder_path, e)
            if hasattr(self.parent, "set_status_message"):
                self.parent.set_status_message("Failed to open output folder", "color: #ff6b6b; font-weight: bold;", 3000)
            try:
                from PyQt5.QtWidgets import QMessageBox
                QMessageBox.warning(
                    self.parent,
                    "Couldn't open folder",
                    f"Could not open this folder automatically:\n{folder_path}\n\n"
                    "Please copy this path and open it manually.",
                )
            except Exception:
                pass

    def open_music_wizard(self):
        from utilities.merger_music_wizard import MergerMusicWizard
        import os
        total_sec = self.parent.estimate_total_duration_seconds()
        if total_sec <= 0:
            from PyQt5.QtWidgets import QMessageBox
            QMessageBox.warning(self.parent, "No Videos", "Please add at least one video first so we know how much music you need!")
            return
        mp3_dir = os.path.join(self.parent.base_dir, "mp3")
        wizard = MergerMusicWizard(
            self.parent, 
            self.parent.vlc_instance, 
            self.parent.bin_dir, 
            mp3_dir, 
            total_sec
        )
        if wizard.exec_() == QDialog.Accepted:
            if hasattr(self.parent, "unified_music_widget"):
                m_vol = wizard.music_vol_slider.value()
                v_vol = wizard.video_vol_slider.value()
                self.parent.unified_music_widget.set_wizard_tracks(wizard.selected_tracks, music_vol=m_vol, video_vol=v_vol)
                self.parent.set_status_message("✅ Music selection applied!", "color: #43b581; font-weight: bold;", 3000, force=True)

    def show_success_dialog(self, output_path: str):
        """Displays success dialog using the legacy finished-popup layout/feel."""

        class FinishedDialog(QDialog):
            def closeEvent(self, e):
                self.accept()
        dialog = FinishedDialog(self.parent)
        dialog.setWindowTitle("Done! Video Processed Successfully!")
        dialog.setModal(True)
        btn_h = 58
        btn_w = 250
        button_size = (btn_w, btn_h)
        dlg_w = max(760, int(self.parent.width() * 0.5)) if hasattr(self.parent, "width") else 860
        dlg_h = 420
        dialog.resize(dlg_w, dlg_h)
        layout = QVBoxLayout(dialog)
        layout.setContentsMargins(30, 30, 30, 30)
        layout.setSpacing(20)
        label = QLabel(f"File successfully saved to:\n{output_path}")
        label.setStyleSheet("font-size: 16px; font-weight: bold; color: #7DD3FC;")
        label.setWordWrap(True)
        label.setAlignment(Qt.AlignCenter)
        layout.addWidget(label)
        grid = QGridLayout()
        grid.setSpacing(24)
        grid.setContentsMargins(20, 20, 20, 20)

        def _open_whatsapp():
            try:
                QDesktopServices.openUrl(QUrl("https://web.whatsapp.com"))
            except Exception as e:
                self.logger.error("Failed to open WhatsApp Web: %s", e)
        whatsapp_button = QPushButton("✆   Share via Whatsapp   ✆")
        whatsapp_button.setFixedSize(*button_size)
        whatsapp_button.setStyleSheet(self._dialog_button_style("#3CA557", "#2B7D40", font_size=10))
        whatsapp_button.setCursor(Qt.PointingHandCursor)
        whatsapp_button.clicked.connect(lambda: (_open_whatsapp(), dialog.accept()))
        open_folder_button = QPushButton("Open Output Folder")
        open_folder_button.setFixedSize(*button_size)
        open_folder_button.setStyleSheet(self._dialog_button_style("#6c5f9e", "#4E4476"))
        open_folder_button.setCursor(Qt.PointingHandCursor)
        open_folder_button.clicked.connect(lambda: (dialog.accept(), self.open_folder(os.path.dirname(output_path))))
        new_file_button = QPushButton("📂   Merge More Videos   📂")
        new_file_button.setFixedSize(*button_size)
        new_file_button.setStyleSheet(self._dialog_button_style("#4a90e2", "#2D6DB8", font_size=10))
        new_file_button.setCursor(Qt.PointingHandCursor)
        new_file_button.clicked.connect(dialog.reject)
        done_button = QPushButton("Done")
        done_button.setFixedSize(*button_size)
        done_button.setStyleSheet(self._dialog_button_style("#821e1e", "#5D1515"))
        done_button.setCursor(Qt.PointingHandCursor)
        done_button.clicked.connect(dialog.accept)

        def _hard_exit():
            dialog.accept()
            try:
                if hasattr(self.parent, "close"):
                    self.parent.close()
            except: pass
            try:
                from utilities.merger_system import MergerProcessManager
                MergerProcessManager.kill_orphans()
            except: pass
            QApplication.instance().quit()
            QTimer.singleShot(500, lambda: os._exit(0))
        finished_button = QPushButton("Close The App! (Exit)")
        finished_button.setFixedSize(*button_size)
        finished_button.setStyleSheet(self._dialog_button_style("#c90e0e", "#950808"))
        finished_button.setCursor(Qt.PointingHandCursor)
        finished_button.clicked.connect(_hard_exit)
        grid.addWidget(whatsapp_button, 0, 0, alignment=Qt.AlignCenter)
        grid.addWidget(open_folder_button, 0, 1, alignment=Qt.AlignCenter)
        grid.addWidget(new_file_button, 0, 2, alignment=Qt.AlignCenter)
        grid.addWidget(done_button, 1, 0, 1, 3, alignment=Qt.AlignCenter)
        grid.addWidget(finished_button, 2, 0, 1, 3, alignment=Qt.AlignCenter)
        layout.addLayout(grid)
        fade_anim = QPropertyAnimation(dialog, b"windowOpacity")
        fade_anim.setDuration(1200)
        fade_anim.setStartValue(1.0)
        fade_anim.setKeyValueAt(0.5, 0.75)
        fade_anim.setEndValue(1.0)
        fade_anim.setLoopCount(-1)
        fade_anim.start()
        dialog._fade_anim = fade_anim
        dialog.exec_()
        try:
            out_sz = Path(output_path).stat().st_size if output_path else 0
            self.logger.info("MERGE_DONE: output='%s' | size=%s", output_path, _human(out_sz))
        except Exception:
            pass
