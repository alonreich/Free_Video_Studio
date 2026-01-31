import os
import subprocess
import sys
try:
    import vlc
except ImportError:
    raise ImportError(
        "VLC is not installed or could not be found. "
        "This application requires a VLC installation to function. "
        "Please install VLC from https://www.videolan.org/vlc/"
    )

import logging
from PyQt5.QtCore import QTimer
logger = logging.getLogger(__name__)

class MediaProcessor:
    def __init__(self, bin_dir):
        self.bin_dir = bin_dir
        logger.info("Initializing MediaProcessor...")
        vlc_args = [
            '--no-xlib', '--no-video-title-show', '--no-plugins-cache',
            '--file-caching=200', '--aout=directsound', '--verbose=2'
        ]
        self.vlc_instance = vlc.Instance(vlc_args)
        self.media_player = self.vlc_instance.media_player_new()
        self.media = None
        self.original_resolution = None
        self.input_file_path = None
        logger.info("MediaProcessor initialized successfully.")

    def load_media(self, file_path, video_frame_winId):
        logger.info(f"Loading media from: {file_path}")
        if self.media:
            self.media.release()
        self.input_file_path = file_path
        self.media = self.vlc_instance.media_new(file_path)
        self.media_player.set_media(self.media)
        self.media_player.set_hwnd(video_frame_winId)
        self.play_pause()
        self.get_video_info(file_path)
        return True

    def play_pause(self):
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
        return self.media_player.is_playing()

    def get_state(self):
        return self.media_player.get_state()

    def get_position(self):
        return self.media_player.get_position()

    def set_position(self, position):
        logger.info(f"set_position called with position={position}")
        if self.media_player.is_seekable():
            logger.info(f"Media is seekable, seeking to {position}")
            try:
                self.media_player.set_position(position)
                logger.info(f"Seek command sent to VLC")
            except Exception as e:
                logger.error(f"Failed to seek: {e}")
        else:
            logger.warning(f"Media reports not seekable, cannot seek.")

    def stop(self):
        logger.info("Stopping media.")
        self.media_player.stop()

    def set_media_to_null(self):
        logger.info("Unloading media.")
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
        QTimer.singleShot(500, lambda: self._fetch_vlc_resolution())
        try:
            ffprobe_path = os.path.join(self.bin_dir, 'ffprobe.exe')
            if not os.path.exists(ffprobe_path):
                logger.error(f"ffprobe.exe not found at {ffprobe_path}")
                return None
            cmd = [
                ffprobe_path, '-v', 'error', '-select_streams', 'v:0',
                '-show_entries', 'stream=width,height', '-of', 'csv=p=0:s=x',
                file_path
            ]
            logger.info(f"Executing ffprobe command: {' '.join(cmd)}")
            result = subprocess.run(cmd, capture_output=True, text=True, check=True,
                                    creationflags=(subprocess.CREATE_NO_WINDOW if sys.platform == "win32" else 0))
            res_string = result.stdout.strip()
            if res_string:
                self.original_resolution = res_string
                logger.info(f"ffprobe got resolution: {res_string}")
                return self.original_resolution
        except (subprocess.CalledProcessError, FileNotFoundError) as e:
            logger.error(f"ffprobe command failed for {file_path}.", exc_info=True)
        return None

    def _fetch_vlc_resolution(self):
        if not self.original_resolution:
            logger.info("Attempting to fetch resolution fallback from VLC.")
            w, h = self.media_player.video_get_size(0)
            if w > 0 and h > 0:
                self.original_resolution = f"{w}x{h}"
                logger.info(f"VLC fallback got resolution: {self.original_resolution}")

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
            ffmpeg_path = os.path.join(self.bin_dir, 'ffmpeg.exe')
            if preferred_time is None:
                curr_time = max(0, self.media_player.get_time() / 1000.0)
            else:
                curr_time = max(0, preferred_time)
            cmd = [
                ffmpeg_path, '-ss', f"{curr_time:.3f}", '-i', self.input_file_path,
                '-frames:v', '1', '-q:v', '2', '-update', '1', '-y', snapshot_path
            ]
            logger.info(f"Executing FFmpeg command: {' '.join(cmd)}")
            subprocess.run(
                cmd, check=True,
                creationflags=(subprocess.CREATE_NO_WINDOW if sys.platform == "win32" else 0)
            )
            logger.info(f"Successfully created snapshot at {snapshot_path}")
            return True, "Snapshot created."
        except Exception as e:
            logger.error(f"FFmpeg snapshot failed. Command: {' '.join(cmd)}", exc_info=True)
            return False, f"FFmpeg snapshot failed: {e}"
