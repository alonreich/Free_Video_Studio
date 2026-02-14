import os
import sys
import time
import hashlib
import shutil
import tempfile
import subprocess
import logging
from pathlib import Path
from typing import Any, Sequence
from PyQt5 import QtCore
from PyQt5.QtCore import pyqtSignal
from PyQt5.QtGui import QPixmap

def _kill_process_tree(proc: Any | None) -> None:
    if proc is None or proc.poll() is not None:
        return
    try:
        if sys.platform == "win32":
            flags = getattr(subprocess, "CREATE_NO_WINDOW", 0)
            subprocess.run(
                ["taskkill", "/PID", str(proc.pid), "/T", "/F"],
                stdout=subprocess.DEVNULL,
                stderr=subprocess.DEVNULL,
                creationflags=flags,
            )
        else:
            proc.kill()
    except Exception:
        try:
            proc.kill()
        except Exception:
            pass
_CACHE_ROOT = Path(tempfile.gettempdir()) / "fvs_timeline_cache"

def _safe_media_signature(path: str) -> tuple[int, int]:
    try:
        st = os.stat(path)
        return int(getattr(st, "st_mtime_ns", int(st.st_mtime * 1_000_000_000))), int(st.st_size)
    except Exception:
        return 0, 0

def _hash_key(parts: Sequence[Any]) -> str:
    raw = "||".join(str(p) for p in parts)
    return hashlib.sha1(raw.encode("utf-8", errors="ignore")).hexdigest()

def _prune_cache_dir(cache_dir: Path, *, max_entries: int = 500, max_age_seconds: int = 7 * 24 * 3600) -> None:
    try:
        if not cache_dir.exists():
            return
        now = time.time()
        entries = []
        for p in cache_dir.iterdir():
            try:
                mtime = p.stat().st_mtime
                if now - mtime > max_age_seconds:
                    if p.is_dir():
                        shutil.rmtree(p, ignore_errors=True)
                    else:
                        p.unlink(missing_ok=True)
                    continue
                entries.append((mtime, p))
            except Exception:
                continue
        if len(entries) <= max_entries:
            return
        entries.sort(key=lambda t: t[0])
        for _, p in entries[: max(0, len(entries) - max_entries)]:
            try:
                if p.is_dir():
                    shutil.rmtree(p, ignore_errors=True)
                else:
                    p.unlink(missing_ok=True)
            except Exception:
                pass
    except Exception:
        pass

