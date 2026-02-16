from PyQt5.QtCore import QTimer, QPoint, Qt
from PyQt5.QtWidgets import QStyleOptionSlider, QStyle
import threading

class VolumeMixin:
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
            eff = self._vol_eff(raw)
            self.volume_badge.setText(f"{eff}%")
            self.volume_badge.adjustSize()
            self.volume_badge.show()
        except Exception as e:
            if hasattr(self, "logger"):
                self.logger.error(f"Volume Badge Error: {e}")

    def apply_master_volume(self):
        """Sync UI -> VLC and start reinforcement."""
        v = 100
        if hasattr(self, "volume_slider"):
            v = int(self._vol_eff()) 
        else:
            cfg = getattr(self.config_manager, "config", {})
            v = int(cfg.get("video_mix_volume", cfg.get("last_volume", 100)))
        self._on_master_volume_changed(int(v))

        def _reinforce():
            try:
                player = getattr(self, "vlc_player", None)
                if player:
                    player.audio_set_volume(v)
            except: pass
        for delay in [100, 500, 1000, 2000, 4000, 7000, 10000, 15000]:
            QTimer.singleShot(delay, _reinforce)

    def _on_master_volume_changed(self, v: int):
        """
        Direction: UI Slider -> VLC Volume
        """
        eff_pct = self._vol_eff(v)
        player = getattr(self, "vlc_player", None)
        if player:
            try: 
                player.audio_set_volume(eff_pct)
                player.audio_set_mute(False)
            except: pass
        if hasattr(self, "config_manager"):
            try:
                cfg = dict(self.config_manager.config)
                cfg['video_mix_volume'] = eff_pct
                cfg['last_volume'] = eff_pct
                self.config_manager.save_config(cfg)
            except: pass
        self._update_volume_badge()
