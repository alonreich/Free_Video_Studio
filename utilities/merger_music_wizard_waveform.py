import os
import time
import tempfile
import subprocess
from PyQt5 import QtCore
from PyQt5.QtCore import Qt
from PyQt5.QtGui import QPixmap

class MergerMusicWizardWaveformMixin:

    def start_waveform_generation(self):
        self.wave_preview.setText("Visualizing audio...")
        self._pm_src = None
        if not self.current_track_path: return
        self.logger.info(f"WIZARD_STEP2: Initializing Waveform Generation for {os.path.basename(self.current_track_path)}")
        self.current_track_dur = self._probe_media_duration(self.current_track_path)
        self.offset_slider.setRange(0, int(self.current_track_dur * 1000))
        self.offset_slider.setValue(0)
        ffmpeg_exe = os.path.join(self.bin_dir, "ffmpeg.exe")
        tf = tempfile.NamedTemporaryFile(suffix=".png", delete=False)
        self._temp_png = tf.name; tf.close()
        self.logger.debug("WIZARD_STEP2: Process Phase 1 - Constructing Filter Chain (Compand -> ShowWavesPic)")
        self.logger.debug("WIZARD_STEP2: Process Phase 2 - Dynamic Range Normalization (Attacks: 0, Peak Cap: -3dB)")
        cmd = [ffmpeg_exe, "-y", "-hide_banner", "-loglevel", "error", "-i", self.current_track_path, "-frames:v", "1", 
               "-filter_complex", "aformat=channel_layouts=mono,compand=attacks=0:decays=0.2:points=-90/-90|-45/-28|-20/-8|0/-2,showwavespic=s=1400x360:colors=0x7DD3FC:scale=sqrt:draw=full", self._temp_png]
        self.logger.info(f"WIZARD_STEP2: Executing FFmpeg (CPU-Bound Rendering): {' '.join(cmd)}")
        try:
            start_t = time.time()
            proc = subprocess.Popen(cmd, creationflags=0x08000000)
            proc.wait(15)
            elapsed = time.time() - start_t
            if os.path.exists(self._temp_png):
                self.logger.info(f"WIZARD_STEP2: Render Complete. Size: {os.path.getsize(self._temp_png)} bytes. Elapsed: {elapsed:.2f}s")
                self._pm_src = QPixmap(self._temp_png)
                self._refresh_wave_scaled()
            else:
                self.logger.error("WIZARD_STEP2: Render Failed - Output file not found.")
        except Exception as e:
            self.logger.error(f"WIZARD_STEP2: Critical Execution Error: {e}")
            self.wave_preview.setText(f"Waveform failed: {e}")
    def _on_slider_seek(self, val_ms):
        if self._dragging or self._wave_dragging: return
        if self._player: self._player.set_time(val_ms)
        self._sync_caret()
    def _on_drag_start(self): self._dragging = True
    def _on_drag_end(self):
        self._dragging = False
        if self._player: self._player.set_time(self.offset_slider.value())
        self._sync_caret()
    def _refresh_wave_scaled(self):
        if not self._pm_src: return
        cr = self.wave_preview.contentsRect()
        scaled = self._pm_src.scaled(cr.size(), Qt.KeepAspectRatio, Qt.SmoothTransformation)
        self.wave_preview.setPixmap(scaled)
        self._draw_w = scaled.width(); self._draw_h = scaled.height()
        self._draw_x0 = (cr.width() - self._draw_w) // 2; self._draw_y0 = (cr.height() - self._draw_h) // 2
        self._sync_caret()
    def eventFilter(self, obj, event):
        if obj is self.wave_preview:
            if event.type() == QtCore.QEvent.MouseButtonPress and event.button() == Qt.LeftButton:
                try:
                    if self._draw_w <= 1: return True
                    self._wave_dragging = True
                    self._set_time_from_wave_x(event.pos().x())
                    return True
                except Exception: return True
            if event.type() == QtCore.QEvent.MouseMove and self._wave_dragging:
                try:
                    self._set_time_from_wave_x(event.pos().x())
                    return True
                except Exception: return True
            if event.type() == QtCore.QEvent.MouseButtonRelease:
                self._wave_dragging = False
                return True
        return super().eventFilter(obj, event)
    def _set_time_from_wave_x(self, x):
        if self._draw_w <= 1: return
        rel = (x - self._draw_x0) / float(self._draw_w)
        rel = max(0.0, min(1.0, rel))
        target_ms = int(rel * self.offset_slider.maximum())
        self.offset_slider.setValue(target_ms)
        if self._player:
            self._player.set_time(target_ms)
            self._last_good_vlc_ms = target_ms
        self._sync_caret()