class VideoFilmstripWorker(QtCore.QThread):
    asset_ready = pyqtSignal(int, list, str)
    finished = pyqtSignal(str)

    def __init__(
        self,
        video_segments_info: Sequence[tuple[str, float]],
        bin_dir: str,
        *,
        stage: str = "final",
        max_workers: int = 2,
    ):
        super().__init__(None)
        self.video_segments_info = list(video_segments_info)
        self.bin_dir = bin_dir
        self.stage = str(stage or "final").lower()
        self.max_workers = max(1, int(max_workers or 1))
        self.cache_dir = _CACHE_ROOT / "video"
        self._running = True

    def stop(self):
        self._running = False

    def _cache_path(self, path: str, duration: float) -> Path:
        sig = _safe_media_signature(path)
        key = _hash_key(("video", path, sig[0], sig[1], round(float(duration or 0.0), 3), self.stage, "v3"))
        return self.cache_dir / key

    def _load_cached_thumbs(self, cache_path: Path) -> list[QPixmap]:
        thumbs: list[QPixmap] = []
        if not cache_path.exists() or not cache_path.is_dir():
            return thumbs
        try:
            for f in sorted(cache_path.glob("thumb_*.jpg")):
                pm = QPixmap(str(f))
                if not pm.isNull():
                    thumbs.append(pm)
        except Exception:
            return []
        return thumbs

    def _segment_settings(self, duration: float) -> tuple[float, str, str]:
        dur = max(1.0, float(duration or 0.0))
        if self.stage in ("fast", "progressive"):
            target_thumbs = 12.0
            fps = max(0.10, min(0.55, target_thumbs / dur))
            return fps, "192:108", "7"
        target_thumbs = 52.0
        fps = max(0.12, min(1.00, target_thumbs / dur))
        return fps, "256:144", "5"

    def _render_segment(self, idx: int, path: str, duration: float) -> tuple[int, list[QPixmap]]:
        logger = logging.getLogger("Video_Merger")
        if not self._running:
            return idx, []
        cache_path = self._cache_path(path, duration)
        cached = self._load_cached_thumbs(cache_path)
        if cached:
            return idx, cached
        ffmpeg_exe = os.path.join(self.bin_dir, "ffmpeg.exe")
        flags = subprocess.CREATE_NO_WINDOW if sys.platform == "win32" else 0
        tmp_pattern_dir = ""
        thumbs: list[QPixmap] = []
        try:
            fps, scale, qv = self._segment_settings(duration)
            tmp_pattern_dir = tempfile.mkdtemp(prefix="fvs_thumbs_")
            out_pattern = os.path.join(tmp_pattern_dir, "thumb_%04d.jpg")
            cmd: list[str] = [
                ffmpeg_exe,
                "-y",
                "-hide_banner",
                "-loglevel",
                "error",
                "-hwaccel",
                "auto",
                "-i",
                path,
                "-vf",
                f"fps={fps:.3f},scale={scale}",
                "-q:v",
                qv,
                out_pattern,
            ]
            subprocess.run(cmd, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, creationflags=flags)
            if os.path.exists(tmp_pattern_dir):
                files = sorted([f for f in os.listdir(tmp_pattern_dir) if f.endswith(".jpg")])
                if files:
                    cache_path.mkdir(parents=True, exist_ok=True)
                for i, f in enumerate(files):
                    src = os.path.join(tmp_pattern_dir, f)
                    dst = cache_path / f"thumb_{i:04d}.jpg"
                    try:
                        shutil.copy2(src, dst)
                    except Exception:
                        pass
                    pm = QPixmap(src)
                    if not pm.isNull():
                        thumbs.append(pm)
        except Exception as e:
            logger.error("GPU_WORKER[%s]: Segment %s error: %s", self.stage, idx, e)
        finally:
            if tmp_pattern_dir and os.path.isdir(tmp_pattern_dir):
                try:
                    shutil.rmtree(tmp_pattern_dir, ignore_errors=True)
                except Exception:
                    pass
        if not thumbs:
            thumbs = self._load_cached_thumbs(cache_path)
        return idx, thumbs

    def run(self):
        logger = logging.getLogger("Video_Merger")
        logger.info("GPU_WORKER[%s]: Initializing ordered filmstrip extraction.", self.stage)
        self.cache_dir.mkdir(parents=True, exist_ok=True)
        _prune_cache_dir(self.cache_dir, max_entries=280)
        try:
            for idx, (path, duration) in enumerate(self.video_segments_info):
                if not self._running:
                    break
                try:
                    seg_idx, thumbs = self._render_segment(idx, path, duration)
                except Exception as e:
                    logger.error("GPU_WORKER[%s]: segment %s failed: %s", self.stage, idx, e)
                    continue
                if thumbs:
                    self.asset_ready.emit(seg_idx, thumbs, self.stage)
        finally:
            self.finished.emit(self.stage)

