import time
from PyQt5 import QtCore
from PyQt5.QtCore import Qt, QTimer
from PyQt5 import QtGui
from PyQt5.QtGui import QPixmap
from ui.widgets.music_wizard_workers import VideoFilmstripWorker, MusicWaveformWorker

class MergerMusicWizardTimelineMixin:
    def _payload_to_pixmap(self, payload):
        """Converts worker payloads to QPixmap on the UI thread (safe for Qt image internals)."""
        try:
            if isinstance(payload, QtGui.QPixmap):
                return payload if not payload.isNull() else None
            if isinstance(payload, QtGui.QImage):
                return QtGui.QPixmap.fromImage(payload) if not payload.isNull() else None
            if isinstance(payload, (bytes, bytearray, memoryview)):
                pm = QPixmap()
                if pm.loadFromData(bytes(payload)) and not pm.isNull():
                    return pm
        except Exception:
            return None
        return None

    def _schedule_timeline_repaint(self):
        """Coalesce heavy timeline background repaints to keep caret/playback smooth."""
        if not hasattr(self, "timeline") or self.timeline is None:
            return
        if not hasattr(self, "_timeline_repaint_timer"):
            self._timeline_repaint_timer = QTimer(self)
            self._timeline_repaint_timer.setSingleShot(True)
            self._timeline_repaint_timer.timeout.connect(self._flush_timeline_repaint)
            self._timeline_repaint_pending = False
        if getattr(self, "_timeline_repaint_pending", False):
            return
        self._timeline_repaint_pending = True
        self._timeline_repaint_timer.start(120)

    def _flush_timeline_repaint(self):
        self._timeline_repaint_pending = False
        if not hasattr(self, "timeline") or self.timeline is None:
            return
        try:
            self.timeline._needs_repaint = True
        except Exception:
            pass
        self.timeline.update()

    def _safe_mpv_loadfile(self, player, path, start_sec=None):
        if not player or not path:
            return False
        try:
            if start_sec is None:
                player.command("loadfile", path, "replace")
            else:
                safe_start = max(0.0, float(start_sec or 0.0))
                player.command("loadfile", path, "replace", f"start={safe_start:.3f}")
            return True
        except Exception as ex:
            self.logger.debug(f"WIZARD_TIMELINE: loadfile failed for {path}: {ex}")
            return False

    def _safe_mpv_seek(self, player, target_sec, *, exact_first=True, label="player"):
        if not player:
            return False
        try:
            safe_target = max(0.0, float(target_sec or 0.0))
        except Exception:
            safe_target = 0.0
        attempts = [True, False] if exact_first else [False, True]
        last_err = None
        for use_exact in attempts:
            try:
                if use_exact:
                    player.seek(safe_target, reference='absolute', precision='exact')
                else:
                    player.seek(safe_target, reference='absolute')
                return True
            except Exception as ex:
                last_err = ex
        self.logger.debug(f"WIZARD_TIMELINE: seek failed ({label} @ {safe_target:.3f}s): {last_err}")
        return False

    def _stop_timeline_workers(self):
        if hasattr(self, '_video_worker') and self._video_worker and self._video_worker.isRunning():
            try:
                self._video_worker.stop()
                self._video_worker.wait(2000)
            except Exception:
                pass
        if hasattr(self, '_music_worker') and self._music_worker and self._music_worker.isRunning():
            try:
                self._music_worker.stop()
                self._music_worker.wait(2000)
            except Exception:
                pass
        self._video_worker = None
        self._music_worker = None
        self._timeline_stage = None

    def _start_timeline_workers(self, video_info_list, unique_music_segments_info, *, stage: str, speed_segments: list = None):
        stage_name = str(stage or "fast").lower()
        chunked_video_info = []
        if stage_name == "fast":
            num_chunks = 2
            max_video_workers = 1
        elif stage_name == "progressive":
            num_chunks = 4
            max_video_workers = 2
        else:
            num_chunks = 6
            max_video_workers = 2
        for orig_idx, (path, duration, t_start, speed) in enumerate(video_info_list):
            chunk_dur = duration / num_chunks
            for i in range(num_chunks):
                c_start_offset_project = i * chunk_dur
                c_start_source = t_start + (c_start_offset_project * speed * 1000.0)
                chunked_video_info.append((
                    path, 
                    chunk_dur, 
                    c_start_source, 
                    speed, 
                    orig_idx
                ))
        self._video_worker = VideoFilmstripWorker(
            chunked_video_info,
            self.bin_dir,
            stage=stage_name,
            max_workers=max_video_workers,
            speed_segments=speed_segments,
        )
        self._video_worker.asset_ready.connect(self._on_video_asset_ready)
        self._video_worker.finished.connect(self._on_worker_finished)
        m_workers = 1
        self._music_worker = MusicWaveformWorker(
            unique_music_segments_info,
            self.bin_dir,
            stage=stage_name,
            max_workers=m_workers,
        )
        self._music_worker.asset_ready.connect(self._on_music_asset_ready)
        self._music_worker.finished.connect(self._on_worker_finished)
        self._workers_running = 2
        self._timeline_stage = stage_name
        self._video_worker.start()
        self._music_worker.start()

    def _on_worker_finished(self, stage: str):
        if stage != getattr(self, "_timeline_stage", None):
            return
        self._workers_running -= 1
        if self._workers_running < 0:
            self._workers_running = 0
        if self._workers_running > 0:
            return
        self.splash_overlay.hide()
        self._schedule_timeline_repaint()

    def _sync_music_only_to_time(self, project_time):
        music_player = getattr(self, "_music_player", None)
        if not music_player:
            return
        is_paused = getattr(music_player, "pause", True)
        if is_paused:
            return
        elapsed = 0.0; target_idx = -1; music_offset = 0.0
        for i, (path, start_off, dur) in enumerate(self.selected_tracks):
            if elapsed + dur > project_time:
                target_idx = i; music_offset = (project_time - elapsed) + start_off; break
            elapsed += dur
        if target_idx != -1:
            target_m_path = self.selected_tracks[target_idx][0]
            curr_m_path = getattr(music_player, "path", "")
            used_start_load = False
            if target_m_path != curr_m_path:
                used_start_load = self._safe_mpv_loadfile(music_player, target_m_path, start_sec=music_offset)
                if not used_start_load:
                    self._safe_mpv_loadfile(music_player, target_m_path)
                self._last_m_mrl = target_m_path
            if not used_start_load:
                self._safe_mpv_seek(music_player, music_offset, exact_first=False, label="music_player")
            music_player.mute = False
            music_player.volume = self._scaled_vol(self.music_vol_slider.value())
        else:
            music_player.stop()
            self._last_m_mrl = ""

    def _ensure_step3_seek_timer(self):
        if hasattr(self, "_step3_seek_timer") and self._step3_seek_timer is not None:
            return
        self._step3_seek_timer = QTimer(self)
        self._step3_seek_timer.setSingleShot(True)
        self._step3_seek_timer.setInterval(55)
        self._step3_seek_timer.timeout.connect(self._flush_pending_step3_seek)
        self._step3_seek_pending_sec = None
        self._last_step3_seek_apply_ts = 0.0
        self._step3_seek_lock_until = 0.0
        self._step3_seek_target_project = 0.0

    def _apply_step3_seek_target(self, target_sec: float):
        safe_sec = max(0.0, min(float(self.total_video_sec), float(target_sec or 0.0)))
        now = time.time()
        self._step3_seek_target_project = safe_sec
        self._step3_seek_lock_until = now + 0.30
        try:
            self._last_good_step3_project_time = safe_sec
            self._last_good_step3_video_ms = float(self._project_time_to_source_ms(safe_sec))
            self._last_clock_ts = now
        except Exception:
            pass
        is_currently_playing = False
        try:
            is_currently_playing = (not bool(getattr(self.player, "pause", True))) and (not bool(getattr(self.player, "idle-active", False)))
        except Exception:
            is_currently_playing = False
        try:
            self._is_syncing = True
            self._sync_all_players_to_time(safe_sec, force_playing=is_currently_playing, seek_exact=True)
        finally:
            self._is_syncing = False
        self._last_step3_seek_apply_ts = now

    def _flush_pending_step3_seek(self):
        pending = getattr(self, "_step3_seek_pending_sec", None)
        self._step3_seek_pending_sec = None
        if pending is None:
            self._is_scrubbing_timeline = False
            return
        if not self.player or self.stack.currentIndex() != 2:
            self._is_scrubbing_timeline = False
            return
        self._apply_step3_seek_target(float(pending))
        self._is_scrubbing_timeline = False

    def _on_timeline_seek(self, pct):
        self._last_seek_ts = time.time(); target_sec = pct * self.total_video_sec
        self.timeline.set_current_time(target_sec)
        if False:
            self._video_player.time_pos = real_v_pos_ms / 1000.0
            self._sync_all_players_to_time(target_sec, force_playing=is_playing)
        if not self.player:
            return
        self._is_scrubbing_timeline = True
        self.timeline.set_current_time(target_sec)
        if self.stack.currentIndex() == 2:
            self._ensure_step3_seek_timer()
            self._step3_seek_pending_sec = float(target_sec)
            if (time.time() - float(getattr(self, "_last_step3_seek_apply_ts", 0.0) or 0.0)) >= 0.055:
                self._flush_pending_step3_seek()
            else:
                self._step3_seek_timer.start()
        else:
            try:
                self.player.pause = True
                if getattr(self, "_music_player", None):
                    self._music_player.pause = True
            except Exception:
                pass
        self._sync_caret()

    def _sync_all_players_to_time(self, timeline_sec, force_playing=None, seek_exact=False):
        if not self.player: return
        music_player = getattr(self, "_music_player", None)
        elapsed = 0.0; target_video_idx = 0; video_offset = 0.0
        for i, seg in enumerate(self.video_segments):
            if elapsed + seg["duration"] > timeline_sec:
                target_video_idx = i; break
            elapsed += seg["duration"]
        target_v_path = self.video_segments[target_video_idx]["path"]
        elapsed_m = 0.0; target_music_idx = -1; music_offset = 0.0
        for i, (path, start_off, dur) in enumerate(self.selected_tracks):
            if elapsed_m + dur > timeline_sec:
                target_music_idx = i; music_offset = (timeline_sec - elapsed_m) + start_off; break
            elapsed_m += dur
        curr_v_path = getattr(self.player, "path", "")
        if target_v_path != curr_v_path:
            self._safe_mpv_loadfile(self.player, target_v_path)
        if music_player:
            if target_music_idx != -1:
                target_m_path = self.selected_tracks[target_music_idx][0]
                curr_m_path = getattr(music_player, "path", "")
                used_start_load = False
                if target_m_path != curr_m_path:
                    used_start_load = self._safe_mpv_loadfile(music_player, target_m_path, start_sec=music_offset)
                    if not used_start_load:
                        self._safe_mpv_loadfile(music_player, target_m_path)
                    self._last_m_mrl = target_m_path
                if not used_start_load:
                    self._safe_mpv_seek(music_player, music_offset, exact_first=bool(seek_exact), label="music_player")
            else:
                music_player.stop()
                self._last_m_mrl = ""
        real_v_pos_ms = self._project_time_to_source_ms(timeline_sec)
        self._safe_mpv_seek(self.player, real_v_pos_ms / 1000.0, exact_first=bool(seek_exact), label="video_player")
        try:
            self._last_good_step3_video_ms = float(real_v_pos_ms)
            self._last_good_step3_project_time = float(timeline_sec)
        except Exception:
            pass
        if force_playing is True:
            try:
                self.player.pause = False
                self.player.speed = self.speed_factor
            except Exception:
                pass
            if music_player and target_music_idx != -1:
                try:
                    music_player.pause = False
                    music_player.speed = 1.0
                except Exception:
                    pass
        elif force_playing is False:
            try:
                self.player.pause = True
            except Exception:
                pass
            if music_player:
                try:
                    music_player.pause = True
                except Exception:
                    pass

    def _prepare_timeline_data(self):
        videos = []; video_info_list = []
        if hasattr(self.parent_window, "listw"):
            for i in range(self.parent_window.listw.count()):
                it = self.parent_window.listw.item(i); p = it.data(Qt.UserRole); probe_data = it.data(Qt.UserRole + 1) or {}
                dur = float(probe_data.get("format", {}).get("duration", 0.0))
                videos.append({"path": p, "duration": dur, "thumbs": []}); video_info_list.append((p, dur, 0.0, 1.0))
        else:
            p = getattr(self.parent_window, "input_file_path", None)
            dur = float(self.total_video_sec)
            if p:
                videos.append({"path": p, "duration": dur, "thumbs": []})
                video_info_list.append((p, dur, self.trim_start_ms, self.speed_factor))
        self.video_segments = videos
        music = []; music_segments_info = []
        if self.selected_tracks:
            covered = 0.0
            for p, offset, dur in self.selected_tracks:
                music.append({"path": p, "duration": dur, "offset": offset, "wave": QPixmap()})
                music_segments_info.append((p, offset, dur))
                covered += dur
            cycle_limit = 0
            while covered < self.total_video_sec - 0.1 and cycle_limit < 20:
                p, _, _ = self.selected_tracks[-1]
                full_dur = self._probe_media_duration(p)
                remaining = self.total_video_sec - covered
                seg_dur = min(full_dur, remaining)
                music.append({"path": p, "duration": seg_dur, "offset": 0.0, "wave": QPixmap()})
                music_segments_info.append((p, 0.0, seg_dur))
                covered += seg_dur
                cycle_limit += 1
        self.music_segments = music; self.timeline.set_data(self.total_video_sec, self.video_segments, self.music_segments)
        self._music_worker_targets = {}
        unique_music_segments_info = []
        _key_to_worker_idx = {}
        for seg_idx, (p, offset, dur) in enumerate(music_segments_info):
            key = (str(p), round(float(offset), 3), round(float(dur), 3))
            w_idx = _key_to_worker_idx.get(key)
            if w_idx is None:
                w_idx = len(unique_music_segments_info)
                _key_to_worker_idx[key] = w_idx
                unique_music_segments_info.append((p, offset, dur))
            self._music_worker_targets.setdefault(w_idx, []).append(seg_idx)
        self._timeline_video_info_list = video_info_list
        self._timeline_unique_music_info = unique_music_segments_info
        self._workers_running = 0
        self._stop_timeline_workers()
        self.splash_overlay.hide()
        self._start_timeline_workers(video_info_list, unique_music_segments_info, stage="progressive", speed_segments=self.speed_segments)

    def _on_video_asset_ready(self, idx, thumbs, stage):
        if stage != getattr(self, "_timeline_stage", None):
            return
        if 0 <= idx < len(self.video_segments):
            existing = self.video_segments[idx].get("thumbs", []) or []
            safe_thumbs = []
            for t in (thumbs or []):
                pm = self._payload_to_pixmap(t)
                if pm is not None:
                    safe_thumbs.append(pm)
            if safe_thumbs:
                merged = list(existing) + safe_thumbs
                self.video_segments[idx]["thumbs"] = merged[-140:]
            self._schedule_timeline_repaint()

    def _on_music_asset_ready(self, idx, pixmap, stage):
        if stage != getattr(self, "_timeline_stage", None):
            return
        safe_pm = self._payload_to_pixmap(pixmap)
        if safe_pm is None:
            return
        targets = getattr(self, "_music_worker_targets", {}).get(idx, [idx])
        did_update = False
        for seg_idx in targets:
            if 0 <= seg_idx < len(self.music_segments):
                self.music_segments[seg_idx]["wave"] = safe_pm
                did_update = True
        if did_update:
            self._schedule_timeline_repaint()
