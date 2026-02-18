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
os.environ['VLC_VERBOSE'] = '-1'
os.environ['VLC_QUIET'] = '1'
os.environ['VLC_DEBUG'] = '0'
script_dir = os.path.dirname(os.path.abspath(__file__))
binaries_dir = os.path.abspath(os.path.join(script_dir, '..', 'binaries'))
os.environ['VLC_PLUGIN_PATH'] = os.path.join(binaries_dir, 'plugins') if os.path.exists(os.path.join(binaries_dir, 'plugins')) else ''
try:
    import vlc
except ImportError:
    vlc = None
logger = logging.getLogger(__name__)
@contextlib.contextmanager
def _suppress_vlc_output():
    """Context manager to suppress stdout and stderr during VLC initialization."""
    with open(os.devnull, 'w') as null:
        with contextlib.redirect_stdout(null), contextlib.redirect_stderr(null):
            yield

def get_vlc_log_dir():
    log_dir = os.path.join(tempfile.gettempdir(), "FortniteVideoTool", "logs")
    os.makedirs(log_dir, exist_ok=True)
    return log_dir

class MediaProcessor(QObject):
    info_retrieved = pyqtSignal(str)
    fallback_resolution_requested = pyqtSignal()

    def __init__(self, bin_dir):
        super().__init__()
        self.bin_dir = bin_dir
        self.vlc_log_path = os.path.join(get_vlc_log_dir(), "vlc_errors.log")
        os.makedirs(os.path.dirname(self.vlc_log_path), exist_ok=True)
        self._ffprobe_procs = []
        self._ffprobe_lock = threading.Lock()
        self._state_lock = threading.RLock()
        self._original_resolution = None
        self._input_file_path = None
        atexit.register(self._kill_ffprobe_procs)
        self.fallback_resolution_requested.connect(self._fetch_vlc_resolution)
        override_vlc_path = None
        override_file = os.path.join(os.path.dirname(bin_dir), 'config', 'vlc_path.txt')
        if os.path.exists(override_file):
            try:
                with open(override_file, 'r') as f:
                    path = f.read().strip()
                    if os.path.isdir(path):
                        os.environ['PYTHON_VLC_MODULE_PATH'] = path
                        override_vlc_path = path
                        logger.info(f"Using VLC override path: {path}")
            except Exception as e:
                logger.error(f"Failed to read VLC override file: {e}")
        if vlc is None:
            logger.error("python-vlc module is unavailable. MediaProcessor will run in no-playback mode.")
            self.vlc_instance = None
            self.media_player = None
            self.media = None
            self.original_resolution = None
            self.input_file_path = None
            return
        vlc_args = [
            '--no-xlib', '--no-video-title-show',
            '--aout=waveout', 
            '--avcodec-hw=any', '--vout=direct3d11',
            '--no-stats', '--no-lua', '--no-interact',
            '--file-logging', '--logmode=text',
            f"--logfile={os.environ.get('FVS_VLC_RAW_LOG', self.vlc_log_path)}",
            f"--app-id=FVS.{os.environ.get('FVS_VLC_SOURCE_TAG', 'crop_tools')}"
        ]
        try:
            with _suppress_vlc_output():
                self.vlc_instance = vlc.Instance(vlc_args)
            if not self.vlc_instance:
                logger.warning("Enhanced VLC args failed, trying minimal args")
                fallback_args = [
                    '--intf=dummy', '--vout=dummy', '--aout=directsound',
                    '--no-xlib', '--no-video-title-show', '--quiet',
                    '--verbose=0', '--no-stats', '--logfile=NUL'
                ]
                with _suppress_vlc_output():
                    self.vlc_instance = vlc.Instance(fallback_args)
        except Exception as e:
            logger.error(f"Failed to create VLC instance: {e}")
            self.vlc_instance = None
        if self.vlc_instance:
            self.media_player = self.vlc_instance.media_player_new()
            logger.info("MediaProcessor initialized successfully.")
        else:
            self.media_player = None
            logger.error("MediaProcessor initialized WITHOUT VLC support.")
        self.media = None
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
        if not self.vlc_instance or not self.media_player:
            logger.error("VLC not initialized; load_media aborted.")
            return False
        try:
            self._kill_ffprobe_procs()
            if self.media:
                self.media.release()
            self.input_file_path = file_path
            self.media = self.vlc_instance.media_new(file_path)
            self.media_player.set_media(self.media)
            if video_frame_winId:
                self.media_player.set_hwnd(int(video_frame_winId))
            self.media_player.play()
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

    def play_pause(self):
        if not self.media_player: return False
        if self.media_player.is_playing():
            logger.info("Pausing media.")
            self.media_player.pause()
            return False
        else:
            if self.media:
                logger.info("Playing media.")
                self.media_player.play()
                return True
        return False

    def is_playing(self):
        return self.media_player.is_playing() if self.media_player else False

    def get_time(self):
        return self.media_player.get_time() if self.media_player else 0

    def get_length(self):
        return self.media_player.get_length() if self.media_player else 0

    def get_state(self):
        return self.media_player.get_state() if self.media_player else None

    def get_position(self):
        return self.media_player.get_position() if self.media_player else 0.0

    def set_position(self, position):
        if not self.media_player:
            return
        logger.info(f"set_position called with position={position}")
        if self.media_player.is_seekable():
            try:
                self.media_player.set_position(position)
                self._last_seek_time = time.time()
            except Exception as e:
                logger.error(f"Failed to seek: {e}")
        else:
            logger.warning(f"Media reports not seekable, cannot seek.")

    def stop(self):
        self._kill_ffprobe_procs()
        if self.media_player:
            logger.info("Stopping media.")
            self.media_player.stop()

    def set_media_to_null(self):
        logger.info("Unloading media.")
        self._kill_ffprobe_procs()
        if self.media_player:
            self.media_player.set_media(None)
        if self.media:
            self.media.release()
        self.media = None
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

    def _fetch_vlc_resolution(self):
        """[FIX #2] Explicit resolution detection with no silent 1080p fallback."""
        if not self.original_resolution and self.media_player:
            logger.info("Attempting to fetch resolution fallback from VLC.")
            w, h = self.media_player.video_get_size(0)
            if w > 0 and h > 0:
                self.original_resolution = f"{w}x{h}"
                logger.info(f"VLC fallback got resolution: {self.original_resolution}")
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
