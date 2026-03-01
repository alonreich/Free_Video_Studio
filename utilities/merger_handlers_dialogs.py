from PyQt5.QtWidgets import QDialog, QVBoxLayout, QLabel, QGridLayout, QPushButton, QApplication, QSizePolicy, QMessageBox
from PyQt5.QtCore import QUrl, Qt, QPropertyAnimation, QTimer
from PyQt5.QtGui import QDesktopServices
from pathlib import Path
import os
import sys
import subprocess
from utilities.merger_utils import _human

class MergerHandlersDialogsMixin:
    def _dialog_button_style(self, color: str, pressed: str, *, font_size: int = 12) -> str:
        return f"""
            QPushButton {{
                background: qlineargradient(x1:0, y1:0, x2:0, y2:1, stop:0 {color}, stop:1 {pressed});
                color: white;
                font-weight: bold;
                font-family: Arial;
                font-size: {font_size}px;
                border-radius: 8px;
                border: 1px solid rgba(0,0,0,0.45);
                padding: 0px;
                text-align: center;
                min-width: 180px;
                max-width: 180px;
                min-height: 45px;
                max-height: 45px;
            }}
            QPushButton:hover {{ border: 1px solid #7DD3FC; }}
            QPushButton:pressed {{
                background: qlineargradient(x1:0, y1:0, x2:0, y2:1, stop:0 {pressed}, stop:1 {color});
            }}
        """

    def open_folder(self, path: str):
        folder_path = os.path.abspath(path)
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
            try:
                from PyQt5.QtWidgets import QMessageBox
                QMessageBox.warning(
                    self.parent,
                    "Error",
                    f"Failed to open folder. Please navigate to {folder_path} manually. Error: {e}",
                )
            except Exception:
                pass

    def open_music_wizard(self):
        from utilities.merger_music_wizard import MergerMusicWizard
        import os
        total_sec = self.parent.estimate_total_duration_seconds()
        if total_sec <= 0:
            QMessageBox.warning(self.parent, "No Videos", "Please add at least one video first so we know how much music you need!")
            return
        mp3_dir = os.path.join(self.parent.base_dir, "mp3")
        wizard = MergerMusicWizard(
            self.parent, 
            getattr(self.parent, "player", None), 
            self.parent.bin_dir, 
            mp3_dir, 
            total_sec
        )
        if wizard.exec_() == QDialog.Accepted:
            if hasattr(self.parent, "unified_music_widget"):
                m_vol = wizard.music_vol_slider.value()
                v_vol = wizard.video_vol_slider.value()
                self.parent.unified_music_widget.set_wizard_tracks(wizard.selected_tracks, music_vol=m_vol, video_vol=v_vol)
                if hasattr(self.parent, "set_status_message"):
                    self.parent.set_status_message("✅ Music selection applied!", "color: #43b581; font-weight: bold;", 3000, force=True)

    def show_success_dialog(self, output_path: str):
        """Displays success dialog using the synced ultra-polished layout/feel from main app."""

        class FinishedDialog(QDialog):
            def closeEvent(self, e):
                self.accept()
        dialog = FinishedDialog(self.parent)
        dialog.setWindowTitle("Done! Video Processed Successfully!")
        dialog.setModal(True)
        dlg_w = 800
        dlg_h = 460
        dialog.setFixedSize(dlg_w, dlg_h)
        screen_geo = QApplication.primaryScreen().availableGeometry()
        dialog.move(
            screen_geo.center().x() - dlg_w // 2,
            screen_geo.center().y() - dlg_h // 2
        )
        dialog.setWindowFlags(dialog.windowFlags() | Qt.WindowStaysOnTopHint)
        dialog.show()
        dialog.setWindowFlags(dialog.windowFlags() & ~Qt.WindowStaysOnTopHint)
        dialog.show()
        dialog.raise_()
        dialog.activateWindow()
        layout = QVBoxLayout(dialog)
        layout.setContentsMargins(30, 30, 30, 30)
        layout.setSpacing(20)
        label = QLabel(f"File successfully saved to:\n{output_path}")
        label.setStyleSheet("font-size: 16px; font-weight: bold; color: #7DD3FC;")
        label.setWordWrap(True)
        label.setAlignment(Qt.AlignCenter)
        layout.addWidget(label)
        grid = QGridLayout()
        grid.setHorizontalSpacing(54)
        grid.setVerticalSpacing(44)
        grid.setContentsMargins(20, 20, 20, 20)
        whatsapp_button = QPushButton("✆  WHATSAPP SHARE  ✆")
        whatsapp_button.setStyleSheet(self._dialog_button_style("#3CA557", "#2B7D40", font_size=12))
        whatsapp_button.clicked.connect(lambda: (QDesktopServices.openUrl(QUrl("https://web.whatsapp.com")), dialog.accept()))
        open_folder_button = QPushButton("OPEN FOLDER")
        open_folder_button.setStyleSheet(self._dialog_button_style("#6c5f9e", "#4E4476", font_size=12))
        open_folder_button.clicked.connect(lambda: (dialog.accept(), self.open_folder(os.path.dirname(output_path))))
        new_file_button = QPushButton("📂  UPLOAD NEW  📂")
        new_file_button.setStyleSheet(self._dialog_button_style("#4a90e2", "#2D6DB8", font_size=12))
        new_file_button.clicked.connect(dialog.reject)
        done_button = QPushButton("DONE")
        done_button.setStyleSheet(self._dialog_button_style("#821e1e", "#5D1515", font_size=12))
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
        finished_button = QPushButton("EXIT APP!")
        finished_button.setStyleSheet(self._dialog_button_style("#c90e0e", "#950808", font_size=12))
        finished_button.clicked.connect(_hard_exit) 
        for b in [whatsapp_button, open_folder_button, new_file_button, done_button, finished_button]:
            b.setFixedSize(180, 45)
            b.setSizePolicy(QSizePolicy.Fixed, QSizePolicy.Fixed)
            b.setCursor(Qt.PointingHandCursor)
        grid.addWidget(whatsapp_button, 0, 0, alignment=Qt.AlignCenter)
        grid.addWidget(open_folder_button, 0, 1, alignment=Qt.AlignCenter)
        grid.addWidget(new_file_button, 0, 2, alignment=Qt.AlignCenter)
        grid.addWidget(done_button, 1, 0, 1, 3, alignment=Qt.AlignCenter)
        grid.addWidget(finished_button, 2, 0, 1, 3, alignment=Qt.AlignCenter)
        layout.addLayout(grid)
        fade_anim = QPropertyAnimation(dialog, b"windowOpacity")
        fade_anim.setDuration(2000)
        fade_anim.setStartValue(1.0)
        fade_anim.setKeyValueAt(0.5, 0.4)
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
