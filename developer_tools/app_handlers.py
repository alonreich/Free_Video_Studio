from config import CROP_APP_STYLESHEET
import os
import sys
import subprocess
import logging
from PyQt5.QtWidgets import QApplication, QFileDialog
from PyQt5.QtGui import QPixmap
from PyQt5.QtCore import QRect, QTimer
from PyQt5.Qt import QStyle
from portrait_window import PortraitWindow
from utils import cleanup_temp_snapshots

class CropAppHandlers:

    def connect_signals(self):
        self.play_pause_button.clicked.connect(self.play_pause)
        self.open_button.clicked.connect(self.open_file)
        self.snapshot_button.clicked.connect(self.take_snapshot)
        self.back_button.clicked.connect(self.show_video_view)
        self.send_crop_button.clicked.connect(self.trigger_portrait_add)
        self.reset_state_button.clicked.connect(self.reset_state)
        self.position_slider.sliderMoved.connect(self.set_position)

    def get_title_info(self):
        monitor_id = QApplication.desktop().screenNumber(self) + 1
        geo = self.frameGeometry()
        return (f"{self.base_title}          "
                f"Monitor: {monitor_id}  |  "
                f"Pos: {geo.x()},{geo.y()}  |  "
                f"Size: {self.width()}x{self.height()}")

    def reset_state(self):
        self.logger.info("Resetting application state.")
        if self.portrait_window:
            self.portrait_window.close()
            self.portrait_window = None
        self.media_processor.stop()
        self.media_processor.set_media_to_null()
        self.play_pause_button.setEnabled(False)
        self.play_pause_button.setText("Play")
        self.play_pause_button.setIcon(self.style().standardIcon(QStyle.SP_MediaPlay))
        self.snapshot_button.setEnabled(False)
        self.snapshot_button.setText("Begin Crops")
        self.send_crop_button.setVisible(False)
        self.position_slider.setValue(0)
        self.position_slider.setEnabled(False)
        self.draw_widget.clear_selection()
        self.draw_widget.setImage(None) 
        self.view_stack.setCurrentWidget(self.video_frame)
        self.coordinates_label.setText("Crop coordinates will be shown here")
        cleanup_temp_snapshots()
        self.open_button.setFocus()

    def play_pause(self):
        self.logger.info("Play/Pause button clicked.")
        self.media_processor.play_pause()
        QTimer.singleShot(50, self.update_play_pause_button)

    def update_play_pause_button(self):
        if self.media_processor.is_playing():
            self.play_pause_button.setText("Pause")
            self.play_pause_button.setIcon(self.style().standardIcon(QStyle.SP_MediaPause))
        else:
            self.play_pause_button.setText("Play")
            self.play_pause_button.setIcon(self.style().standardIcon(QStyle.SP_MediaPlay))

    def _launch_main_app(self):
        self.logger.warning("Attempting to launch main app (app.py) - this is a debug/development feature.")
        try:
            app_path = os.path.join(self.base_dir, 'app.py')
            if not os.path.exists(app_path):
                self.logger.error("app.py not found, cannot launch.")
                return
            subprocess.Popen([sys.executable, app_path], cwd=self.base_dir)
            self.close()
        except Exception as e:
            self.logger.error("Failed to launch main app.", exc_info=True)

    def set_style(self):
        self.setStyleSheet(CROP_APP_STYLESHEET)
        self.snapshot_button.setStyleSheet("background-color: #148C14;")
        self.send_crop_button.setStyleSheet("background-color: #e67e22; color: white; padding: 5px; border-radius: 6px; font-weight: bold; max-width: 120px;")

    def trigger_portrait_add(self):
        pix, rect = self.draw_widget.get_selection()
        if pix and rect:
            self.logger.info(f"Adding new scissored item to portrait window from crop rect: {rect}")
            self.update_crop_coordinates_label(rect)
            if self.portrait_window is None:
                self.logger.info("Creating new PortraitWindow instance.")
                self.portrait_window = PortraitWindow(self.media_processor.original_resolution, self.config_path)
            self.portrait_window.add_scissored_item(pix, rect, self.background_crop_width)
            self.draw_widget.clear_selection()
            self.portrait_window.show()
        else:
            self.logger.warning("Attempted to add to portrait, but no crop selection was made.")
            self.coordinates_label.setText("Please draw a box first!")

    def open_file(self):
        file_path, _ = QFileDialog.getOpenFileName(self, "Open Video", self.last_dir, "Video Files (*.mp4 *.avi *.mkv)")
        if file_path:
            self.logger.info(f"File dialog opened and selected file: {file_path}")
            self.load_file(file_path)
        else:
            self.logger.info("File dialog opened but no file was selected.")

    def load_file(self, file_path):
        self.last_dir = os.path.dirname(file_path)
        self.media_processor.load_media(file_path, self.video_frame.winId())
        self.play_pause_button.setEnabled(True)
        self.snapshot_button.setEnabled(False)
        self.snapshot_button.setText("Loading...")
        self.position_slider.setEnabled(True)
        self.show_video_view()

        def enable_snap():
            res = self.media_processor.original_resolution
            self.logger.info(f"Media resolution determined: {res}")
            self.coordinates_label.setText(f"Resolution: {res}" if res else "Could not get resolution.")
            self.snapshot_button.setEnabled(True)
            self.snapshot_button.setText("Begin Crops")
        QTimer.singleShot(1000, enable_snap)

    def take_snapshot(self):
        self.logger.info("Snapshot process initiated.")
        self.coordinates_label.setText("Generating snapshot...")
        QApplication.processEvents()
        if self.media_processor.media_player.is_playing():
            self.media_processor.stop()
            self.play_pause_button.setText("Play")
            self.play_pause_button.setIcon(self.style().standardIcon(QStyle.SP_MediaPlay))
        success, message = self.media_processor.take_snapshot(self.snapshot_path)
        self.logger.info(f"Snapshot result: Success={success}, Message='{message}'")
        self.coordinates_label.setText(message)
        if success:
            self._show_draw_view()

    def _show_draw_view(self):
        self.logger.info("Showing draw view with new snapshot.")
        snapshot_pixmap = QPixmap(self.snapshot_path)
        if snapshot_pixmap.isNull():
            self.logger.error(f"Failed to load snapshot image from disk: {self.snapshot_path}")
            self.coordinates_label.setText("Failed to load snapshot image.")
            return
        if self.portrait_window is None:
            self.portrait_window = PortraitWindow(self.media_processor.original_resolution, self.config_path)
        target_aspect = 1150 / 1920
        img_aspect = snapshot_pixmap.width() / snapshot_pixmap.height()
        if img_aspect > target_aspect:
            h = snapshot_pixmap.height()
            w = int(h * target_aspect)
            x = (snapshot_pixmap.width() - w) // 2
            y = 0
        else:
            w = snapshot_pixmap.width()
            h = int(w / target_aspect)
            x = 0
            y = (snapshot_pixmap.height() - h) // 2
        self.background_crop_width = w
        center_crop_rect = QRect(x, y, w, h)
        background_pixmap = snapshot_pixmap.copy(center_crop_rect)
        self.portrait_window.set_background(background_pixmap)
        self.portrait_window.show()
        self.draw_widget.setImage(self.snapshot_path)
        self.view_stack.setCurrentWidget(self.draw_widget)
        self.send_crop_button.setVisible(True)
        self.draw_widget.setFocus()
        self.coordinates_label.setText("Ready to draw crops.")

    def show_video_view(self):
        self.logger.info("Switching back to video view.")
        self.view_stack.setCurrentWidget(self.video_frame)
        self.video_frame.setFocus()
        self.send_crop_button.setVisible(False)

    def set_position(self, position):
        self.logger.info(f"Slider position changed to: {position}")
        self.media_processor.set_position(position / 1000.0)

    def update_ui(self):
        if self.media_processor.media:
            media_pos = int(self.media_processor.get_position() * 1000)
            self.position_slider.setValue(media_pos)
            if str(self.media_processor.get_state()) == 'State.Ended':
                self.logger.info("Media reached end.")
                self.media_processor.stop()
                self.play_pause_button.setText("Play")
                self.play_pause_button.setIcon(self.style().standardIcon(QStyle.SP_MediaPlay))
                self.position_slider.setValue(0)

    def update_crop_coordinates_label(self, rect):
        self.coordinates_label.setText(f"Crop: x={rect.x()}, y={rect.y()}, w={rect.width()}, h={rect.height()}")