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

    def _music_eff(self) -> int:
        """[FIX #8] Consistent music volume source."""
        if not hasattr(self, "_music_volume_pct"):
            try:
                self._music_volume_pct = int(self.config_manager.config.get('music_mix_volume', 80))
            except:
                self._music_volume_pct = 80
        return int(self._music_volume_pct)

    def _sync_all_volumes(self):
        """[FIX #8] One-stop shop for player volume synchronization."""
        v_eff = self._vol_eff()
        m_eff = self._music_eff()
        v_player = getattr(self, "vlc_player", None)
        if v_player:
            v_player.audio_set_volume(v_eff)
            v_player.audio_set_mute(False)
        m_player = getattr(self, "vlc_music_player", None)
        if m_player:
            m_player.audio_set_volume(m_eff)
            m_player.audio_set_mute(False)

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
        self._sync_all_volumes()
        self._update_volume_badge()

        def _reinforce():
            try:
                self._sync_all_volumes()
            except: pass
        for delay in [100, 500, 1000, 2000, 4000, 7000, 10000, 15000]:
            QTimer.singleShot(delay, _reinforce)

    def _on_master_volume_changed(self, v: int):
        """
        Direction: UI Slider -> VLC Volume
        """
        self._sync_all_volumes()
        eff_pct = self._vol_eff(v)
        if hasattr(self, "config_manager"):
            try:
                cfg = dict(self.config_manager.config)
                cfg['video_mix_volume'] = eff_pct
                cfg['last_volume'] = eff_pct
                self.config_manager.save_config(cfg)
            except: pass
        self._update_volume_badge()
