import os
import sys
import psutil
import logging
import tempfile
import traceback
import subprocess
import shutil
from logging.handlers import RotatingFileHandler
from typing import Optional, Tuple

class DependencyDoctor:
    """
    Centralized health check for external dependencies (VLC, FFmpeg).
    """
    @staticmethod
    def get_bin_dir(base_dir: str) -> str:
        return os.path.join(base_dir, 'binaries')
    @staticmethod
    def check_ffmpeg(base_dir: str) -> Tuple[bool, str, str]:
        """
        Validates FFmpeg/FFprobe presence.
        Returns (is_valid, ffmpeg_path, error_message).
        """
        bin_dir = DependencyDoctor.get_bin_dir(base_dir)
        ffmpeg_exe = "ffmpeg.exe" if sys.platform == "win32" else "ffmpeg"
        ffprobe_exe = "ffprobe.exe" if sys.platform == "win32" else "ffprobe"
        ffmpeg_path = os.path.join(bin_dir, ffmpeg_exe)
        ffprobe_path = os.path.join(bin_dir, ffprobe_exe)
        if os.path.exists(ffmpeg_path) and os.path.exists(ffprobe_path):
            return True, ffmpeg_path, ""
        path_ffmpeg = shutil.which(ffmpeg_exe)
        path_ffprobe = shutil.which(ffprobe_exe)
        if path_ffmpeg and path_ffprobe:
            return True, path_ffmpeg, ""
        return False, "", "FFmpeg or FFprobe binaries are missing."
    @staticmethod
    def find_vlc_path() -> Optional[str]:
        """
        Attempts to locate VLC installation dynamically.
        """
        common_paths = [
            r"C:\Program Files\VideoLAN\VLC",
            r"C:\Program Files (x86)\VideoLAN\VLC"
        ]
        for p in common_paths:
            if os.path.exists(os.path.join(p, "libvlc.dll")):
                return p
        if sys.platform == "win32":
            import winreg
            try:
                key = winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE, r"SOFTWARE\VideoLAN\VLC")
                val, _ = winreg.QueryValueEx(key, "InstallDir")
                if val and os.path.exists(os.path.join(val, "libvlc.dll")):
                    return val
            except Exception:
                pass
        return None

class ProcessManager:
    """
    Manages application lifecycle, PID locking, and zombie cleanup.
    """
    @staticmethod
    def kill_orphans(process_names: list = ["ffmpeg.exe", "ffprobe.exe"]):
        """
        Kills stray processes that might be lingering from previous sessions.
        """
        my_pid = os.getpid()
        for proc in psutil.process_iter(['pid', 'name']):
            try:
                if proc.info['name'] in process_names and proc.info['pid'] != my_pid:
                    proc.kill()
            except (psutil.NoSuchProcess, psutil.AccessDenied):
                pass
    @staticmethod
    def cleanup_temp_files(prefix: str = "fvs_"):
        """
        Cleans up temporary files from previous sessions.
        [FIX] Also aggressively removes any __pycache__ folders found in the project.
        """
        temp_dir = tempfile.gettempdir()
        try:
            for filename in os.listdir(temp_dir):
                if filename.startswith(prefix) or filename.startswith("core-") or filename.startswith("ffmpeg2pass"):
                    file_path = os.path.join(temp_dir, filename)
                    try:
                        if os.path.isfile(file_path):
                            os.remove(file_path)
                        elif os.path.isdir(file_path):
                            shutil.rmtree(file_path)
                    except Exception:
                        pass
        except Exception:
            pass
        try:
            project_root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
            for root, dirs, files in os.walk(project_root):
                if "__pycache__" in dirs:
                    cache_path = os.path.join(root, "__pycache__")
                    try:
                        shutil.rmtree(cache_path)
                    except:
                        pass
        except:
            pass
    @staticmethod
    def acquire_pid_lock(app_name: str) -> Tuple[bool, Optional[object]]:
        """
        Acquires a named lock file. Returns (success, file_handle).
        [FIX] Better stale lock handling for Windows.
        """
        pid_file = os.path.join(tempfile.gettempdir(), f"{app_name}.pid")
        if os.path.exists(pid_file):
            try:
                os.remove(pid_file)
            except OSError:
                try:
                    with open(pid_file, 'r') as f:
                        old_pid = int(f.read().strip())
                    if not psutil.pid_exists(old_pid):
                        pass
                    else:
                        return False, None
                except:
                    pass
                return False, None
        try:
            f = open(pid_file, "w")
            if sys.platform == "win32":
                import msvcrt
                msvcrt.locking(f.fileno(), msvcrt.LK_NBLCK, 1)
            else:
                import fcntl
                fcntl.lockf(f, fcntl.LOCK_EX | fcntl.LOCK_NB)
            f.write(str(os.getpid()))
            f.flush()
            return True, f
        except (IOError, OSError):
            return False, None

class LogManager:
    @staticmethod
    def setup_logger(base_dir: str, log_filename: str, logger_name: str) -> logging.Logger:
        """
        Configures a rotating logger.
        """
        logger = logging.getLogger(logger_name)
        if logger.handlers:
            return logger
        logger.setLevel(logging.INFO)
        log_dir = os.path.join(base_dir, "logs")
        os.makedirs(log_dir, exist_ok=True)
        log_path = os.path.join(log_dir, log_filename)
        handler = RotatingFileHandler(log_path, maxBytes=10*1024*1024, backupCount=3, encoding='utf-8')
        formatter = logging.Formatter('%(asctime)s - %(name)s - %(levelname)s - %(message)s')
        handler.setFormatter(formatter)
        logger.addHandler(handler)
        console = logging.StreamHandler()
        console.setFormatter(formatter)
        logger.addHandler(console)
        return logger

