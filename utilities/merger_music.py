import os
import subprocess
from pathlib import Path
from utilities.merger_utils import _mp3_dir, _ffprobe
from utilities.merger_music_dialog import MusicDialogHandler

class MusicHandler(MusicDialogHandler):
    def __init__(self, parent):
        self.parent = parent
        self.logger = parent.logger
        self._music_files = []
        super().__init__(parent)

    def _scan_mp3_folder(self):
        r"""Scan .\mp3 for .mp3 files, sorted by modified time (newest first)."""
        try:
            d = _mp3_dir()
            files = []
            for name in os.listdir(d):
                if name.lower().endswith(".mp3"):
                    p = os.path.join(d, name)
                    try:
                        mt = os.path.getmtime(p)
                    except Exception:
                        mt = 0
                    files.append((mt, name, p))
            files.sort(key=lambda x: x[0], reverse=True)
            self._music_files = [(n, p) for _, n, p in files]
        except Exception:
            self._music_files = []
        self._populate_music_combo()

    def _populate_music_combo(self):
        """Refresh the dropdown safely based on self._music_files."""
        mf = getattr(self, "_music_files", [])
        self.parent.music_combo.blockSignals(True)
        self.parent.music_combo.clear()
        if not mf:
            self.parent.music_combo.addItem("No MP3 files found in ./mp3", "")
            self.parent.music_combo.setEnabled(False)
        else:
            self.parent.music_combo.addItem("— Select an MP3 —", "")
            for name, path in mf:
                self.parent.music_combo.addItem(name, path)
            self.parent.music_combo.setCurrentIndex(0)
            self.parent.music_combo.setEnabled(True)
        self.parent.music_combo.blockSignals(False)

    def _on_add_music_toggled(self, checked: bool):
        """Show/enable music controls only if files exist and checkbox checked."""
        have_files = bool(self._music_files)
        enable = checked and have_files
        self.parent.music_combo.setVisible(enable)
        self.parent.music_combo.setEnabled(enable)
        self.parent.music_volume_slider.setVisible(enable)
        self.parent.music_volume_label.setVisible(enable)
        self.parent.music_offset_input.setVisible(enable)
        if enable:
            self.parent.music_volume_slider.setEnabled(True)
            self.parent.music_offset_input.setEnabled(True)
            self._on_music_selected(self.parent.music_combo.currentIndex())
        else:
            self.parent.music_volume_slider.setEnabled(False)
            self.parent.music_offset_input.setEnabled(False)

    def _on_music_selected(self, index: int):
        if not self._music_files:
            return
        if self.parent.music_volume_slider.value() in (0, 25):
            self.parent.music_volume_slider.setValue(25)
        try:
            p = self.parent.music_combo.currentData()
            if not p:
                self.parent.music_offset_input.setRange(0.0, 0.0)
                self.parent.music_offset_input.setValue(0.0)
                return
            dur = self._probe_audio_duration(p)
            self.parent.music_offset_input.setRange(0.0, max(0.0, dur - 0.01))
            if self.parent.vlc_instance:
                self.show_music_offset_dialog(p)
            else:
                self.logger.warning("VLC instance not available, cannot show music offset dialog.")
        except Exception as e:
            self.logger.error("Error on music selection or dialog: %s", e)
            self.parent.music_offset_input.setRange(0.0, 0.0)
            self.parent.music_offset_input.setValue(0.0)

    def _probe_audio_duration(self, path: str) -> float:
        """Return audio duration in seconds (float) or 0.0 on failure."""
        try:
            cmd = [_ffprobe(self.parent.ffmpeg), "-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", path]
            r = subprocess.run(cmd, capture_output=True, text=True, check=True,
                               creationflags=(subprocess.CREATE_NO_WINDOW if os.name == 'nt' else 0))
            return max(0.0, float(r.stdout.strip()))
        except Exception:
            self.logger.exception("Failed to probe audio duration for %s", path)
            return 0.0

    def get_selected_music(self):
        """Return (path, volume_linear) or (None, None) if disabled/invalid."""
        if not self.parent.add_music_checkbox.isChecked():
            return None, None
        if not self._music_files:
            return None, None
        path = self.parent.music_combo.currentData() or ""
        if not path or not os.path.isfile(path):
            return None, None
        vol_pct = self._music_eff()
        return path, (vol_pct / 100.0)

    def _music_eff(self, raw: int | None = None) -> int:
        """Map slider value -> 0..100 respecting invertedAppearance."""
        v = int(self.parent.music_volume_slider.value() if raw is None else raw)
        if self.parent.music_volume_slider.invertedAppearance():
            return max(0, min(100, self.parent.music_volume_slider.maximum() + self.parent.music_volume_slider.minimum() - v))
        return max(0, min(100, v))

    def _on_music_volume_changed(self, raw: int):
        """Keep label/badge in effective %."""
        try:
            eff = self._music_eff(raw)
            self.parent.music_volume_label.setText(f"{eff}%")
            self.parent.ui_handler._update_music_badge()
        except Exception:
            pass