import subprocess
import json
import os
import tempfile
import time
from pathlib import Path
from PyQt5.QtCore import QProcess, Qt, QThread, pyqtSignal
from PyQt5.QtWidgets import QMessageBox, QApplication
from utilities.merger_utils import _human, _get_logger
from utilities.merger_ffmpeg_process import FFMpegProcessMixin
from processing.encoders import EncoderManager

class ProbeWorker(QThread):
    """Background worker to probe files without freezing UI."""
    finished = pyqtSignal(list, bool)
    progress = pyqtSignal(str)

    def __init__(self, handler, paths):
        super().__init__()
        self.handler = handler
        self.paths = paths

    def run(self):
        use_fallback = False
        if not self.paths:
            self.finished.emit([], False)
            return
        self.progress.emit("Analyzing 1/{}...".format(len(self.paths)))
        first = self.handler._probe_file(self.paths[0])
        if not first:
            use_fallback = True
        else:
            ref_w = first.get('width')
            ref_h = first.get('height')
            ref_codec = first.get('codec_name')
            ref_ar = first.get('sample_rate')
            for i, p in enumerate(self.paths):
                if i == 0: continue
                self.progress.emit(f"Analyzing {i+1}/{len(self.paths)}...")
                m = self.handler._probe_file(p)
                if not m:
                    use_fallback = True
                    break
                if (m.get('width') != ref_w or 
                    m.get('height') != ref_h or 
                    m.get('codec_name') != ref_codec or
                    m.get('sample_rate') != ref_ar):
                    use_fallback = True
                    break
        self.finished.emit(self.paths, use_fallback)

