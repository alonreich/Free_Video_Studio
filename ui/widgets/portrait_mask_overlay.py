from PyQt5.QtWidgets import QWidget
from PyQt5.QtGui import QPainter, QColor
from PyQt5.QtCore import Qt, QRect

class PortraitMaskOverlay(QWidget):

    def __init__(self, parent=None):
        super().__init__(parent)
        self.setAttribute(Qt.WA_TransparentForMouseEvents)
        self.original_video_resolution = ""
        self.current_video_frame_size = None
        self.setHidden(True)

    def set_video_info(self, resolution_str: str, frame_size):
        if self.original_video_resolution != resolution_str or self.current_video_frame_size != frame_size:
            self.original_video_resolution = resolution_str
            self.current_video_frame_size = frame_size
            self.update()

    def paintEvent(self, event):
        if not self.isVisible() or not self.current_video_frame_size or not self.original_video_resolution:
            return
        painter = QPainter(self)
        painter.setRenderHint(QPainter.Antialiasing)
        try:
            orig_w, orig_h = map(int, self.original_video_resolution.split('x'))
        except ValueError:
            return
        if orig_w == 0 or orig_h == 0:
            return
        aspect_ratio = orig_w / orig_h
        if aspect_ratio < 1.5:
            return
        frame_w = self.width()
        frame_h = self.height()
        video_display_w = frame_w
        video_display_h = int(frame_w / aspect_ratio)
        if video_display_h > frame_h:
            video_display_h = frame_h
            video_display_w = int(frame_h * aspect_ratio)
        video_x = (frame_w - video_display_w) // 2
        video_y = (frame_h - video_display_h) // 2
        target_portrait_aspect = 1150 / 1920
        clear_w = video_display_w
        clear_h = int(video_display_w / target_portrait_aspect)
        if clear_h > video_display_h:
            clear_h = video_display_h
            clear_w = int(video_display_h * target_portrait_aspect)
        clear_x_offset = (video_display_w - clear_w) // 2
        clear_y_offset = (video_display_h - clear_h) // 2
        clear_rect = QRect(video_x + clear_x_offset, video_y + clear_y_offset, clear_w, clear_h)
        dim_color = QColor(0, 0, 0, 128)
        painter.fillRect(self.rect(), dim_color)
        painter.setCompositionMode(QPainter.CompositionMode_Clear)
        painter.fillRect(clear_rect, Qt.transparent)
        painter.end()