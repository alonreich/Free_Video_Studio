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

    def _start_ffmpeg_process(self, paths, out_path, use_fallback):
        """Constructs and starts the FFmpeg process."""
        try:
            if hasattr(self.parent, "_get_selected_music"):
                music_path, music_vol = self.parent._get_selected_music()
            elif hasattr(self.parent, "music_handler"):
                music_path, music_vol = self.parent.music_handler.get_selected_music()
            else:
                music_path, music_vol = None, 0.0
        except Exception:
            music_path, music_vol = None, 0.0
        music_offset = self.parent.music_offset_input.value()
        if use_fallback:
            self.logger.info("MODE: Smart Merge (Transcode) - Standardizing to 1080p/60fps.")
            cmd = [self.parent.ffmpeg, "-y"]
            for p in paths:
                cmd.extend(["-i", p])
            music_idx = len(paths)
            if music_path:
                cmd.extend(["-i", music_path])
            filter_chains = []
            concat_v = []
            concat_a = []
            tgt_w, tgt_h = 1920, 1080
            for i in range(len(paths)):
                vf = f"[{i}:v]scale={tgt_w}:{tgt_h}:force_original_aspect_ratio=decrease,pad={tgt_w}:{tgt_h}:(ow-iw)/2:(oh-ih)/2,setsar=1,fps=60,format=yuv420p[v{i}]"
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
                "-c:v", "libx264", "-preset", "veryfast", "-crf", "23",
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
            except:
                pass
        self.logger.info("MERGE_START: output='%s'", self._output_path)
        self.logger.info("MERGE_INPUTS: %s", inputs_log)
        self.logger.info("MERGE_CMD: %s", " ".join(self._cmd))
        self.process = QProcess(self.parent)
        self.process.finished.connect(self._merge_finished)
        self.process.readyReadStandardError.connect(self._process_ffmpeg_output)
        self._merge_started_at = time.time()
        self.process.start(self.parent.ffmpeg, self._cmd[1:])