class MusicWaveformWorker(QtCore.QThread):
    asset_ready = pyqtSignal(int, QPixmap, str)
    finished = pyqtSignal(str)

    def __init__(
        self,
        music_segments_info: Sequence[tuple[str, float, float]],
        bin_dir: str,
        *,
        stage: str = "final",
        max_workers: int = 2,
    ):
        super().__init__(None)
        self.music_segments_info = list(music_segments_info)
        self.bin_dir = bin_dir
        self.stage = str(stage or "final").lower()
        self.max_workers = max(1, int(max_workers or 1))
        self.cache_dir = _CACHE_ROOT / "wave"
        self._running = True

    def stop(self):
        self._running = False

    def _wave_cache_path(self, path: str, offset: float, dur: float) -> Path:
        sig = _safe_media_signature(path)
        key = _hash_key(
            (
                "wave",
                path,
                sig[0],
                sig[1],
                round(float(offset or 0.0), 3),
                round(float(dur or 0.0), 3),
                self.stage,
                "v3",
            )
        )
        return self.cache_dir / f"{key}.png"

    def _wave_filter(self) -> str:
        if self.stage == "fast":
            return "aformat=channel_layouts=mono,showwavespic=s=760x96:colors=0x2ecc71:draw=full"
        return "aformat=channel_layouts=mono,compand=attacks=0:decays=0.20:points=-90/-90|-45/-30|-20/-8|0/-2,showwavespic=s=2200x280:colors=0x2ecc71:scale=sqrt:draw=full"

    def _render_wave(self, i: int, path: str, offset: float, dur: float) -> tuple[int, QPixmap | None]:
        logger = logging.getLogger("Video_Merger")
        if not self._running:
            return i, None
        cache_path = self._wave_cache_path(path, offset, dur)
        try:
            if cache_path.exists():
                pm = QPixmap(str(cache_path))
                if not pm.isNull():
                    return i, pm
        except Exception:
            pass
        ffmpeg_exe = os.path.join(self.bin_dir, "ffmpeg.exe")
        flags = subprocess.CREATE_NO_WINDOW if sys.platform == "win32" else 0
        tmp_path = ""
        try:
            tf = tempfile.NamedTemporaryFile(prefix="fvs_wave_", suffix=".png", delete=False)
            tmp_path = tf.name
            tf.close()
            cmd: list[str] = [
                ffmpeg_exe,
                "-y",
                "-ss",
                f"{offset:.3f}",
                "-t",
                f"{dur:.3f}",
                "-i",
                path,
                "-filter_complex",
                self._wave_filter(),
                "-frames:v",
                "1",
                tmp_path,
            ]
            subprocess.run(cmd, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, creationflags=flags)
            if os.path.exists(tmp_path):
                pm = QPixmap(tmp_path)
                if not pm.isNull():
                    try:
                        self.cache_dir.mkdir(parents=True, exist_ok=True)
                        shutil.copy2(tmp_path, cache_path)
                    except Exception:
                        pass
                    return i, pm
        except Exception as e:
            logger.error("CPU_WORKER[%s]: waveform %s failed: %s", self.stage, i, e)
        finally:
            if tmp_path and os.path.exists(tmp_path):
                try:
                    os.remove(tmp_path)
                except Exception:
                    pass
        try:
            if cache_path.exists():
                pm = QPixmap(str(cache_path))
                if not pm.isNull():
                    return i, pm
        except Exception:
            pass
        return i, None

    def run(self):
        logger = logging.getLogger("Video_Merger")
        logger.info("CPU_WORKER[%s]: Initializing ordered waveform generation.", self.stage)
        self.cache_dir.mkdir(parents=True, exist_ok=True)
        _prune_cache_dir(self.cache_dir, max_entries=900)
        try:
            for i, (path, offset, dur) in enumerate(self.music_segments_info):
                if not self._running:
                    break
                try:
                    wave_idx, pm = self._render_wave(i, path, offset, dur)
                except Exception as e:
                    logger.error("CPU_WORKER[%s]: waveform %s failed: %s", self.stage, i, e)
                    continue
                if pm is not None and not pm.isNull():
                    self.asset_ready.emit(wave_idx, pm, self.stage)
        finally:
            self.finished.emit(self.stage)

