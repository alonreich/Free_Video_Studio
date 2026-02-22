import os
import sys
import subprocess
from PyQt5.QtCore import QPoint, QRect, QTimer
from PyQt5.QtWidgets import QApplication, QLabel

class MergerMusicWizardMiscMixin:
    def _bind_video_output(self):
        if not getattr(self, "_video_player", None): return
        if not hasattr(self, "video_container"): return
        try:
            wid = int(self.video_container.winId())
            self._video_player.wid = wid
        except: pass

    def _get_default_size(self, step_idx):
        """Returns (width, height) for a step if no config exists."""
        if step_idx == 0: return (1100, 850)
        if step_idx == 1: return (1300, 580)
        if step_idx == 2: return (1600, 850)
        return (1300, 850)

    def _apply_step_geometry(self, step_idx):
        """Applies saved geometry or centers the default size with multi-monitor sanity."""
        if not hasattr(self, "parent_window") or not hasattr(self.parent_window, "config_manager"): return
        
        def _do_apply():
            self._is_applying_geometry = True
            try:
                cfg = self.parent_window.config_manager.config
                geo_map = cfg.get("music_wizard_custom_geo", {})
                key = f"step_{step_idx}"
                w_def, h_def = self._get_default_size(step_idx)
                self.setMinimumSize(600, 400)
                app = QApplication.instance()
                screen = app.primaryScreen()
                if self.parent_window:
                    try: screen = self.parent_window.screen() or screen
                    except: pass
                screen_geo = screen.availableGeometry()
                if key in geo_map:
                    g = geo_map[key]
                    target_x, target_y = g['x'], g['y']
                    target_w, target_h = g['w'], g['h']
                    if target_x < screen_geo.left() - target_w // 2 or \
                       target_x > screen_geo.right() - 50 or \
                       target_y < screen_geo.top() - 20 or \
                       target_y > screen_geo.bottom() - 50:
                        target_x = screen_geo.left() + (screen_geo.width() - target_w) // 2
                        target_y = screen_geo.top() + (screen_geo.height() - target_h) // 2
                    self.setGeometry(target_x, target_y, target_w, target_h)
                else:
                    self.resize(w_def, h_def)
                    my_geo = self.frameGeometry()
                    center = screen_geo.center()
                    my_geo.moveCenter(center)
                    self.move(my_geo.topLeft())
            finally:
                QTimer.singleShot(200, lambda: setattr(self, "_is_applying_geometry", False))
        QTimer.singleShot(0, _do_apply)

    def _save_step_geometry(self):
        """Saves current geometry for the active step into config."""
        if not getattr(self, "_startup_complete", False): return
        if getattr(self, "_is_applying_geometry", False): return
        if not hasattr(self.parent_window, "config_manager"): return
        if hasattr(self, "_save_geo_timer") and self._save_geo_timer.isActive():
            return
        if not hasattr(self, "_save_geo_timer"):
            self._save_geo_timer = QTimer(self)
            self._save_geo_timer.setSingleShot(True)
            self._save_geo_timer.setInterval(1000)
            self._save_geo_timer.timeout.connect(self._do_save_step_geometry)
        self._save_geo_timer.start()

    def _do_save_step_geometry(self):
        try:
            step_idx = self.stack.currentIndex()
            geom = self.geometry()
            cfg = dict(self.parent_window.config_manager.config)
            if "music_wizard_custom_geo" not in cfg:
                cfg["music_wizard_custom_geo"] = {}
            cfg["music_wizard_custom_geo"][f"step_{step_idx}"] = {
                'x': geom.x(), 'y': geom.y(), 'w': geom.width(), 'h': geom.height()
            }
            self.parent_window.config_manager.save_config(cfg)
        except: pass

    def moveEvent(self, event):
        super().moveEvent(event)
        self._save_step_geometry()

    def resizeEvent(self, event):
        super().resizeEvent(event)
        if hasattr(self, "_save_step_geometry"):
            self._save_step_geometry()
        if hasattr(self, "stack") and self.stack.currentIndex() == 1:
            if hasattr(self, "_refresh_wave_scaled"):
                self._refresh_wave_scaled()

    def _sync_caret(self, override_ms=None, override_state=None):
        try:
            curr_idx = self.stack.currentIndex()
            if curr_idx == 1:
                show_st2 = getattr(self, "_show_caret_step2", False)
                if not show_st2 and hasattr(self, "offset_slider") and self.offset_slider.maximum() > 0:
                    self._show_caret_step2 = True
                    show_st2 = True
                if not show_st2 or not self.wave_preview.isVisible(): 
                    self._wave_caret.hide()
                    self._wave_time_badge.hide()
                    self._wave_time_badge_bottom.hide()
                    return
                max_ms = self.offset_slider.maximum()
                if max_ms <= 0: return
                val_ms = override_ms if override_ms is not None else self.offset_slider.value()
                frac = val_ms / float(max_ms)
                label_pos = self.wave_preview.mapTo(self, QPoint(0, 0))
                draw_w = getattr(self, "_draw_w", 0)
                if draw_w <= 0:
                    draw_w = self.wave_preview.width()
                    draw_x0 = 0
                else:
                    draw_x0 = getattr(self, "_draw_x0", 0)
                x = label_pos.x() + draw_x0 + int(frac * draw_w) - 1
                y = label_pos.y() + getattr(self, "_draw_y0", 0)
                total_h = getattr(self, "_draw_h", 0) or self.wave_preview.height() or 265
                self._wave_caret.setGeometry(x, y, 1, total_h)
                self._wave_caret.show()
                self._wave_caret.raise_()
                time_str = self._format_time_long(val_ms)
                self._wave_time_badge.setText(time_str); self._wave_time_badge.adjustSize()
                self._wave_time_badge_bottom.setText(time_str); self._wave_time_badge_bottom.adjustSize()
                bw = self._wave_time_badge.width()
                self._wave_time_badge.move(x - bw // 2, y - 25); self._wave_time_badge.show(); self._wave_time_badge.raise_()
                self._wave_time_badge_bottom.move(x - bw // 2, y + total_h + 5); self._wave_time_badge_bottom.show(); self._wave_time_badge_bottom.raise_()
            elif curr_idx == 2:
                self._wave_caret.hide()
                self._wave_time_badge.hide()
                self._wave_time_badge_bottom.hide()
            else: 
                self._wave_caret.hide(); self._wave_time_badge.hide(); self._wave_time_badge_bottom.hide()
        except: pass

    def _scaled_vol(self, mix_val):
        """Scale a 0-100 mix value by the parent's master monitor percentage."""
        try:
            if hasattr(self.parent_window, "_vol_eff"):
                master_ratio = self.parent_window._vol_eff() / 100.0
                final_vol = int(max(0, min(100, mix_val * master_ratio)))
                self.logger.info(f"DEBUG_SCALED_VOL: Mix={mix_val}% Master={master_ratio*100:.1f}% -> Final={final_vol}%")
                return final_vol
        except: pass
        return int(max(0, min(100, mix_val)))

    def _format_time_long(self, ms):
        total_seconds = int(ms / 1000)
        hours = total_seconds // 3600
        minutes = (total_seconds % 3600) // 60
        seconds = total_seconds % 60
        if hours > 0: return f"{hours}:{minutes:02d}:{seconds:02d}"
        return f"{minutes:02d}:{seconds:02d}"

    def _probe_media_duration(self, path):
        try:
            ffprobe = os.path.join(self.bin_dir, "ffprobe.exe")
            cmd = [ffprobe, "-v", "error", "-show_entries", "format=duration", "-of", "default=noprint_wrappers=1:nokey=1", path]
            r = subprocess.run(cmd, capture_output=True, text=True, creationflags=0x08000000, timeout=5)
            return float(r.stdout.strip()) if r.returncode == 0 else 0.0
        except: return 0.0

    def stop_previews(self):
        if hasattr(self, '_stop_waveform_worker'):
            try: self._stop_waveform_worker()
            except: pass
        if hasattr(self, '_player') and self._player: self._player.stop()
        if hasattr(self, '_video_player') and self._video_player: self._video_player.stop()
        if hasattr(self, '_play_timer'): self._play_timer.stop()

    def update_coverage_ui(self):
        covered = sum(t[2] for t in self.selected_tracks)
        pct = int((covered / self.total_video_sec) * 100) if self.total_video_sec > 0 else 0
        self.coverage_progress.setValue(min(100, pct))
        self.coverage_progress.setFormat(f"Music Coverage: {covered:.1f}s / {self.total_video_sec:.1f}s (%p%)")

    def _cache_wall_times(self):
        """Pre-calculates wall-clock durations for speed segments to avoid O(N) math in tick."""
        self._cached_wall_durations = []
        for seg in self.speed_segments:
            dur_source = seg['end'] - seg['start']
            self._cached_wall_durations.append(dur_source / seg['speed'])
        self._wall_trim_start = self._calculate_wall_clock_time_raw(self.trim_start_ms, self.speed_segments, self.speed_factor)

    def _calculate_wall_clock_time_raw(self, video_ms, segments, base_speed):
        """Internal raw math without using cache. Returns SECONDS."""
        target = float(video_ms)
        base_speed = base_speed or 1.1
        if not segments: return target / (1000.0 * base_speed)
        if target < segments[0]['start']: return target / (1000.0 * base_speed)
        current_video_time = 0.0
        accumulated_wall_ms = 0.0
        for seg in segments:
            start, end, speed = seg['start'], seg['end'], seg['speed']
            if start >= target: break
            if start > current_video_time:
                accumulated_wall_ms += (start - current_video_time) / base_speed
                current_video_time = start
            if target < end:
                accumulated_wall_ms += (target - start) / speed
                current_video_time = target
                break
            else:
                accumulated_wall_ms += (end - start) / speed
                current_video_time = end
        if current_video_time < target:
            accumulated_wall_ms += (target - current_video_time) / base_speed
        return accumulated_wall_ms / 1000.0

    def _calculate_wall_clock_time(self, video_ms, segments, base_speed):
        if video_ms == self.trim_start_ms and hasattr(self, "_wall_trim_start"):
            return self._wall_trim_start
        return self._calculate_wall_clock_time_raw(video_ms, segments, base_speed)

    def _project_time_to_source_ms(self, project_sec):
        target_wall_ms = (project_sec * 1000.0) + (self._wall_trim_start * 1000.0)
        if not self.speed_segments:
            return int(target_wall_ms * self.speed_factor)
        accumulated_wall_ms = 0.0
        first_start = self.speed_segments[0]['start']
        wall_to_first = first_start / self.speed_factor
        if target_wall_ms <= wall_to_first:
            return int(target_wall_ms * self.speed_factor)
        accumulated_wall_ms = wall_to_first
        for i, seg in enumerate(self.speed_segments):
            start, end, speed = seg['start'], seg['end'], seg['speed']
            seg_dur_wall = (end - start) / speed
            if accumulated_wall_ms + seg_dur_wall >= target_wall_ms:
                return int(start + ((target_wall_ms - accumulated_wall_ms) * speed))
            accumulated_wall_ms += seg_dur_wall
            next_start = self.speed_segments[i+1]['start'] if i+1 < len(self.speed_segments) else float('inf')
            gap_wall = (next_start - end) / self.speed_factor
            if accumulated_wall_ms + gap_wall >= target_wall_ms:
                return int(end + ((target_wall_ms - accumulated_wall_ms) * self.speed_factor))
            accumulated_wall_ms += gap_wall
        return int(self.speed_segments[-1]['end'] + ((target_wall_ms - accumulated_wall_ms) * self.speed_factor))

    def _on_search_changed(self, text): 
        if hasattr(self, "_search_timer"):
            self._search_timer.start(300)

    def _do_search(self):
        txt = self.search_input.text().lower()
        for i in range(self.track_list.count()):
            it = self.track_list.item(i); w = self.track_list.itemWidget(it)
            it.setHidden(txt not in w.name_lbl.text().lower() if w else False)
