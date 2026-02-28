import os
import subprocess
import sys
import logging
import tempfile
import contextlib
import time
import atexit
import threading
import shutil
import json
from PyQt5.QtCore import QObject, pyqtSignal
try:
    import mpv
except Exception:
    mpv = None
logger = logging.getLogger(__name__)

class MediaProcessor(QObject):
    info_retrieved = pyqtSignal(str)
    fallback_resolution_requested = pyqtSignal()

    def __init__(self, bin_dir):
        super().__init__()
        self.bin_dir = bin_dir
        self._ffprobe_procs = []
        self._ffprobe_lock = threading.Lock()
        self._state_lock = threading.RLock()
        self._original_resolution = None
        self._input_file_path = None
        atexit.register(self._kill_ffprobe_procs)
        self.fallback_resolution_requested.connect(self._fetch_mpv_resolution)
        self.player = None
        if mpv:
            try:
                mpv_kwargs = {
                    'osc': False,
                    'input_default_bindings': False,
                    'input_vo_keyboard': False,
                    'hr_seek': 'yes',
                    'hwdec': 'auto',
                    'keep_open': 'yes',
                    'log_handler': logger.debug,
                    'loglevel': "info",
                    'vo': 'gpu',
                    'ytdl': False,
                    'demuxer_max_bytes': '500M',
                    'demuxer_max_back_bytes': '100M',
                }
                if sys.platform == 'win32':
                    mpv_kwargs['gpu-context'] = 'd3d11'
                    os.environ["LC_NUMERIC"] = "C"
                self.player = mpv.MPV(**mpv_kwargs)
                logger.info("MediaProcessor initialized successfully with MPV.")
            except Exception as e:
                fallback_args = [
                '--vout=dummy'
                ]
                logger.error(f"Failed to initialize MPV in MediaProcessor: {e}")
                self.player = None
        else:
            logger.error("python-mpv module is unavailable. MediaProcessor will run in no-playback mode.")
        self.media_player = self.player
        self.media = True
        self.original_resolution = None
        self.input_file_path = None
        self._ffprobe_procs = []
        self._last_seek_time = 0
    @property
    def original_resolution(self):
        with self._state_lock:
            return self._original_resolution
    @original_resolution.setter
    def original_resolution(self, value):
        with self._state_lock:
            self._original_resolution = value
    @property
    def input_file_path(self):
        with self._state_lock:
            return self._input_file_path
    @input_file_path.setter
    def input_file_path(self, value):
        with self._state_lock:
            self._input_file_path = value

    def _get_binary_path(self, name):
        """Returns the path to a binary, favoring local binaries folder then system PATH."""
        ext = ".exe" if sys.platform == "win32" else ""
        local_path = os.path.abspath(os.path.join(self.bin_dir, f"{name}{ext}"))
        if os.path.exists(local_path):
            return local_path
        system_path = shutil.which(name)
        if system_path:
            return system_path
        return local_path

    def load_media(self, file_path, video_frame_winId):
        logger.info(f"Loading media from: {file_path}")
        if not self.player:
            logger.error("MPV not initialized; load_media aborted.")
            return False
        try:
            self._kill_ffprobe_procs()
            self.input_file_path = file_path
            if video_frame_winId:
                self.player.wid = int(video_frame_winId)
            self.player.command("loadfile", file_path, "replace")
            self.player.pause = False
            thread = threading.Thread(target=self.get_video_info, args=(file_path,), daemon=True)
            thread.start()
            return True
        except Exception as e:
            logger.error(f"Failed to load media: {e}", exc_info=True)
            return False

    def _kill_ffprobe_procs(self):
        """Kill any running ffprobe processes."""
        if not hasattr(self, '_ffprobe_procs'):
            return
        with self._ffprobe_lock:
            for proc in self._ffprobe_procs:
                try:
                    if proc.poll() is None:
                        proc.terminate()
                        try:
                             proc.communicate(timeout=0.2)
                        except subprocess.TimeoutExpired:
                             proc.kill()
                             proc.communicate(timeout=0.1)
                except Exception:
                    pass
            self._ffprobe_procs = []

    def __del__(self):
        try:
            atexit.unregister(self._kill_ffprobe_procs)
        except:
            pass
        self._kill_ffprobe_procs()
        if hasattr(self, 'player') and self.player:
            try: self.player.terminate()
            except: pass

    def play_pause(self):
        if not self.player: return False
        is_paused = getattr(self.player, "pause", True)
        if not is_paused:
            logger.info("Pausing media.")
            self.player.pause = True
            return False
        else:
            logger.info("Playing media.")
            self.player.pause = False
            return True

    def is_playing(self):
        return not getattr(self.player, "pause", True) if self.player else False

    def get_time(self):
        return int((getattr(self.player, 'time-pos', 0) or 0) * 1000) if self.player else 0

    def get_length(self):
        return int((getattr(self.player, 'duration', 0) or 0) * 1000) if self.player else 0

    def get_state(self):
        if not self.player: return None
        return 3 if not getattr(self.player, "pause", True) else 4

    def get_position(self):
        if not self.player: return 0.0
        dur = getattr(self.player, 'duration', 0) or 1
        return (getattr(self.player, 'time-pos', 0) or 0) / dur

    def set_position(self, position):
        if not self.player:
            return
        logger.info(f"set_position called with position={position}")
        try:
            self.player.seek(position * 100, reference='relative-percent', precision='exact')
            self._last_seek_time = time.time()
        except Exception as e:
            logger.error(f"Failed to seek: {e}")

    def stop(self):
        self._kill_ffprobe_procs()
        if self.player:
            logger.info("Stopping media.")
            self.player.stop()

    def set_media_to_null(self):
        logger.info("Unloading media.")
        self._kill_ffprobe_procs()
        if self.player:
            self.player.stop()
        self.original_resolution = None
        self.input_file_path = None

    def get_video_info(self, file_path):
        if not file_path or not os.path.exists(file_path):
            logger.warning(f"get_video_info failed: file path not provided or does not exist: {file_path}")
            return None
        self._kill_ffprobe_procs()
        ffprobe_path = self._get_binary_path('ffprobe')
        creation_flags = subprocess.CREATE_NO_WINDOW if sys.platform == "win32" else 0
        cmd_json = [
            ffprobe_path, '-v', 'error', '-select_streams', 'v:0',
            '-show_entries', 'stream=width,height', '-of', 'json',
            file_path
        ]
        try:
            logger.info("Starting ffprobe detection...")
            proc_json = subprocess.Popen(
                cmd_json,
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
                text=True,
                creationflags=creation_flags
            )
            with self._ffprobe_lock:
                self._ffprobe_procs = [proc_json]
            try:
                out, _ = proc_json.communicate(timeout=3.0)
            except subprocess.TimeoutExpired:
                proc_json.kill()
                out, _ = proc_json.communicate()
            if proc_json.returncode == 0 and out:
                try:
                    data = json.loads(out)
                    w = data['streams'][0]['width']
                    h = data['streams'][0]['height']
                    self.original_resolution = f"{w}x{h}"
                    logger.info(f"ffprobe (JSON) resolution: {self.original_resolution}")
                    self.info_retrieved.emit(self.original_resolution)
                    self._kill_ffprobe_procs()
                    return self.original_resolution
                except Exception as e:
                    logger.warning(f"Failed to parse ffprobe JSON: {e}")
            if not self.original_resolution:
                cmd_csv = [
                    ffprobe_path, '-v', 'error', '-select_streams', 'v:0',
                    '-show_entries', 'stream=width,height', '-of', 'csv=p=0:s=x',
                    file_path
                ]
                proc_csv = subprocess.run(
                    cmd_csv,
                    capture_output=True,
                    text=True,
                    creationflags=creation_flags,
                    timeout=3.0
                )
                if proc_csv.returncode == 0:
                    res = proc_csv.stdout.strip()
                    if res:
                        self.original_resolution = res
                        logger.info(f"ffprobe (CSV) fallback resolution: {self.original_resolution}")
                        self.info_retrieved.emit(self.original_resolution)
                        return self.original_resolution
        except Exception as e:
            logger.error(f"ffprobe error: {e}")
            self._kill_ffprobe_procs()
        self.fallback_resolution_requested.emit()
        return None

    def _fetch_mpv_resolution(self):
        """[FIX #2] Explicit resolution detection with no silent 1080p fallback."""
        if not self.original_resolution and self.player:
            logger.info("Attempting to fetch resolution fallback from MPV.")
            w = getattr(self.player, 'width', 0)
            h = getattr(self.player, 'height', 0)
            if w and h and w > 0 and h > 0:
                self.original_resolution = f"{w}x{h}"
                logger.info(f"MPV fallback got resolution: {self.original_resolution}")
                self.info_retrieved.emit(self.original_resolution)
                return
        if not self.original_resolution:
            logger.warning("All automated resolution detection failed.")
            self.original_resolution = None
            self.info_retrieved.emit("UNKNOWN")

    def take_snapshot(self, snapshot_path, preferred_time=None):
        """[FIX #8, #11] Reliable snapshot with atomic overwrite."""
        if self.is_playing():
            self.media_player.pause()
        if not self.media or not self.input_file_path:
            return False, "No media loaded."
        temp_path = None
        try:
            ffmpeg_path = self._get_binary_path('ffmpeg')
            curr_time = max(0, preferred_time if preferred_time is not None else self.get_time() / 1000.0)
            temp_fd, temp_path = tempfile.mkstemp(suffix=".png")
            os.close(temp_fd)
            cmd = [
                ffmpeg_path, '-ss', f"{curr_time:.3f}", '-i', self.input_file_path,
                '-frames:v', '1', '-q:v', '2', '-y', temp_path
            ]
            subprocess.run(
                cmd, check=True, capture_output=True,
                text=True,
                timeout=15.0,
                creationflags=(subprocess.CREATE_NO_WINDOW if sys.platform == "win32" else 0)
            )
            if os.path.exists(temp_path) and os.path.getsize(temp_path) > 0:
                os.replace(temp_path, snapshot_path)
                return True, "Snapshot created."
            return False, "Snapshot file empty."
        except subprocess.TimeoutExpired:
            return False, "FFmpeg snapshot timed out."
        except subprocess.CalledProcessError as e:
            stderr = (e.stderr or "").strip()
            return False, f"FFmpeg failed: {stderr or e}"
        except Exception as e:
            return False, f"FFmpeg failed: {e}"
        finally:
            if temp_path and os.path.exists(temp_path):
                try:
                    os.unlink(temp_path)
                except OSError:
                    pass
