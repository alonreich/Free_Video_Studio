from PyQt5.QtWidgets import QDialog, QVBoxLayout, QLabel, QGridLayout, QPushButton, QApplication
from PyQt5.QtCore import QUrl, Qt
from PyQt5.QtGui import QDesktopServices
from pathlib import Path
import os
import sys
import subprocess
from utilities.merger_utils import _human

class MergerHandlersDialogsMixin:
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
        """Displays the high-production-value success dialog after a successful merge."""
        dialog = QDialog(self.parent)
        dialog.setWindowTitle("Done! Video Processed Successfully!")
        dialog.setModal(True)
        btn_h = 55
        btn_w = 250
        button_size = (btn_w, btn_h)
        dlg_w = 850
        dlg_h = 450
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
        grid.setSpacing(20)
        
        def _open_whatsapp():
            try:
                QDesktopServices.openUrl(QUrl("https://web.whatsapp.com"))
            except Exception as e:
                self.logger.error("Failed to open WhatsApp Web: %s", e)
        whatsapp_button = QPushButton("✆   Share via Whatsapp   ✆")
        whatsapp_button.setFixedSize(*button_size)
        whatsapp_button.setStyleSheet("background-color: #328742; color: white; font-weight: bold; border-radius: 8px;")
        whatsapp_button.setCursor(Qt.PointingHandCursor)
        whatsapp_button.clicked.connect(lambda: (_open_whatsapp(), dialog.accept()))
        open_folder_button = QPushButton("Open Output Folder")
        open_folder_button.setFixedSize(*button_size)
        open_folder_button.setStyleSheet("background-color: #6c5f9e; color: white; font-weight: bold; border-radius: 8px;")
        open_folder_button.setCursor(Qt.PointingHandCursor)
        open_folder_button.clicked.connect(lambda: (dialog.accept(), self.open_folder(os.path.dirname(output_path))))
        new_file_button = QPushButton("📂   Merge More Videos   📂")
        new_file_button.setFixedSize(*button_size)
        new_file_button.setStyleSheet("background-color: #3498db; color: white; font-weight: bold; border-radius: 8px;")
        new_file_button.setCursor(Qt.PointingHandCursor)
        new_file_button.clicked.connect(dialog.reject)
        done_button = QPushButton("Done")
        done_button.setFixedSize(dlg_w - 100, 50)
        done_button.setStyleSheet("background-color: #1b6d26; color: white; font-weight: bold; border-radius: 10px;")
        done_button.setCursor(Qt.PointingHandCursor)
        done_button.clicked.connect(dialog.accept)
        finished_button = QPushButton("Close The App! (Exit)")
        finished_button.setFixedSize(dlg_w - 100, 50)
        finished_button.setStyleSheet("background-color: #c90e0e; color: white; font-weight: bold; border-radius: 10px;")
        finished_button.setCursor(Qt.PointingHandCursor)
        finished_button.clicked.connect(lambda: (dialog.accept(), QApplication.instance().quit()))
        grid.addWidget(whatsapp_button, 0, 0, alignment=Qt.AlignCenter)
        grid.addWidget(open_folder_button, 0, 1, alignment=Qt.AlignCenter)
        grid.addWidget(new_file_button, 0, 2, alignment=Qt.AlignCenter)
        layout.addLayout(grid)
        layout.addWidget(done_button, 0, Qt.AlignCenter)
        layout.addWidget(finished_button, 0, Qt.AlignCenter)
        dialog.exec_()
        try:
            out_sz = Path(output_path).stat().st_size if output_path else 0
            self.logger.info("MERGE_DONE: output='%s' | size=%s", output_path, _human(out_sz))
        except Exception:
            pass