class SingleWaveformWorker(QtCore.QThread):
    ready = pyqtSignal(str, float, QPixmap, str, str)
    error = pyqtSignal(str, str)

    def __init__(self, track_path: str, bin_dir: str, timeout_sec: float = 15.0):
        super().__init__(None)
        self.track_path = track_path
        self.bin_dir = bin_dir
        self.timeout_sec = max(3.0, float(timeout_sec or 15.0))
        self._running = True
        self._proc: Any | None = None

    def stop(self):
        self._running = False
        _kill_process_tree(self._proc)

    def _probe_duration(self, path: str) -> float:
        ffprobe_exe = os.path.join(self.bin_dir, "ffprobe.exe")
        flags = getattr(subprocess, "CREATE_NO_WINDOW", 0) if sys.platform == "win32" else 0
        try:
            r = subprocess.run(
                [
                    ffprobe_exe,
                    "-v", "error",
                    "-select_streams", "a:0",
                    "-show_entries", "stream=duration",
                    "-of", "default=noprint_wrappers=1:nokey=1",
                    path,
                ],
                capture_output=True, text=True, creationflags=flags, timeout=5
            )
            val = (r.stdout or "").strip()
            if r.returncode == 0 and val and val != "N/A":
                return max(0.0, float(val))
            r = subprocess.run(
                [
                    ffprobe_exe,
                    "-v", "error",
                    "-show_entries", "format=duration",
                    "-of", "default=noprint_wrappers=1:nokey=1",
                    path,
                ],
                capture_output=True, text=True, creationflags=flags, timeout=5
            )
            val = (r.stdout or "").strip()
            if r.returncode == 0 and val:
                return max(0.0, float(val))
        except Exception:
            pass
        return 0.0

    def run(self):
        logger = logging.getLogger("Video_Merger")
        if not self._running or not self.track_path:
            return
        ffmpeg_exe = os.path.join(self.bin_dir, "ffmpeg.exe")
        flags = getattr(subprocess, "CREATE_NO_WINDOW", 0) if sys.platform == "win32" else 0
        tf_sync = tempfile.NamedTemporaryFile(prefix="fvs_sync_", suffix=".wav", delete=False)
        tmp_sync = tf_sync.name
        tf_sync.close()
        conv_cmd = [
            ffmpeg_exe, "-y", "-hide_banner", "-loglevel", "error",
            "-i", self.track_path,
            "-vn", "-ac", "2", "-ar", "44100", "-f", "wav",
            tmp_sync
        ]
        try:
            logger.info("WIZARD_STEP2: Straightening audio for perfect sync...")
            self._proc = subprocess.Popen(conv_cmd, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, creationflags=flags)
            while self._running:
                if self._proc.poll() is not None: break
                self.msleep(50)
            if not self._running:
                _kill_process_tree(self._proc)
                if os.path.exists(tmp_sync): os.remove(tmp_sync)
                return
        except Exception as e:
            if os.path.exists(tmp_sync): os.remove(tmp_sync)
            self.error.emit(self.track_path, f"Sync conversion failed: {e}")
            return
        duration = self._probe_duration(tmp_sync)
        tf_png = tempfile.NamedTemporaryFile(prefix="fvs_wave_", suffix=".png", delete=False)
        tmp_png = tf_png.name
        tf_png.close()
        cmd = [
            ffmpeg_exe,
            "-y",
            "-hide_banner",
            "-loglevel",
            "error",
            "-i",
            tmp_sync,
            "-frames:v",
            "1",
            "-filter_complex",
            "aformat=channel_layouts=mono,volume=1.5,showwavespic=s=1400x360:colors=0x7DD3FC:draw=full",
            tmp_png,
        ]
        logger.info("WIZARD_STEP2: Executing async waveform render for %s", os.path.basename(self.track_path))
        started = time.time()
        try:
            self._proc = subprocess.Popen(cmd, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, creationflags=flags)
            while self._running:
                if self._proc.poll() is not None:
                    break
                if (time.time() - started) > self.timeout_sec:
                    _kill_process_tree(self._proc)
                    self.error.emit(self.track_path, f"Waveform rendering timed out after {self.timeout_sec:.0f}s")
                    if os.path.exists(tmp_png): os.remove(tmp_png)
                    if os.path.exists(tmp_sync): os.remove(tmp_sync)
                    return
                self.msleep(40)
            if not self._running:
                _kill_process_tree(self._proc)
                if os.path.exists(tmp_png): os.remove(tmp_png)
                if os.path.exists(tmp_sync): os.remove(tmp_sync)
                return
            if not os.path.exists(tmp_png):
                self.error.emit(self.track_path, "Waveform render failed: output image missing")
                if os.path.exists(tmp_sync): os.remove(tmp_sync)
                return
            pm = QPixmap(tmp_png)
            if pm.isNull():
                self.error.emit(self.track_path, "Waveform render failed: invalid image")
                if os.path.exists(tmp_png): os.remove(tmp_png)
                if os.path.exists(tmp_sync): os.remove(tmp_sync)
                return
            self.ready.emit(self.track_path, duration, pm, tmp_png, tmp_sync)
        except Exception as e:
            _kill_process_tree(self._proc)
            if os.path.exists(tmp_png): os.remove(tmp_png)
            if os.path.exists(tmp_sync): os.remove(tmp_sync)
            self.error.emit(self.track_path, f"Waveform failed: {e}")
