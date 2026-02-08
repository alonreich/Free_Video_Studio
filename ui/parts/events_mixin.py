from PyQt5.QtCore import QEvent, Qt, QRect, QTimer
from PyQt5.QtGui import QPainter, QColor, QFont, QPen

class EventsMixin:
    def mousePressEvent(self, event):
        """Force keyboard focus back to the main window to enable shortcuts."""
        try:
            if event.button() == Qt.LeftButton:
                self.setFocus(Qt.MouseFocusReason)
        except Exception as e:
            if hasattr(self, "logger"): self.logger.error(f"MousePress error: {e}")
        super().mousePressEvent(event)

    def eventFilter(self, obj, event):
        if obj in (getattr(self, "video_frame", None), getattr(self, "video_surface", None)):
            if event.type() in (QEvent.Resize, QEvent.Move):
                try:
                    self._update_volume_badge()
                    if hasattr(self, "portrait_mask_overlay"):
                        r = self.video_surface.rect()
                        top_left = self.video_surface.mapToGlobal(r.topLeft())
                        self.portrait_mask_overlay.setGeometry(QRect(top_left, r.size()))
                        self._update_portrait_mask_overlay_state()
                except Exception as e:
                    if hasattr(self, "logger"): self.logger.error(f"EventFilter resize error: {e}")
                return False
        return super().eventFilter(obj, event)

    