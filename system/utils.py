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
        STRICT SAFETY: Only kills processes running from the application's 'binaries' directory.
        """
        my_pid = os.getpid()
        base_dir = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
        bin_dir = os.path.join(base_dir, 'binaries')
        for proc in psutil.process_iter(['pid', 'name', 'exe']):
            try:
                if proc.info['pid'] == my_pid:
                    continue
                if proc.info['name'] in process_names:
                    proc_exe = proc.info.get('exe')
                    if proc_exe and bin_dir in os.path.abspath(proc_exe):
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
        Acquires a named lock file using OS-level file locking (msvcrt/fcntl).
        Returns (success, file_handle).
        If success, file_handle MUST be kept open to maintain the lock.
        """
        pid_file = os.path.join(tempfile.gettempdir(), f"{app_name}.pid")
        try:
            f = open(pid_file, "a+")
            f.seek(0)
            if sys.platform == "win32":
                import msvcrt
                msvcrt.locking(f.fileno(), msvcrt.LK_NBLCK, 1)
            else:
                import fcntl
                fcntl.lockf(f, fcntl.LOCK_EX | fcntl.LOCK_NB)
            f.seek(0)
            f.truncate()
            f.write(str(os.getpid()))
            f.flush()
            return True, f
        except (IOError, OSError, PermissionError):
            return False, None

class ConsoleManager:
    @staticmethod
    def initialize(base_dir: str, log_filename: str, logger_name: str):
        LogManager.truncate_vlc_log(base_dir)
        logger = LogManager.setup_logger(base_dir, log_filename, logger_name)
        log_dir = os.path.join(base_dir, "logs")
        vlc_log_path = os.path.join(log_dir, "vlc.log")

        def global_exception_handler(exc_type, exc_value, exc_traceback):
            if issubclass(exc_type, KeyboardInterrupt):
                sys.__excepthook__(exc_type, exc_value, exc_traceback)
                return
            error_msg = "".join(traceback.format_exception(exc_type, exc_value, exc_traceback))
            logger.critical(f"UNCAUGHT EXCEPTION:\n{error_msg}")
            try:
                from PyQt5.QtWidgets import QMessageBox, QApplication
                if QApplication.instance():
                    QMessageBox.critical(None, "Critical Error", f"An unexpected error occurred.\n\n{exc_value}\n\nDetails saved to log.")
            except: pass
        sys.excepthook = global_exception_handler
        try:
            f = open(vlc_log_path, 'a', buffering=1, encoding='utf-8')
            os.dup2(f.fileno(), sys.stdout.fileno())
            os.dup2(f.fileno(), sys.stderr.fileno())

            import faulthandler
            faulthandler.enable(f)
            f.write("\n--- NATIVE DEBUG LOGGING ACTIVE ---\n")
            f.flush()
        except Exception as e:
            print(f"Failed FD redirection: {e}")
        if sys.platform == "win32":
            try:
                import ctypes
                hwnd = ctypes.windll.kernel32.GetConsoleWindow()
                if hwnd != 0:
                    ctypes.windll.user32.ShowWindow(hwnd, 0)
            except: pass
        return logger

class LogManager:
    @staticmethod
    def truncate_vlc_log(base_dir: str, max_size_mb: int = 10):
        """
        Maintains logs/vlc.log size by keeping only the last N MB (FIFO).
        """
        try:
            log_path = os.path.join(base_dir, "logs", "vlc.log")
            if not os.path.exists(log_path):
                return
            file_size = os.path.getsize(log_path)
            max_bytes = max_size_mb * 1024 * 1024
            if file_size > max_bytes:
                with open(log_path, 'rb') as f:
                    f.seek(-max_bytes, os.SEEK_END)
                    data = f.read()
                with open(log_path, 'wb') as f:
                    f.write(data)
        except Exception:
            pass
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

