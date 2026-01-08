from PyQt5.QtCore import QTimer, QPoint, Qt
from PyQt5.QtWidgets import QStyleOptionSlider, QStyle

class VolumeMixin:

    def apply_master_volume(self):
        """Push current slider value into VLC (unmute), persist, and refresh badge."""
        try:
            self.vlc_player.audio_set_mute(False)
        except Exception:
            pass
        try:
            v = int(self.volume_slider.value())
        except Exception:
            try:
                v = int(getattr(self.config_manager, "config", {}).get("last_volume", 100))
            except Exception:
                v = 100
        try:
            self._on_master_volume_changed(int(v))
        except Exception:
            pass

    def _layout_volume_slider(self):
        """Safe layout: only after widgets are visible; avoid early mapTo crashes."""
        try:
            if not hasattr(self, "volume_slider") or not hasattr(self, "video_frame"):
                return
            if not self.isVisible() or not self.video_frame.isVisible():
                QTimer.singleShot(0, self._layout_volume_slider)
                return
            w = 40
            H = 150
            margin = 20
            top_left_global = self.video_frame.mapToGlobal(QPoint(0, 0))
            top_left_self   = self.mapFromGlobal(top_left_global)
            x = top_left_self.x() + margin
            y = top_left_self.y()
            h = max(0, self.video_frame.height())
            if h < 4:
                QTimer.singleShot(0, self._layout_volume_slider)
                return
            self.volume_slider.setGeometry(x, y, w, h)
            self.volume_slider.show(); self.volume_slider.raise_()
            if hasattr(self, "volume_badge"):
                self.volume_badge.show(); self.volume_badge.raise_()
            self._update_volume_badge()
        except Exception:
            pass

    def _vol_eff(self, raw: int | None = None) -> int:
        """Map slider value -> real volume (0..100) respecting invertedAppearance."""
        v = int(self.volume_slider.value() if raw is None else raw)
        if self.volume_slider.invertedAppearance():
            return max(0, min(100, self.volume_slider.maximum() + self.volume_slider.minimum() - v))
        return max(0, min(100, v))

    def _update_volume_badge(self):
        """Position badge next to the actual handle; text shows effective %."""
        try:
            if not hasattr(self, "volume_badge") or not hasattr(self, "volume_slider"):
                return
            from PyQt5.QtWidgets import QStyleOptionSlider, QStyle
            opt = QStyleOptionSlider()
            opt.initFrom(self.volume_slider)
            opt.orientation = Qt.Vertical
            opt.minimum = self.volume_slider.minimum()
            opt.maximum = self.volume_slider.maximum()
            opt.rect = self.volume_slider.rect()
            raw = int(self.volume_slider.value())
            opt.sliderPosition = raw
            opt.sliderValue = raw
            handle = self.volume_slider.style().subControlRect(
                QStyle.CC_Slider, opt, QStyle.SC_SliderHandle, self.volume_slider
            )
            parent = self.volume_slider.parentWidget() or self
            tl = self.volume_slider.mapTo(parent, handle.topLeft())
            x = self.volume_slider.x() + self.volume_slider.width() + 8
            y = tl.y() + (handle.height() // 2) - (self.volume_badge.height() // 2)
            self.volume_badge.setText(f"{self._vol_eff(raw)}%")
            self.volume_badge.adjustSize()
            y = max(2, min(parent.height() - self.volume_badge.height() - 2, y))
            self.volume_badge.move(x, y)
            self.volume_badge.show()
        except Exception:
            pass

    def _on_master_volume_changed(self, v: int):
        """Apply mapped volume, persist real %, and refresh badge."""
        eff = self._vol_eff(v)
        try:
            self.vlc_player.audio_set_volume(eff)
        except Exception:
            pass
        try:
            cfg = dict(self.config_manager.config)
            cfg['last_volume'] = eff
            self.config_manager.save_config(cfg)
        except Exception:
            pass
        try:
            self._update_volume_badge()
        except Exception:
            pass

    def showEvent(self, e):
        super().showEvent(e)
        try:
            self._layout_volume_slider()
            self._update_volume_badge()
        except Exception:
            pass