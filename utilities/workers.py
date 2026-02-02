import os
import hashlib
import json
import subprocess
import time
from pathlib import Path
from PyQt5.QtCore import QThread, pyqtSignal, QMutex, QMutexLocker
from utilities.merger_utils import _human, _ffprobe

class FastFileLoaderWorker(QThread):
    """
    Loads files, calculates partial hash for dupes, and probes basic metadata.
    Optimized for speed.
    """
    file_loaded = pyqtSignal(str, int, dict, str)
    finished = pyqtSignal(int, int)

    def __init__(self, files, existing_files, existing_hashes, max_limit, ffmpeg_path):
        super().__init__()
        self.files = files
        self.existing_files = set(existing_files)
        self.existing_hashes = set(existing_hashes)
        self.max_limit = max_limit
        self.ffprobe = _ffprobe(ffmpeg_path)
        self._cancelled = False
        self._mutex = QMutex()

    def _calculate_partial_hash(self, filepath):
        """Hashes first 1MB, last 1MB, and file size/mtime for speed."""
        try:
            h = hashlib.sha256()
            p = Path(filepath)
            stat = p.stat()
            h.update(str(stat.st_size).encode('utf-8'))
            h.update(str(stat.st_mtime).encode('utf-8'))
            
            with open(filepath, "rb") as f:
                # First 1MB
                h.update(f.read(1024 * 1024))
                # Last 1MB (if large enough)
                if stat.st_size > 2 * 1024 * 1024:
                    f.seek(-1024 * 1024, 2)
                    h.update(f.read(1024 * 1024))
            return h.hexdigest()
        except Exception:
            return None

    def run(self):
        added = 0
        duplicates = 0
        room = self.max_limit - len(self.existing_files)
        
        for f in self.files:
            with QMutexLocker(self._mutex):
                if self._cancelled: break
                
            if added >= room: 
                break
            
            # 1. Path check
            if f in self.existing_files:
                duplicates += 1
                continue
                
            # 2. Hash check (Fast)
            f_hash = self._calculate_partial_hash(f)
            if f_hash and f_hash in self.existing_hashes:
                duplicates += 1
                continue
                
            # 3. Size
            try:
                sz = os.path.getsize(f)
            except OSError:
                sz = 0
                
            # 4. Probe (Fast)
            probe_data = {}
            try:
                cmd = [self.ffprobe, "-v", "error", "-show_entries", 
                       "format=duration:stream=width,height,codec_name,sample_rate,channels", 
                       "-of", "json", f]
                flags = subprocess.CREATE_NO_WINDOW if os.name == 'nt' else 0
                # Timeout prevents hanging on bad files
                r = subprocess.run(cmd, capture_output=True, text=True, creationflags=flags, timeout=5)
                if r.returncode == 0:
                    probe_data = json.loads(r.stdout)
            except Exception:
                pass
                
            self.file_loaded.emit(f, sz, probe_data, f_hash)
            self.existing_files.add(f)
            if f_hash:
                self.existing_hashes.add(f_hash)
            added += 1
            
        self.finished.emit(added, duplicates)

    def cancel(self):
        with QMutexLocker(self._mutex):
            self._cancelled = True

class ProbeWorker(QThread):
    """
    Dedicated worker for re-probing or deep probing if necessary.
    Usually we try to use cached data, but this is for the 'Merge' step
    verification if needed.
    """
    finished = pyqtSignal(list, float) # float_durations, total_seconds
    error = pyqtSignal(str)

    def __init__(self, video_files, ffmpeg_path):
        super().__init__()
        self.video_files = video_files
        self.ffprobe = _ffprobe(ffmpeg_path)
        self._cancelled = False
        self._mutex = QMutex()

    def run(self):
        durations = []
        total = 0.0
        try:
            for path in self.video_files:
                with QMutexLocker(self._mutex):
                    if self._cancelled: return
                    
                try:
                    cmd = [self.ffprobe, "-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", path]
                    flags = subprocess.CREATE_NO_WINDOW if os.name == 'nt' else 0
                    r = subprocess.run(cmd, capture_output=True, text=True, creationflags=flags, timeout=5)
                    d = float(r.stdout.strip() or 0)
                except Exception:
                    d = 0.0
                
                durations.append((path, d))
                total += d
                
            if not self._cancelled:
                self.finished.emit(durations, total)
        except Exception as e:
            self.error.emit(str(e))

    def cancel(self):
        with QMutexLocker(self._mutex):
            self._cancelled = True
        self.wait()