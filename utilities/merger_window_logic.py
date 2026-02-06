import os
from pathlib import Path
from PyQt5.QtCore import QPropertyAnimation, QEasingCurve, Qt
from PyQt5.QtWidgets import QLabel, QPushButton
from utilities.merger_utils import _load_conf, _save_conf

class MergerWindowLogic:
    def __init__(self, window):
        self.window = window

    def _is_widget_alive(self, widget):
        if not widget:
            return False
        try:
            _ = widget.isVisible()
            return True
        except RuntimeError:
            return False

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

    def get_last_dir(self):
        return getattr(self.window, "_last_dir", str(Path.home()))

    def set_last_dir(self, path):
        self.window._last_dir = path

    def get_last_out_dir(self):
        return getattr(self.window, "_last_out_dir", str(Path.home()))

    def set_last_out_dir(self, path):
        self.window._last_out_dir = path

    def can_anim(self, row, new_row):
        if row == new_row or not (0 <= row < self.window.listw.count()) or not (0 <= new_row < self.window.listw.count()):
            return False
        if getattr(self.window, "_animating", False):
            return False
        w_row = self.window.listw.itemWidget(self.window.listw.item(row))
        w_new = self.window.listw.itemWidget(self.window.listw.item(new_row))
        if not self._is_widget_alive(w_row) or not self._is_widget_alive(w_new):
            return False
        return True

    def start_swap_animation(self, row, new_row):
        ghost1 = None
        ghost2 = None
        a1 = None
        a2 = None
        try:
            v = self.window.listw.viewport()
            it1, it2 = self.window.listw.item(row), self.window.listw.item(new_row)
            w1, w2 = self.window.listw.itemWidget(it1), self.window.listw.itemWidget(it2)
            if not self._is_widget_alive(w1) or not self._is_widget_alive(w2):
                return False
            r1 = self.window.listw.visualItemRect(it1)
            r2 = self.window.listw.visualItemRect(it2)
            if r1.isNull() or r2.isNull():
                return False
            try:
                pm1 = w1.grab()
                pm2 = w2.grab()
            except RuntimeError:
                return False
            ghost1 = QLabel(v); ghost1.setPixmap(pm1); ghost1.setAttribute(Qt.WA_TransparentForMouseEvents, True)
            ghost2 = QLabel(v); ghost2.setPixmap(pm2); ghost2.setAttribute(Qt.WA_TransparentForMouseEvents, True)
            ghost1.move(r1.topLeft()); ghost1.show()
            ghost2.move(r2.topLeft()); ghost2.show()
            w1.setVisible(False); w2.setVisible(False)
            a1 = QPropertyAnimation(ghost1, b"pos", self.window); a1.setDuration(150)
            a2 = QPropertyAnimation(ghost2, b"pos", self.window); a2.setDuration(150)
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
        """Redirects swap to move, as swapping neighbors is effectively a move."""
        self.perform_move(row, new_row)
        
    def perform_move(self, from_row, to_row, rebuild_widget: bool = False):
        """Robustly moves an item from from_row to to_row (insertion)."""
        listw = self.window.listw
        if from_row == to_row or from_row < 0 or to_row < 0 or from_row >= listw.count() or to_row >= listw.count():
            return
        if from_row < to_row:
            to_row -= 1
        if from_row == to_row:
            return
        updates_prev = listw.updatesEnabled()
        listw.setUpdatesEnabled(False)
        blocker = None
        try:
            from PyQt5.QtCore import QSignalBlocker
            blocker = QSignalBlocker(listw)
        except Exception:
            blocker = None
        try:
            item = listw.item(from_row)
            if not item:
                return
            path = item.data(Qt.UserRole)
            probe_data = item.data(Qt.UserRole + 1)
            self.window.logger.info(f"LOGIC: Moving item '{os.path.basename(str(path))}' from index {from_row} to {to_row}.")
            existing_widget = None if rebuild_widget else listw.itemWidget(item)
            if existing_widget and self._is_widget_alive(existing_widget):
                try:
                    existing_widget.setParent(self.window)
                except RuntimeError:
                    existing_widget = None
            else:
                existing_widget = None
            listw.takeItem(from_row)
            if rebuild_widget:
                from PyQt5.QtWidgets import QListWidgetItem
                new_item = QListWidgetItem()
                new_item.setToolTip(path)
                new_item.setData(Qt.UserRole, path)
                if probe_data:
                    new_item.setData(Qt.UserRole + 1, probe_data)
                listw.insertItem(to_row, new_item)
                item = new_item
            else:
                listw.insertItem(to_row, item)
            if existing_widget and not rebuild_widget:
                try:
                    from PyQt5.QtCore import QSize
                    existing_widget.setVisible(True)
                    item.setSizeHint(QSize(existing_widget.width(), 52))
                    listw.setItemWidget(item, existing_widget)
                except RuntimeError:
                    existing_widget = None
            if not existing_widget:
                w = self.window.make_item_widget(path)
                if probe_data:
                    try:
                        from utilities.merger_handlers_list import _human_time
                        dur = float(probe_data.get('format', {}).get('duration', 0))
                        if dur > 0: w.set_duration_label(_human_time(dur))
                        streams = probe_data.get('streams', [])
                        vid = next((s for s in streams if s.get('width')), None)
                        if vid: w.set_resolution_label(f"{vid['width']}x{vid['height']}")
                    except: pass
                item.setSizeHint(w.sizeHint())
                listw.setItemWidget(item, w)
            listw.clearSelection()
            item.setSelected(True)
            listw.setCurrentItem(item)
            listw.doItemsLayout()
            if hasattr(self.window.event_handler, 'update_button_states'):
                self.window.event_handler.update_button_states()
        finally:
            if blocker is not None:
                try:
                    blocker.unblock()
                except Exception:
                    pass
            listw.setUpdatesEnabled(updates_prev)
