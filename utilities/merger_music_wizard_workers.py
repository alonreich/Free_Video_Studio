import os
import sys
import time
import tempfile
import subprocess
import logging
from PyQt5 import QtCore
from PyQt5.QtCore import pyqtSignal
from PyQt5.QtGui import QPixmap

class VideoFilmstripWorker(QtCore.QThread):
    asset_ready = pyqtSignal(int, list)
    finished = pyqtSignal()

    def __init__(self, video_segments_info, bin_dir):
        super().__init__(None)
        self.video_segments_info = video_segments_info 
        self.bin_dir = bin_dir

    def run(self):
        ffmpeg_exe = os.path.join(self.bin_dir, "ffmpeg.exe")
        flags = subprocess.CREATE_NO_WINDOW if sys.platform == "win32" else 0
        logger = logging.getLogger("Video_Merger")
        logger.info("GPU_WORKER: Initializing Parallel Filmstrip Extraction.")
        for idx, (path, duration) in enumerate(self.video_segments_info):
            try:
                tmp_pattern_dir = tempfile.mkdtemp(prefix="fvs_thumbs_")
                out_pattern = os.path.join(tmp_pattern_dir, "thumb_%04d.jpg")
                logger.debug(f"GPU_WORKER: Segment {idx} -> {os.path.basename(path)} ({duration:.1f}s)")
                logger.debug(f"GPU_WORKER: Density: 1fps | Pattern: {out_pattern}")
                cmd = [
                    ffmpeg_exe, "-y", "-hide_banner", "-loglevel", "error",
                    "-hwaccel", "auto",
                    "-i", path,
                    "-vf", "fps=1,scale=320:180",
                    "-q:v", "5",
                    out_pattern
                ]
                logger.info(f"GPU_WORKER: Executing FFmpeg: {' '.join(cmd)}")
                start_t = time.time()
                subprocess.run(cmd, capture_output=True, creationflags=flags)
                elapsed = time.time() - start_t
                thumbs = []
                if os.path.exists(tmp_pattern_dir):
                    files = sorted([f for f in os.listdir(tmp_pattern_dir) if f.endswith(".jpg")])
                    logger.debug(f"GPU_WORKER: Found {len(files)} frames in {elapsed:.2f}s")
                    for f in files:
                        full_p = os.path.join(tmp_pattern_dir, f)
                        pm = QPixmap(full_p)
                        if not pm.isNull():
                            thumbs.append(pm)
                        try: os.remove(full_p)
                        except: pass
                    try: os.rmdir(tmp_pattern_dir)
                    except: pass
                if thumbs:
                    logger.info(f"GPU_WORKER: Segment {idx} Complete. Mapped {len(thumbs)} frames.")
                    self.asset_ready.emit(idx, thumbs)
                else:
                    logger.error(f"GPU_WORKER: Segment {idx} Failed - No frames found.")
            except Exception as e:
                logger.error(f"GPU_WORKER: Segment {idx} Critical Error: {e}")
        self.finished.emit()

class MusicWaveformWorker(QtCore.QThread):
    asset_ready = pyqtSignal(int, QPixmap)
    finished = pyqtSignal()

    def __init__(self, music_segments_info, bin_dir):
        super().__init__(None)
        self.music_segments_info = music_segments_info
        self.bin_dir = bin_dir

    def run(self):
        ffmpeg_exe = os.path.join(self.bin_dir, "ffmpeg.exe")
        flags = subprocess.CREATE_NO_WINDOW if sys.platform == "win32" else 0
        logger = logging.getLogger("Video_Merger")
        logger.info("CPU_WORKER: Initializing Music Waveform Generation (Parallel).")
        for i, (path, offset, dur) in enumerate(self.music_segments_info):
            try:
                tf = tempfile.NamedTemporaryFile(suffix=".png", delete=False)
                tmp_path = tf.name; tf.close()
                logger.debug(f"CPU_WORKER: Rendering Waveform {i} -> {os.path.basename(path)}")
                logger.debug(f"CPU_WORKER: Offset: {offset:.2f}s | Duration: {dur:.2f}s")
                cmd = [ffmpeg_exe, "-y", "-ss", f"{offset:.3f}", "-t", f"{dur:.3f}", "-i", path, 
                       "-filter_complex", "aformat=channel_layouts=mono,compand=attacks=0:decays=0.25:points=-90/-90|-45/-30|-20/-8|0/-2,showwavespic=s=6000x500:colors=0x2ecc71:scale=sqrt:draw=full", "-frames:v", "1", tmp_path]
                logger.info(f"CPU_WORKER: Executing FFmpeg (CPU-Bound): {' '.join(cmd)}")
                start_t = time.time()
                subprocess.run(cmd, capture_output=True, creationflags=flags)
                elapsed = time.time() - start_t
                if os.path.exists(tmp_path):
                    logger.info(f"CPU_WORKER: Waveform {i} Complete. Resolution: 6000x400. Elapsed: {elapsed:.2f}s")
                    pm = QPixmap(tmp_path)
                    os.remove(tmp_path)
                    if not pm.isNull(): self.asset_ready.emit(i, pm)
                else:
                    logger.error(f"CPU_WORKER: Waveform {i} Failed - File missing.")
            except Exception as e:
                logger.error(f"CPU_WORKER: Critical error during waveform {i}: {e}")
        self.finished.emit()
