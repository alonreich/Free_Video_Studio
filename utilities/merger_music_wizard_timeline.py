import time
from PyQt5 import QtCore
from PyQt5.QtCore import Qt
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

    def _start_timeline_workers(self, video_info_list, unique_music_segments_info, *, stage: str):
        stage_name = str(stage or "fast").lower()
        if stage_name in ("fast", "progressive"):
            v_workers = 2
            m_workers = 2
        else:
            v_workers = 1
            m_workers = 1
        self._video_worker = VideoFilmstripWorker(
            video_info_list,
            self.bin_dir,
            stage=stage_name,
            max_workers=v_workers,
        )
        self._video_worker.asset_ready.connect(self._on_video_asset_ready)
        self._video_worker.finished.connect(self._on_worker_finished)
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
        if not self._video_player: return
        v_state = self._video_player.get_state()
        if v_state != 3:
            if self._player: self._player.pause()
            return
        elapsed = 0.0; target_idx = -1; music_offset = 0.0
        for i, (path, start_off, dur) in enumerate(self.selected_tracks):
            if elapsed + dur > project_time:
                target_idx = i; music_offset = (project_time - elapsed) + start_off; break
            elapsed += dur
        if target_idx != -1:
            target_path = self.selected_tracks[target_idx][0]
            if target_path != self._last_m_mrl:
                m = self.vlc.media_new(target_path); self._player.set_media(m); self._player.play(); self._player.set_time(int(music_offset * 1000)); self._last_m_mrl = target_path
            else:
                try:
                    curr_audio_time = self._player.get_time() / 1000.0
                    if abs(curr_audio_time - music_offset) > 0.5: self._player.set_time(int(music_offset * 1000))
                    if self._player.get_state() != 3: self._player.play()
                except Exception as ex:
                    self.logger.debug("WIZARD: music sync drift correction skipped: %s", ex)
        else:
            self._player.stop(); self._last_m_mrl = ""

    def _on_timeline_seek(self, pct):
        self._last_seek_ts = time.time(); target_sec = pct * self.total_video_sec
        is_playing = False
        if self._video_player: is_playing = (self._video_player.get_state() == 3)
        self.timeline.set_current_time(target_sec)
        if self.stack.currentIndex() == 2 and self._video_player:
            target_vid_idx = len(self.video_segments) - 1; video_offset = 0.0; current_count_elapsed = 0.0
            for i, seg in enumerate(self.video_segments):
                if current_count_elapsed + seg["duration"] > target_sec + 0.001:
                    target_vid_idx = i; video_offset = target_sec - current_count_elapsed; break
                current_count_elapsed += seg["duration"]
            final_elapsed = 0.0
            for i in range(target_vid_idx): final_elapsed += self.video_segments[i]["duration"]
            self._current_elapsed_offset = final_elapsed
            target_path = self.video_segments[target_vid_idx]["path"]
            curr_media = self._video_player.get_media()
            curr_mrl = str(curr_media.get_mrl()).replace("%20", " ") if curr_media else ""
            if target_path.replace("\\", "/").lower() not in curr_mrl.lower():
                m = self.vlc.media_new(target_path); self._video_player.set_media(m)
                if is_playing: self._video_player.play()
            self._video_player.set_time(int(video_offset * 1000))
            if not is_playing: self._video_player.set_pause(True)
        self._sync_all_players_to_time(target_sec)
        if not is_playing:
            if self._video_player: self._video_player.set_pause(True)
            if self._player: self._player.set_pause(True)
        self._sync_caret()

    def _sync_all_players_to_time(self, timeline_sec):
        elapsed = 0.0; target_video_idx = 0; video_offset = 0.0
        for i, seg in enumerate(self.video_segments):
            if elapsed + seg["duration"] > timeline_sec:
                target_video_idx = i; video_offset = timeline_sec - elapsed; break
            elapsed += seg["duration"]
        if self._video_player:
            target_path = self.video_segments[target_video_idx]["path"]; curr_media = self._video_player.get_media()
            if not curr_media or target_path.replace("\\", "/") not in str(curr_media.get_mrl()).replace("%20", " "):
                m = self.vlc.media_new(target_path); self._video_player.set_media(m); self._video_player.play()
            self._video_player.set_time(int(video_offset * 1000))
        elapsed = 0.0; target_music_idx = -1; music_offset = 0.0
        for i, (path, start_off, dur) in enumerate(self.selected_tracks):
            if elapsed + dur > timeline_sec:
                target_music_idx = i; music_offset = (timeline_sec - elapsed) + start_off; break
            elapsed += dur
        if self._player:
            if target_music_idx != -1:
                target_path = self.selected_tracks[target_music_idx][0]
                if target_path != self._last_m_mrl:
                    m = self.vlc.media_new(target_path); self._player.set_media(m); self._player.play(); self._last_m_mrl = target_path
                self._player.set_time(int(music_offset * 1000))
            else: self._player.stop()

    def _prepare_timeline_data(self):
        videos = []; video_info_list = []
        for i in range(self.parent_window.listw.count()):
            it = self.parent_window.listw.item(i); p = it.data(Qt.UserRole); probe_data = it.data(Qt.UserRole + 1) or {}
            dur = float(probe_data.get("format", {}).get("duration", 0.0))
            videos.append({"path": p, "duration": dur, "thumbs": []}); video_info_list.append((p, dur))
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
        self._start_timeline_workers(video_info_list, unique_music_segments_info, stage="progressive")

    def _on_video_asset_ready(self, idx, thumbs, stage):
        if stage != getattr(self, "_timeline_stage", None):
            return
        if 0 <= idx < len(self.video_segments): self.video_segments[idx]["thumbs"] = thumbs; self.timeline.update()

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
