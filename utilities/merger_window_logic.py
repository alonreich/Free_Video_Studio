import os
from pathlib import Path
from PyQt5.QtCore import QPropertyAnimation, QEasingCurve, Qt
from PyQt5.QtWidgets import QLabel, QPushButton
from utilities.merger_utils import _load_conf, _save_conf

class MergerWindowLogic:
    def __init__(self, window):
        self.window = window

    def load_config(self):
        self.window._cfg = _load_conf()
        self.window._last_dir = self.window._cfg.get("last_dir", str(Path.home() / "Downloads"))
        self.window._last_out_dir = self.window._cfg.get("last_out_dir", str(Path.home() / "Downloads"))
        self.window.logger.info(f"Loaded last_dir: {self.window._last_dir}")
        self.window.logger.info(f"Loaded last_out_dir: {self.window._last_out_dir}")
        try:
            g = self.window._cfg.get("geometry", {})
            if g:
                x = int(g.get("x", self.window.x()))
                y = int(g.get("y", self.window.y()))
                w = int(g.get("w", self.window.width()))
                h = int(g.get("h", self.window.height()))
                screen = self.window.screen()
                if screen:
                    screen_geometry = screen.availableGeometry()
                    x = max(screen_geometry.left(), min(x, screen_geometry.right() - w))
                    y = max(screen_geometry.top(), min(y, screen_geometry.bottom() - h))
                    w = max(800, min(w, screen_geometry.width()))
                    h = max(600, min(h, screen_geometry.height()))
                self.window.move(x, y)
                self.window.resize(w, h)
                self.window.logger.info(f"Restored window geometry: {x},{y} {w}x{h}")
        except Exception as ex:
            self.window.logger.debug(f"Failed to restore window geometry: {ex}")

    def save_config(self):
        """Thread-safe configuration saving with atomic write."""
        try:
            config_copy = self.window._cfg.copy()
            config_copy["geometry"] = {
                "x": self.window.x(),
                "y": self.window.y(),
                "w": self.window.width(),
                "h": self.window.height()
            }
            config_copy["last_dir"] = self.window._last_dir
            config_copy["last_out_dir"] = self.window._last_out_dir
            if hasattr(self.window, '_music_eff'):
                config_copy["last_music_volume"] = self.window._music_eff()
            self.window.logger.info("Saving config to merger_app.conf")
            _save_conf(config_copy)
            self.window._cfg = config_copy
        except Exception as err:
            self.window.logger.error("Error saving config in merger closeEvent: %s", err)

    def can_anim(self, row, new_row):
        if row == new_row or not (0 <= row < self.window.listw.count()) or not (0 <= new_row < self.window.listw.count()):
            return False
        if getattr(self.window, "_animating", False):
            return False
        if not self.window.listw.itemWidget(self.window.listw.item(row)) or not self.window.listw.itemWidget(self.window.listw.item(new_row)):
            return False
        return True

    def start_swap_animation(self, row, new_row):
        try:
            v = self.window.listw.viewport()
            it1, it2 = self.window.listw.item(row), self.window.listw.item(new_row)
            w1, w2 = self.window.listw.itemWidget(it1), self.window.listw.itemWidget(it2)
            r1 = self.window.listw.visualItemRect(it1)
            r2 = self.window.listw.visualItemRect(it2)
            if r1.isNull() or r2.isNull():
                return False
            pm1 = w1.grab()
            pm2 = w2.grab()
            ghost1 = QLabel(v); ghost1.setPixmap(pm1); ghost1.setAttribute(Qt.WA_TransparentForMouseEvents, True)
            ghost2 = QLabel(v); ghost2.setPixmap(pm2); ghost2.setAttribute(Qt.WA_TransparentForMouseEvents, True)
            ghost1.move(r1.topLeft()); ghost1.show()
            ghost2.move(r2.topLeft()); ghost2.show()
            w1.setVisible(False); w2.setVisible(False)
            a1 = QPropertyAnimation(ghost1, b"pos", self.window); a1.setDuration(280)
            a2 = QPropertyAnimation(ghost2, b"pos", self.window); a2.setDuration(280)
            a1.setStartValue(r1.topLeft()); a1.setEndValue(r2.topLeft()); a1.setEasingCurve(QEasingCurve.InOutQuad)
            a2.setStartValue(r2.topLeft()); a2.setEndValue(r1.topLeft()); a2.setEasingCurve(QEasingCurve.InOutQuad)
            self.window._animating = True

            def _cleanup_animation():
                """Safe cleanup of animation resources."""
                try:
                    if w1: w1.setVisible(True)
                    if w2: w2.setVisible(True)
                    if ghost1: ghost1.deleteLater()
                    if ghost2: ghost2.deleteLater()
                    if a1: a1.deleteLater()
                    if a2: a2.deleteLater()
                except Exception as ex:
                    self.window.logger.debug(f"Error cleaning up animation: {ex}")
                finally:
                    self.window._animating = False

            def _finish():
                try:
                    self.perform_swap(row, new_row)
                except Exception as ex:
                    self.window.logger.debug(f"Error during swap animation finish: {ex}")
                finally:
                    _cleanup_animation()
            animations_completed = [False, False]
            
            def _on_animation_finished(anim_index):
                animations_completed[anim_index] = True
                if all(animations_completed):
                    _finish()
            a1.finished.connect(lambda: _on_animation_finished(0))
            a2.finished.connect(lambda: _on_animation_finished(1))
            try:
                a1.start()
                a2.start()
                return True
            except Exception as ex:
                self.window.logger.debug(f"Animation start failed: {ex}")
                _cleanup_animation()
                return False
        except Exception as ex:
            self.window.logger.debug(f"Animation setup failed: {ex}")
            if hasattr(self.window, '_animating'):
                self.window._animating = False
            return False

    def perform_swap(self, row, new_row):
        """Robustly swaps two items in the list widget by updating their content."""
        listw = self.window.listw
        i1 = listw.item(row)
        i2 = listw.item(new_row)
        if not i1 or not i2:
            return
        d1 = i1.data(Qt.UserRole)
        d2 = i2.data(Qt.UserRole)
        w1 = listw.itemWidget(i1)
        w2 = listw.itemWidget(i2)
        if row < new_row:
            listw.takeItem(row)
            listw.insertItem(new_row - 1, i1)
            listw.takeItem(new_row - 1)
            listw.insertItem(row, i2)
        else:
            listw.takeItem(row)
            listw.insertItem(new_row, i1)
            listw.takeItem(new_row + 1)
            listw.insertItem(row, i2)
        i1.setData(Qt.UserRole, d2)
        i2.setData(Qt.UserRole, d1)
        t1 = i1.toolTip()
        t2 = i2.toolTip()
        i1.setToolTip(t2)
        i2.setToolTip(t1)
        if w1:
            listw.setItemWidget(i2, w1)
        if w2:
            listw.setItemWidget(i1, w2)

        def update_widget_content(item, path, is_original_widget=True):
            w = listw.itemWidget(item)
            if not w: 
                return
            
            from PyQt5.QtWidgets import QLabel, QPushButton
            lbl = w.findChild(QLabel, "fileLabel")
            if lbl:
                lbl.setText(os.path.basename(path))
                lbl.setToolTip(path)
            btn = w.findChild(QPushButton, "playButton")
            if btn:
                btn.setProperty("path", path)
            w.updateGeometry()
            w.update()
        update_widget_content(i1, d2, True)
        update_widget_content(i2, d1, True)
        listw.clearSelection()
        listw.setCurrentRow(new_row)
        if listw.item(new_row):
            listw.item(new_row).setSelected(True)
        listw.viewport().update()
        listw.updateGeometry()
        if hasattr(self.window, 'event_handler') and hasattr(self.window.event_handler, 'update_button_states'):
            self.window.event_handler.update_button_states()
