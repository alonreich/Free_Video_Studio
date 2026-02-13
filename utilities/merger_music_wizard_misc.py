import os
import sys
import subprocess
from PyQt5.QtCore import QPoint
from PyQt5.QtWidgets import QApplication

class MergerMusicWizardMiscMixin:

    def _bind_video_output(self):
        if not getattr(self, "_video_player", None):
            return
        if not hasattr(self, "video_container"):
            return
        try:
            wid = int(self.video_container.winId())
        except Exception:
            return
        try:
            if sys.platform.startswith("win") and hasattr(self._video_player, "set_hwnd"):
                self._video_player.set_hwnd(wid)
            elif sys.platform.startswith("linux") and hasattr(self._video_player, "set_xwindow"):
                self._video_player.set_xwindow(wid)
            elif sys.platform == "darwin" and hasattr(self._video_player, "set_nsobject"):
                self._video_player.set_nsobject(wid)
        except Exception as ex:
            try:
                self.logger.debug(f"WIZARD: Video output binding skipped: {ex}")
            except Exception:
                pass

    def _sync_caret(self):
        try:
            curr_idx = self.stack.currentIndex()
            if curr_idx == 1:
                if not self.wave_preview.isVisible(): self._wave_caret.hide(); return
                max_ms = self.offset_slider.maximum()
                if max_ms <= 0: self._wave_caret.hide(); return
                frac = self.offset_slider.value() / float(max_ms)
                label_pos = self.wave_preview.mapTo(self, QPoint(0, 0))
                x = label_pos.x() + self._draw_x0 + int(frac * self._draw_w) - 1; y = label_pos.y() + self._draw_y0
                self._wave_caret.setGeometry(x, y, 2, self._draw_h); self._wave_caret.show(); self._wave_caret.raise_()
            elif curr_idx == 2:
                if not self.timeline.isVisible(): self._wave_caret.hide(); return
                frac = self.timeline.current_time / self.total_video_sec
                tl_pos = self.timeline.mapTo(self, QPoint(0, 0))
                x = tl_pos.x() + int(frac * self.timeline.width()) - 1; y = tl_pos.y(); h = self.timeline.height()
                self._wave_caret.setGeometry(x, y, 2, h); self._wave_caret.show(); self._wave_caret.raise_()
            else: self._wave_caret.hide()
        except: self._wave_caret.hide()
    def _probe_media_duration(self, path):
        try:
            ffprobe = os.path.join(self.bin_dir, "ffprobe.exe")
            cmd = [ffprobe, "-v", "error", "-show_entries", "format=duration", "-of", "default=noprint_wrappers=1:nokey=1", path]
            r = subprocess.run(cmd, capture_output=True, text=True, creationflags=0x08000000, timeout=5)
            return float(r.stdout.strip()) if r.returncode == 0 else 0.0
        except: return 0.0
    def _restore_geometry(self):
        try:
            if not hasattr(self.parent_window, "config_manager") or not self.parent_window.config_manager:
                self._center_on_primary()
                return
            cfg = self.parent_window.config_manager.config
            geom = cfg.get("music_wizard_geometry")
            if geom:
                from PyQt5.QtCore import QByteArray
                self.restoreGeometry(QByteArray.fromBase64(geom.encode()))
            else:
                self._center_on_primary()
        except Exception:
            self._center_on_primary()
    def _center_on_primary(self, w=1300, h=650):
        screen = QApplication.primaryScreen().availableGeometry()
        x = screen.x() + (screen.width() - w) // 2
        y = screen.y() + (screen.height() - h) // 2
        self.setGeometry(x, y, w, h)
    def _save_geometry(self):
        try:
            if not hasattr(self.parent_window, "config_manager") or not self.parent_window.config_manager:
                return
            cfg = dict(self.parent_window.config_manager.config)
            cfg["music_wizard_geometry"] = self.saveGeometry().toBase64().data().decode()
            self.parent_window.config_manager.save_config(cfg)
        except Exception:
            pass
    def stop_previews(self):
        if self._player: self._player.stop()
        if self._video_player: self._video_player.stop()
        if hasattr(self, '_play_timer'): self._play_timer.stop()
    def closeEvent(self, e):
        self._save_geometry()
        self.stop_previews()
        super().closeEvent(e)
    def _on_search_changed(self, text): self._search_timer.start(300)
    def _do_search(self):
        txt = self.search_input.text().lower()
        for i in range(self.track_list.count()):
            it = self.track_list.item(i); w = self.track_list.itemWidget(it)
            it.setHidden(txt not in w.name_lbl.text().lower() if w else False)
    def update_coverage_ui(self):
        covered = sum(t[2] for t in self.selected_tracks)
        pct = int((covered / self.total_video_sec) * 100) if self.total_video_sec > 0 else 0
        self.coverage_progress.setValue(min(100, pct))
        self.coverage_progress.setFormat(f"Music Coverage: {covered:.1f}s / {self.total_video_sec:.1f}s (%p%)")
