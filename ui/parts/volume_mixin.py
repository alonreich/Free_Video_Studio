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

    def _vol_eff(self, raw: int | None = None) -> int:
        """Map slider value -> real volume (0..100) respecting invertedAppearance."""
        v = int(self.volume_slider.value() if raw is None else raw)
        if self.volume_slider.invertedAppearance():
            return max(0, min(100, self.volume_slider.maximum() + self.volume_slider.minimum() - v))
        return max(0, min(100, v))

    def _update_volume_badge(self):
        """Update badge text to show effective %."""
        try:
            if not hasattr(self, "volume_badge") or not hasattr(self, "volume_slider"):
                return
            raw = int(self.volume_slider.value())
            self.volume_badge.setText(f"{self._vol_eff(raw)}%")
            self.volume_badge.adjustSize()
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