class FFMpegHandler(FFMpegProcessMixin):
    def __init__(self, parent):
        self.parent = parent
        self.logger = _get_logger()
        self.process = None
        self._temp_dir = None
        self._output_path = ""
        self._cmd = []
        self._probe_worker = None

    def _probe_file(self, path):
        """Helper to get resolution, codec, and audio sample rate safely."""
        try:
            bin_dir = self.parent.bin_dir
            ffprobe = os.path.join(bin_dir, "ffprobe.exe")
            if not os.path.exists(ffprobe):
                ffprobe = "ffprobe"
            cmd = [
                ffprobe, "-v", "error", 
                "-show_entries", "stream=width,height,codec_name,r_frame_rate,sample_rate", 
                "-of", "json", path
            ]
            flags = 0
            if os.name == 'nt':
                flags = subprocess.CREATE_NO_WINDOW
            r = subprocess.run(cmd, capture_output=True, text=True, creationflags=flags)
            if r.returncode != 0:
                return None
            data = json.loads(r.stdout)
            if not data.get('streams'):
                return None
            vid = None
            aud = None
            for s in data['streams']:
                if 'width' in s and 'height' in s and not vid:
                    vid = s
                if 'sample_rate' in s and not aud:
                    aud = s
            if not vid: 
                return None
            result = vid.copy()
            if aud:
                result['sample_rate'] = aud.get('sample_rate')
            else:
                result['sample_rate'] = "none"
            return result
        except Exception as e:
            self.logger.error(f"Probe failed for {path}: {e}")
            return None

    def merge_now(self):
        n = self.parent.listw.count()
        if n < 2:
            QMessageBox.information(self.parent, "Need more videos", "Please add at least 2 videos to merge.")
            return
        items = [self.parent.listw.item(i) for i in range(n)]
        paths = [it.data(Qt.UserRole) for it in items]
        self.parent.set_ui_busy(True)
        self._probe_worker = ProbeWorker(self, paths)
        if hasattr(self.parent, "status_updated"):
            self._probe_worker.progress.connect(self.parent.status_updated.emit)
        self._probe_worker.finished.connect(self._on_probe_finished)
        self._probe_worker.start()

    def _on_probe_finished(self, paths, use_fallback):
        """Called when probing is complete. Resumes merge logic on main thread."""
        try:
            if use_fallback:
                self.logger.info("Probe Result: Mismatch (Audio/Video). Switching to Smart Transcode.")
            last_out_dir = self.parent._last_out_dir if self.parent._last_out_dir and Path(self.parent._last_out_dir).exists() else str(Path.home() / "Downloads")
            default_path = str(Path(last_out_dir) / "merged_video.mp4")
            out_path, _ = self.parent.open_save_dialog(default_path)
            if not out_path:
                self.parent.set_ui_busy(False)
                return
            self.parent._last_out_dir = str(Path(out_path).parent)
            self.parent.logic_handler.save_config()
            self._output_path = out_path
            self._temp_dir = tempfile.TemporaryDirectory()
            self._start_ffmpeg_process(paths, out_path, use_fallback)
        except Exception as e:
            self.logger.error(f"Error in merge setup: {e}")
            self.parent.set_ui_busy(False)

    def _get_target_resolution(self, paths):
        """Determine target resolution based on input videos with bounds checking."""
        default_resolution = (1920, 1080)
        max_resolution = (8192, 4320)
        min_resolution = (64, 64)
        resolutions = []
        for path in paths:
            probe_data = self._probe_file(path)
            if probe_data and 'width' in probe_data and 'height' in probe_data:
                width = probe_data.get('width')
                height = probe_data.get('height')
                if width and height:
                    width = max(min_resolution[0], min(width, max_resolution[0]))
                    height = max(min_resolution[1], min(height, max_resolution[1]))
                    width = width if width % 2 == 0 else width + 1
                    height = height if height % 2 == 0 else height + 1
                    resolutions.append((width, height))
        if not resolutions:
            return default_resolution

        from collections import Counter
        resolution_counts = Counter(resolutions)
        most_common = resolution_counts.most_common(1)
        if most_common:
            target_w, target_h = most_common[0][0]
            target_w = max(min_resolution[0], min(target_w, max_resolution[0]))
            target_h = max(min_resolution[1], min(target_h, max_resolution[1]))
            target_w = target_w if target_w % 2 == 0 else target_w + 1
            target_h = target_h if target_h % 2 == 0 else target_h + 1
            return (target_w, target_h)
        return default_resolution
    
    def _get_target_framerate(self, paths):
        """Determine target framerate based on input videos."""
        default_fps = 30
        framerates = []
        for path in paths:
            probe_data = self._probe_file(path)
            if probe_data and 'r_frame_rate' in probe_data:
                r_frame_rate = probe_data.get('r_frame_rate')
                if r_frame_rate and '/' in r_frame_rate:
                    try:
                        num, den = map(int, r_frame_rate.split('/'))
                        if den != 0:
                            fps = num / den
                            framerates.append(fps)
                    except (ValueError, ZeroDivisionError):
                        pass
        if not framerates:
            return default_fps

        from collections import Counter
        standard_framerates = [24, 25, 30, 48, 50, 60]
        rounded_framerates = []
        for fps in framerates:
            closest = min(standard_framerates, key=lambda x: abs(x - fps))
            rounded_framerates.append(closest)
        framerate_counts = Counter(rounded_framerates)
        most_common = framerate_counts.most_common(1)
        if most_common:
            return most_common[0][0]
        return default_fps
    
    def _start_ffmpeg_process(self, paths, out_path, use_fallback):
        """Constructs and starts the FFmpeg process."""
        try:
            if hasattr(self.parent, "_get_selected_music"):
                music_path, music_vol = self.parent._get_selected_music()
            elif hasattr(self.parent, "music_handler"):
                music_path, music_vol = self.parent.music_handler.get_selected_music()
            else:
                music_path, music_vol = None, 0.0
        except Exception as ex:
            self.logger.debug(f"Failed to get music selection: {ex}")
            music_path, music_vol = None, 0.0
        music_offset = self.parent.music_offset_input.value() if hasattr(self.parent, 'music_offset_input') else 0.0
        encoder_manager = EncoderManager(self.logger)
        encoder_flags, encoder_label = encoder_manager.get_codec_flags(
            encoder_manager.get_initial_encoder(),
            video_bitrate_kbps=None,
            effective_duration_sec=0.0
        )
        self.logger.info(f"Using encoder: {encoder_label}")
        if use_fallback:
            target_resolution = self._get_target_resolution(paths)
            target_fps = self._get_target_framerate(paths)
            self.logger.info(f"MODE: Smart Merge (Transcode) - Standardizing to {target_resolution[0]}x{target_resolution[1]}/{target_fps}fps.")
            cmd = [self.parent.ffmpeg, "-y"]
            for p in paths:
                cmd.extend(["-i", p])
            music_idx = len(paths)
            if music_path:
                cmd.extend(["-i", music_path])
            filter_chains = []
            concat_v = []
            concat_a = []
            tgt_w, tgt_h = target_resolution
            for i in range(len(paths)):
                vf = f"[{i}:v]scale={tgt_w}:{tgt_h}:force_original_aspect_ratio=decrease,pad={tgt_w}:{tgt_h}:(ow-iw)/2:(oh-ih)/2,setsar=1,fps={target_fps},format=yuv420p[v{i}]"
                filter_chains.append(vf)
                concat_v.append(f"[v{i}]")
                concat_a.append(f"[{i}:a]")
            filter_chains.append(f"{''.join(concat_v)}{''.join(concat_a)}concat=n={len(paths)}:v=1:a=1[v_concat][a_concat]")
            final_map_a = "[a_concat]"
            if music_path:
                mix_cmd = (
                    f"[a_concat]volume=1.0[main_a];"
                    f"[{music_idx}:a]atrim=start={music_offset:.3f},volume={music_vol:.3f}[mus_a];"
                    f"[main_a][mus_a]amix=inputs=2:duration=first[a_final]"
                )
                filter_chains.append(mix_cmd)
                final_map_a = "[a_final]"
            cmd.extend([
                "-filter_complex", ";".join(filter_chains),
                "-map", "[v_concat]",
                "-map", final_map_a,
                *encoder_flags,
                "-c:a", "aac", "-b:a", "192k",
                "-shortest",
                str(out_path)
            ])
            self._cmd = cmd
        else:
            self.logger.info("MODE: Fast Merge (Stream Copy) - Inputs are strictly compatible.")
            concat_txt = Path(self._temp_dir.name, "concat_list.txt")
            with concat_txt.open("w", encoding="utf-8") as f:
                for p in paths:
                    escaped_path = p.replace("'", "'\\''")
                    f.write(f"file '{escaped_path}'\n")
            base_cmd = [self.parent.ffmpeg, "-y", "-f", "concat", "-safe", "0", "-i", str(concat_txt)]
            if music_path:
                self.logger.info("MUSIC: Adding background music (Re-encoding audio only).")
                base_cmd.extend(["-i", music_path])
                self._cmd = base_cmd + [
                    "-filter_complex", 
                    f"[0:a]volume=1.0[a0];[1:a]atrim=start={music_offset:.3f},volume={music_vol:.3f}[a1];[a0][a1]amix=inputs=2:duration=first[a_out]",
                    "-map", "0:v", "-map", "[a_out]",
                    "-c:v", "copy", "-c:a", "aac", "-b:a", "192k",
                    "-shortest", str(out_path)
                ]
            else:
                self.logger.info("MUSIC: No background music.")
                self._cmd = base_cmd + ["-c", "copy", str(out_path)]
        total_in = 0
        inputs_log = []
        for f in paths:
            try:
                sz = Path(f).stat().st_size
                total_in += sz
                inputs_log.append({"path": f, "size": _human(sz)})
            except Exception as ex:
                self.logger.debug(f"Failed to get file size for {f}: {ex}")
        self.logger.info("MERGE_START: output='%s'", self._output_path)
        self.logger.info("MERGE_INPUTS: %s", inputs_log)
        self.logger.info("MERGE_CMD: %s", " ".join(self._cmd))
        self.process = QProcess(self.parent)
        self.process.finished.connect(self._merge_finished)
        self.process.readyReadStandardError.connect(self._process_ffmpeg_output)
        self._merge_started_at = time.time()
        self.process.start(self.parent.ffmpeg, self._cmd[1:])
