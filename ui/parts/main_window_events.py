import os, sys, time, threading, logging, subprocess, traceback
from PyQt5.QtCore import *
from PyQt5.QtGui import *
from PyQt5.QtWidgets import *

class MainWindowEventsMixin:
    def keyPressEvent(self, event):
        if hasattr(self, "handle_global_key_press") and self.handle_global_key_press(event):
            return
        if event.key() == Qt.Key_F11:
            self.launch_advanced_editor()
        elif event.key() == Qt.Key_F12:
            self.launch_crop_tool()
        else:
            QMainWindow.keyPressEvent(self, event)

    def mousePressEvent(self, event):
        try:
            if event.button() == Qt.LeftButton:
                self.setFocus(Qt.MouseFocusReason)
        except Exception as e:
            if hasattr(self, 'logger'):
                self.logger.error("MousePress error: %s", e)
        QMainWindow.mousePressEvent(self, event)

    def eventFilter(self, obj, event):
        try:
            if event.type() == QEvent.KeyPress:
                if hasattr(self, "handle_global_key_press") and self.handle_global_key_press(event):
                    return True
        except Exception as e:
            if hasattr(self, 'logger'):
                self.logger.error("Keyboard eventFilter error: %s", e)
        return False

    def resizeEvent(self, event):
        if hasattr(self, "_update_upload_hint_responsive"):
            self._update_upload_hint_responsive()
            QTimer.singleShot(0, self._update_upload_hint_responsive)
        try:
            if hasattr(self, '_update_volume_badge'):
                self._update_volume_badge()
            if hasattr(self, "portrait_mask_overlay") and self.portrait_mask_overlay and hasattr(self, "video_surface"):
                r = self.video_surface.rect()
                top_left = self.video_surface.mapToGlobal(r.topLeft())
                self.portrait_mask_overlay.setGeometry(QRect(top_left, r.size()))
                if hasattr(self, '_update_portrait_mask_overlay_state'):
                    self._update_portrait_mask_overlay_state()
        except Exception as e:
            if hasattr(self, "logger"):
                self.logger.error("Resize child update error: %s", e)
        if hasattr(self, '_resize_timer'):
            self._resize_timer.start()
        else:
            if hasattr(self, '_delayed_resize_event'):
                self._delayed_resize_event()

    def moveEvent(self, event):
        if hasattr(self, 'handle_persistence_event'):
            self.handle_persistence_event()
        try:
            if hasattr(self, "portrait_mask_overlay") and self.portrait_mask_overlay and hasattr(self, "video_surface"):
                r = self.video_surface.rect()
                top_left = self.video_surface.mapToGlobal(r.topLeft())
                self.portrait_mask_overlay.setGeometry(QRect(top_left, r.size()))
        except Exception: pass

    def _delayed_resize_event(self):
        try:
            if hasattr(self, "_update_upload_hint_responsive"):
                self._update_upload_hint_responsive()
            if hasattr(self, "_update_volume_badge"):
                self._update_volume_badge()
            if hasattr(self, "_resize_overlay"):
                self._resize_overlay()
            if hasattr(self, "_adjust_trim_margins"):
                self._adjust_trim_margins()
            if hasattr(self, "_update_portrait_mask_overlay_state"):
                self._update_portrait_mask_overlay_state()
        except Exception:
            pass

    def closeEvent(self, event):
        self._shutting_down = True
        if hasattr(self, 'save_geometry'):
            self.save_geometry()
        if getattr(self, "is_processing", False):
            reply = QMessageBox.question(self, "Quit During Processing",
                "A video is currently being processed. Closing now will cancel all progress. Quit anyway?",
                QMessageBox.Yes | QMessageBox.No, QMessageBox.No)
            if reply == QMessageBox.No:
                event.ignore()
                return
        if getattr(self, "_switching_app", False):
            if hasattr(self, 'cleanup_and_exit'):
                self.cleanup_and_exit()
            QMainWindow.closeEvent(self, event)
            return
        try:
            import psutil
            current_process = psutil.Process()
            children = current_process.children(recursive=True)
            for child in children:
                try:
                    if hasattr(self, 'logger'):
                        self.logger.info(f"EXIT: Killing child process {child.pid} ({child.name()})")
                    child.kill()
                except: pass
        except: pass
        if hasattr(self, 'cleanup_and_exit'):
            self.cleanup_and_exit()
        QMainWindow.closeEvent(self, event)
