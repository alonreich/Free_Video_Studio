import time
from PyQt5 import QtCore
from PyQt5.QtCore import Qt, QTimer
from PyQt5.QtGui import QPixmap
from utilities.merger_music_wizard_workers import VideoFilmstripWorker, MusicWaveformWorker

class MergerMusicWizardTimelineMixin:
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
        num_chunks = 8 if stage_name in ("fast", "progressive") else 4
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
            max_workers=num_chunks,
            speed_segments=speed_segments,
        )
        self._video_worker.asset_ready.connect(self._on_video_asset_ready)
        self._video_worker.finished.connect(self._on_worker_finished)
        m_workers = 2 if stage_name in ("fast", "progressive") else 1
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

    def _sync_music_only_to_time(self, project_time):
        if not self.player: return
        is_paused = getattr(self.player, "pause", True)
        if is_paused:
            return
        elapsed = 0.0; target_idx = -1; music_offset = 0.0
        for i, (path, start_off, dur) in enumerate(self.selected_tracks):
            if elapsed + dur > project_time:
                target_idx = i; music_offset = (project_time - elapsed) + start_off; break
            elapsed += dur
        if target_idx != -1:
            self.player.seek(music_offset, reference='absolute', precision='exact')
        else:
            self.player.stop(); self._last_m_mrl = ""

    def _on_timeline_seek(self, pct):
        self._last_seek_ts = time.time(); target_sec = pct * self.total_video_sec
        self.timeline.set_current_time(target_sec)
        if False:
            self._video_player.set_time(real_v_pos_ms)
            self._sync_all_players_to_time(target_sec)
        if not self.player: return
        is_paused = getattr(self.player, "pause", True)
        self.timeline.set_current_time(target_sec)
        if self.stack.currentIndex() == 2:
            self._sync_all_players_to_time(target_sec)
        if is_paused:
            self.player.pause = True
        self._sync_caret()

    def _sync_all_players_to_time(self, timeline_sec):
        if not self.player: return
        elapsed = 0.0; target_video_idx = 0; video_offset = 0.0
        elapsed = 0.0
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
            self.player.command("loadfile", target_v_path, "replace")
            self._last_m_mrl = ""
            self.player.mute = False
            if hasattr(self, "video_vol_slider"):
                self.player.volume = self._scaled_vol(self.video_vol_slider.value())
        if target_music_idx != -1:
            target_m_path = self.selected_tracks[target_music_idx][0]
            if target_m_path != self._last_m_mrl:
                self.player.audio_add(target_m_path)
                self._last_m_mrl = target_m_path
                
                def _set_vol():
                    if self.player:
                        self.player.volume = self._scaled_vol(self.music_vol_slider.value())
                QTimer.singleShot(200, _set_vol)
        real_v_pos_ms = self._project_time_to_source_ms(timeline_sec)
        self.player.seek(real_v_pos_ms / 1000.0, reference='absolute', precision='exact')

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
                music.append({"path": p, "duration": dur, "offset": offset, "wave": QPixmap()}); music_segments_info.append((p, offset, dur)); covered += dur
            cycle_limit = 0
            while covered < self.total_video_sec - 0.1 and cycle_limit < 20:
                p, _, _ = self.selected_tracks[-1]; full_dur = self._probe_media_duration(p)
                music.append({"path": p, "duration": full_dur, "offset": 0.0, "wave": QPixmap()}); music_segments_info.append((p, 0.0, full_dur)); covered += full_dur; cycle_limit += 1
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
            existing = self.video_segments[idx].get("thumbs", [])
            self.video_segments[idx]["thumbs"] = thumbs
            self.timeline.update()

    def _on_music_asset_ready(self, idx, pixmap, stage):
        if stage != getattr(self, "_timeline_stage", None):
            return
        targets = getattr(self, "_music_worker_targets", {}).get(idx, [idx])
        did_update = False
        for seg_idx in targets:
            if 0 <= seg_idx < len(self.music_segments):
                self.music_segments[seg_idx]["wave"] = pixmap
                did_update = True
        if did_update:
            self.timeline.update()
