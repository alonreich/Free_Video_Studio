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
                self.window.move(int(g.get("x", self.window.x())), int(g.get("y", self.window.y())))
                self.window.resize(int(g.get("w", self.window.width())), int(g.get("h", self.window.height())))
        except Exception:
            pass

    def save_config(self):
        try:
            g = {"x": self.window.x(), "y": self.window.y(), "w": self.window.width(), "h": self.window.height()}
            save_cfg = self.window.config_manager.config if self.window.config_manager else self.window._cfg
            save_cfg["geometry"]  = g
            save_cfg["last_dir"]  = self.window._last_dir
            save_cfg["last_out_dir"] = self.window._last_out_dir
            save_cfg["last_music_volume"] = self.window.music_handler._music_eff()
            self.window.logger.info(f"Saving last_dir: {self.window._last_dir}")
            self.window.logger.info(f"Saving last_out_dir: {self.window._last_out_dir}")
            if self.window.config_manager:
                self.window.config_manager.save_config(save_cfg)
            else:
                _save_conf(save_cfg)
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
            a1 = QPropertyAnimation(ghost1, b"pos", self.window); a1.setDuration(140)
            a2 = QPropertyAnimation(ghost2, b"pos", self.window); a2.setDuration(140)
            a1.setStartValue(r1.topLeft()); a1.setEndValue(r2.topLeft()); a1.setEasingCurve(QEasingCurve.InOutQuad)
            a2.setStartValue(r2.topLeft()); a2.setEndValue(r1.topLeft()); a2.setEasingCurve(QEasingCurve.InOutQuad)
            self.window._animating = True
            def _finish():
                try:
                    self.perform_swap(row, new_row)
                finally:
                    try:
                        w1.setVisible(True); w2.setVisible(True)
                        ghost1.deleteLater(); ghost2.deleteLater()
                    except Exception:
                        pass
                    self.window._animating = False
            a2.finished.connect(_finish)
            a1.start(); a2.start()
            return True
        except Exception:
            return False

    def perform_swap(self, row, new_row):
        i1, i2 = self.window.listw.item(row), self.window.listw.item(new_row)
        if not i1 or not i2:
            return
        p1, p2 = i1.data(Qt.UserRole), i2.data(Qt.UserRole)
        i1.setData(Qt.UserRole, p2); i2.setData(Qt.UserRole, p1)
        i1.setToolTip(p2);           i2.setToolTip(p1)
        w1 = self.window.listw.itemWidget(i1)
        if w1:
            lbl = w1.findChild(QLabel, "fileLabel") or w1.findChild(QLabel)
            if lbl: lbl.setText(os.path.basename(p2))
            btn = w1.findChild(QPushButton, "playButton")
            if btn: btn.setProperty("path", p2)
        w2 = self.window.listw.itemWidget(i2)
        if w2:
            lbl = w2.findChild(QLabel, "fileLabel") or w2.findChild(QLabel)
            if lbl: lbl.setText(os.path.basename(p1))
            btn = w2.findChild(QPushButton, "playButton")
            if btn: btn.setProperty("path", p1)
        self.window.listw.setCurrentRow(new_row)
        self.window.listw.viewport().update()