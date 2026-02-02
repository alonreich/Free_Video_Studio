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

    def show_success_dialog(self, output_path):
        dialog = QDialog(self.parent)
        dialog.setWindowTitle("Done! Video Processed Successfully!")
        dialog.setModal(True)
        fm = dialog.fontMetrics()
        btn_h = int(max(58, fm.height() * 2 + 18) + 115)
        btn_w = 220
        dialog.setFixedSize(1200, 3 * btn_h + 80)
        layout = QVBoxLayout(dialog)
        label = QLabel(f"File saved to:\n{output_path}")
        layout.addWidget(label)
        grid = QGridLayout()
        grid.setHorizontalSpacing(150)
        grid.setVerticalSpacing(100)
        grid.setContentsMargins(30, 20, 30, 24)
        button_size = (btn_w, btn_h)

        def _open_whatsapp():
            try:
                QDesktopServices.openUrl(QUrl("https://web.whatsapp.com"))
            except Exception as e:
                self.logger.error("Failed to open WhatsApp Web: %s", e)
        whatsapp_button = QPushButton("\r\n✆   Share via Whatsapp   ✆\r\n")
        whatsapp_button.setFixedSize(*button_size)
        whatsapp_button.setStyleSheet("background-color: #328742; color: white;")
        whatsapp_button.setCursor(Qt.PointingHandCursor)
        whatsapp_button.clicked.connect(lambda: (_open_whatsapp(), dialog.accept(), QApplication.instance().quit()))
        open_folder_button = QPushButton("\r\nOpen Output Folder\r\n")
        open_folder_button.setFixedSize(*button_size)
        open_folder_button.setStyleSheet("background-color: #6c5f9e; color: white;")
        open_folder_button.setCursor(Qt.PointingHandCursor)
        open_folder_button.clicked.connect(lambda: (dialog.accept(), self.open_folder(os.path.dirname(output_path)), QApplication.instance().quit()))
        new_file_button = QPushButton("\r\n📂   Upload a New File   📂\r\n")
        new_file_button.setFixedSize(*button_size)
        new_file_button.setStyleSheet("background-color: #6c5f9e; color: white;")
        new_file_button.setCursor(Qt.PointingHandCursor)
        new_file_button.clicked.connect(dialog.reject)
        done_button = QPushButton("\r\nDone\r\n")
        done_button.setFixedSize(*button_size)
        done_button.setStyleSheet("background-color: #821e1e; color: white; padding: 8px 16px;")
        done_button.setCursor(Qt.PointingHandCursor)
        done_button.clicked.connect(dialog.accept)
        finished_button = QPushButton("Close The App!\r\n(Exit)")
        finished_button.setFixedSize(*button_size)
        finished_button.setStyleSheet("background-color: #c90e0e; color: white; padding: 8px 16px;")
        finished_button.setCursor(Qt.PointingHandCursor)
        finished_button.clicked.connect(lambda: (dialog.accept(), QApplication.instance().quit()))
        grid.addWidget(whatsapp_button, 0, 0, alignment=Qt.AlignCenter)
        grid.addWidget(open_folder_button, 0, 1, alignment=Qt.AlignCenter)
        grid.addWidget(new_file_button, 0, 2, alignment=Qt.AlignCenter)
        grid.addWidget(done_button, 1, 0, 1, 3, alignment=Qt.AlignCenter)
        grid.addWidget(finished_button, 2, 0, 1, 3, alignment=Qt.AlignCenter)
        layout.addLayout(grid)
        dialog.setLayout(layout)
        result = dialog.exec_()
        if result == QDialog.Rejected:
            self.add_videos()
        try:
            out_sz = Path(output_path).stat().st_size if output_path else 0
            self.logger.info("MERGE_DONE: output='%s' | size=%s", output_path, _human(out_sz))
        except Exception:
            pass