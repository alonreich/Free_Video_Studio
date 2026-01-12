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
                    hasattr(self, 'draw_widget') and 
                    self.view_stack.currentWidget() == self.draw_widget and
                    hasattr(self.draw_widget, '_crop_rect') and
                    not self.draw_widget._crop_rect.isNull()):
                    self.arrow_key_press_counter += 1
                    if self.arrow_key_press_counter % 2 == 0:
                        delta = 1 
                        if key == Qt.Key_Up:
                            self.draw_widget._crop_rect.translate(0, -delta)
                        elif key == Qt.Key_Down:
                            self.draw_widget._crop_rect.translate(0, delta)
                        elif key == Qt.Key_Left:
                            self.draw_widget._crop_rect.translate(-delta, 0)
                        elif key == Qt.Key_Right:
                            self.draw_widget._crop_rect.translate(delta, 0)
                        self.draw_widget.update()
                        if hasattr(self, 'update_crop_coordinates_label'):
                            self.update_crop_coordinates_label(self.draw_widget._crop_rect)
                    event.accept()
                    return
        super().keyPressEvent(event)