from PyQt5.QtCore import QTimer, QPoint, Qt
from PyQt5.QtWidgets import QStyleOptionSlider, QStyle

class VolumeMixin:
    def apply_master_volume(self):
        """Push current slider value into VLC (unmute), persist, and refresh badge."""
        if getattr(self, "vlc_player", None):
            self.vlc_player.audio_set_mute(False)
        v = 100
        if hasattr(self, "volume_slider"):
            v = int(self.volume_slider.value())
        else:
            cfg = getattr(self.config_manager, "config", {})
            v = int(cfg.get("last_volume", 100))
        self._on_master_volume_changed(int(v))

    def _vol_eff(self, raw: int | None = None) -> int:
        """Map slider value -> real volume (0..100) respecting invertedAppearance."""
        if not hasattr(self, "volume_slider"):
            return 100
        v = int(self.volume_slider.value() if raw is None else raw)
        if self.volume_slider.invertedAppearance():
            return max(0, min(100, self.volume_slider.maximum() + self.volume_slider.minimum() - v))
        return max(0, min(100, v))

    def _update_volume_badge(self):
        """Update badge text to show effective %."""
        if not hasattr(self, "volume_badge") or not hasattr(self, "volume_slider"):
            return
        try:
            raw = int(self.volume_slider.value())
            self.volume_badge.setText(f"{self._vol_eff(raw)}%")
            self.volume_badge.adjustSize()
            self.volume_badge.show()
        except Exception as e:
            if hasattr(self, "logger"):
                self.logger.error(f"Volume Badge Error: {e}")

    def _on_master_volume_changed(self, v: int):
        """Apply mapped volume, persist real %, and refresh badge."""
        eff = self._vol_eff(v)
        if getattr(self, "vlc_player", None):
            self.vlc_player.audio_set_volume(eff)
        if hasattr(self, "config_manager"):
            try:
                cfg = dict(self.config_manager.config)
                cfg['last_volume'] = eff
                self.config_manager.save_config(cfg)
            except Exception as e:
                if hasattr(self, "logger"):
                    self.logger.error(f"Config Save Error (Volume): {e}")
        self._update_volume_badge()
