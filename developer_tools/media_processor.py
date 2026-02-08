import os
import subprocess
import sys
import logging
import tempfile
import contextlib
from PyQt5.QtCore import QTimer, QObject, pyqtSignal
os.environ['VLC_VERBOSE'] = '-1'
os.environ['VLC_QUIET'] = '1'
os.environ['VLC_DEBUG'] = '0'
script_dir = os.path.dirname(os.path.abspath(__file__))
binaries_dir = os.path.abspath(os.path.join(script_dir, '..', 'binaries'))
os.environ['VLC_PLUGIN_PATH'] = os.path.join(binaries_dir, 'plugins') if os.path.exists(os.path.join(binaries_dir, 'plugins')) else ''
try:
    import vlc
except ImportError:
    raise ImportError(
        "VLC is not installed or could not be found. "
        "This application requires a VLC installation to function. "
        "Please install VLC from https://www.videolan.org/vlc/"
    )
logger = logging.getLogger(__name__)

def _suppress_vlc_output():
    """Context manager to suppress stdout and stderr during VLC initialization."""
    @contextlib.contextmanager
    def suppress_output():
        with open(os.devnull, 'w') as null:
            with contextlib.redirect_stdout(null), contextlib.redirect_stderr(null):
                yield
    return suppress_output()

class MediaProcessor(QObject):
    info_retrieved = pyqtSignal(str)

    def __init__(self, bin_dir):
        super().__init__()
        self.bin_dir = bin_dir
        logger.info("Initializing MediaProcessor...")
        os.environ['VLC_VERBOSE'] = '0'
        os.environ['VLC_QUIET'] = '1'
        os.environ['VLC_DEBUG'] = '0'
        vlc_args = [
            '--no-xlib', '--no-video-title-show', '--no-plugins-cache',
            '--file-caching=200', '--aout=directsound', '--verbose=0',
            '--quiet', '--no-stats', '--no-lua', '--no-interact',
            '--logfile=NUL'
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

    def _get_binary_path(self, name):
        """Returns the path to a binary, favoring local binaries folder then system PATH."""
        ext = ".exe" if sys.platform == "win32" else ""
        local_path = os.path.abspath(os.path.join(self.bin_dir, f"{name}{ext}"))
        if os.path.exists(local_path):
            return local_path
        
        import shutil
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
            if self.media:
                self.media.release()
            self.input_file_path = file_path
            self.media = self.vlc_instance.media_new(file_path)
            self.media_player.set_media(self.media)
            if video_frame_winId:
                self.media_player.set_hwnd(int(video_frame_winId))
            self.media_player.play()
            
            import threading
            thread = threading.Thread(target=self.get_video_info, args=(file_path,), daemon=True)
            thread.start()
            return True
        except Exception as e:
            logger.error(f"Failed to load media: {e}", exc_info=True)
            return False

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
        if not self.media_player: return
        logger.info(f"set_position called with position={position}")
        if self.media_player.is_seekable():
            try:
                self.media_player.set_position(position)
            except Exception as e:
                logger.error(f"Failed to seek: {e}")
        else:
            logger.warning(f"Media reports not seekable, cannot seek.")

    def stop(self):
        if self.media_player:
            logger.info("Stopping media.")
            self.media_player.stop()

    def set_media_to_null(self):
        logger.info("Unloading media.")
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
        try:
            ffprobe_path = self._get_binary_path('ffprobe')
            cmd = [
                ffprobe_path, '-v', 'error', '-select_streams', 'v:0',
                '-show_entries', 'stream=width,height', '-of', 'json',
                file_path
            ]
            logger.info(f"Executing ffprobe (JSON) command: {' '.join(cmd)}")
            result = subprocess.run(cmd, capture_output=True, text=True, check=True,
                                    creationflags=(subprocess.CREATE_NO_WINDOW if sys.platform == "win32" else 0),
                                    timeout=5.0)

            import json
            data = json.loads(result.stdout)
            streams = data.get('streams', [])
            if streams:
                w = streams[0].get('width')
                h = streams[0].get('height')
                if w and h:
                    self.original_resolution = f"{w}x{h}"
                    logger.info(f"ffprobe (JSON) got resolution: {self.original_resolution}")
                    self.info_retrieved.emit(self.original_resolution)
                    return self.original_resolution
        except subprocess.TimeoutExpired:
            logger.error(f"ffprobe (JSON) timed out for {file_path}")
        except Exception as e:
             logger.warning(f"ffprobe (JSON) failed: {e}")
        try:
            ffprobe_path = self._get_binary_path('ffprobe')
            cmd = [
                ffprobe_path, '-v', 'error', '-select_streams', 'v:0',
                '-show_entries', 'stream=width,height', '-of', 'csv=p=0:s=x',
                file_path
            ]
            logger.info(f"Executing ffprobe (CSV) command: {' '.join(cmd)}")
            result = subprocess.run(cmd, capture_output=True, text=True, check=True,
                                    creationflags=(subprocess.CREATE_NO_WINDOW if sys.platform == "win32" else 0),
                                    timeout=5.0)
            res_string = result.stdout.strip()
            if res_string:
                self.original_resolution = res_string
                logger.info(f"ffprobe (CSV) got resolution: {res_string}")
                self.info_retrieved.emit(self.original_resolution)
                return self.original_resolution
        except subprocess.TimeoutExpired:
            logger.error(f"ffprobe (CSV) timed out for {file_path}")
        except Exception as e:
            logger.error(f"ffprobe (CSV) failed: {e}")
        QTimer.singleShot(500, lambda: self._fetch_vlc_resolution())
        return None

    def _fetch_vlc_resolution(self):
        if not self.original_resolution:
            logger.info("Attempting to fetch resolution fallback from VLC.")
            w, h = self.media_player.video_get_size(0)
            if w > 0 and h > 0:
                self.original_resolution = f"{w}x{h}"
                logger.info(f"VLC fallback got resolution: {self.original_resolution}")
                self.info_retrieved.emit(self.original_resolution)

    def take_snapshot(self, snapshot_path, preferred_time=None):
        if self.is_playing():
            logger.info("Pausing video for snapshot")
            self.media_player.pause()
        if not self.media or not self.input_file_path:
            logger.warning("take_snapshot failed: No media or input file path.")
            return False, "No media loaded."
        if not self.original_resolution:
            logger.warning("take_snapshot failed: Original resolution not yet determined.")
            return False, "Please wait for video information."
        try:
            ffmpeg_path = self._get_binary_path('ffmpeg')
            if preferred_time is None:
                curr_time = max(0, self.get_time() / 1000.0)
            else:
                curr_time = max(0, preferred_time)
            cmd = [
                ffmpeg_path, '-ss', f"{curr_time:.3f}", '-i', self.input_file_path,
                '-frames:v', '1', '-q:v', '2', '-update', '1', '-y', snapshot_path
            ]
            logger.info(f"Executing FFmpeg command: {' '.join(cmd)}")
            subprocess.run(
                cmd, check=True,
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
                text=True,
                creationflags=(subprocess.CREATE_NO_WINDOW if sys.platform == "win32" else 0)
            )
            logger.info(f"Successfully created snapshot at {snapshot_path}")
            return True, "Snapshot created."
        except Exception as e:
            logger.error(f"FFmpeg snapshot failed. Command: {' '.join(cmd)}", exc_info=True)
            return False, f"FFmpeg snapshot failed: {e}"
