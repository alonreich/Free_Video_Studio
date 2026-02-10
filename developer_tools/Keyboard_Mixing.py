from PyQt5.QtCore import Qt

class KeyboardShortcutMixin:
    def keyPressEvent(self, event):
        key = event.key()
        if key == Qt.Key_F12:
            if hasattr(self, '_launch_main_app') and callable(self._launch_main_app):
                self._launch_main_app()
                event.accept()
                return
        if key == Qt.Key_Space:
            if hasattr(self, 'view_stack') and hasattr(self, 'video_frame') and hasattr(self, 'play_pause'):
                if self.view_stack.currentWidget() == self.video_frame:
                    self.play_pause()
                    event.accept()
                    return
        if key in [Qt.Key_Up, Qt.Key_Down, Qt.Key_Left, Qt.Key_Right]:
            if not event.isAutoRepeat():
                if (hasattr(self, 'view_stack') and
                    hasattr(self, 'draw_scroll_area') and
                    self.view_stack.currentWidget() == self.draw_scroll_area and
                    hasattr(self, 'draw_widget') and
                    hasattr(self.draw_widget, 'handle_key_press')):
                    self.draw_widget.handle_key_press(event)
                    return
        super().keyPressEvent(event)