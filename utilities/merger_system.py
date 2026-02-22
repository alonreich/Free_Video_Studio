import os
import sys
import psutil
import logging
import tempfile
import subprocess
import shutil
import traceback
import time
import threading
from logging.handlers import RotatingFileHandler
from typing import Optional, Tuple

class MergerDependencyDoctor:
    @staticmethod
    def get_bin_dir(base_dir: str) -> str:
        return os.path.join(base_dir, 'binaries')
    @staticmethod
    def check_ffmpeg(base_dir: str) -> Tuple[bool, str, str]:
        bin_dir = MergerDependencyDoctor.get_bin_dir(base_dir)
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
        return False, "", f"FFmpeg or FFprobe binaries are missing in {bin_dir} or System PATH."

class MergerProcessManager:
    @staticmethod
    def kill_orphans(process_names: list = ["ffmpeg.exe", "ffprobe.exe", "ffmpeg", "ffprobe"]):
        """Kill orphans and zombies related to this application."""
        my_pid = os.getpid()
        try:
            for proc in psutil.process_iter(['pid', 'name', 'ppid']):
                try:
                    pinfo = proc.info
                    if pinfo['pid'] == my_pid:
                        continue
                    if pinfo['name'].lower() in [n.lower() for n in process_names]:
                        if pinfo.get('ppid') == my_pid or pinfo.get('ppid') in (0, 1):
                            proc.kill()
                except (psutil.NoSuchProcess, psutil.AccessDenied):
                    pass
        except Exception as e:
            logging.getLogger("Video_Merger").error(f"kill_orphans failed: {e}")
    @staticmethod
    def cleanup_temp_files(
        prefixes: tuple[str, ...] | None = None,
        min_age_seconds: int = 600,
        prefix: str | None = None,
    ):
        temp_dir = tempfile.gettempdir()
        logger = logging.getLogger("Video_Merger")
        if prefix:
            if prefixes is None:
                prefixes = (prefix,)
            elif prefix not in prefixes:
                prefixes = tuple(prefixes) + (prefix,)
        if prefixes is None:
            prefixes = (
                "fvs_merger_",
                "fvs_thumbs_",
                "fvs_wiz_",
                "fvs_offset_",
                "fvs_wave_",
            )
        try:
            now = time.time()
            for filename in os.listdir(temp_dir):
                if not (filename.startswith("ffmpeg2pass") or any(filename.startswith(p) for p in prefixes)):
                    continue
                file_path = os.path.join(temp_dir, filename)
                try:
                    if os.path.exists(file_path):
                        mtime = os.path.getmtime(file_path)
                        if now - mtime < max(0, int(min_age_seconds)):
                            continue
                    if os.path.isfile(file_path):
                        os.remove(file_path)
                    elif os.path.isdir(file_path):
                        shutil.rmtree(file_path, ignore_errors=True)
                except Exception as e:
                    logger.debug(f"Could not cleanup {file_path}: {e}")
        except Exception as e:
            logger.error(f"cleanup_temp_files error: {e}")
    @staticmethod
    def acquire_pid_lock(app_name: str) -> Tuple[bool, Optional[object]]:
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

class MergerConsoleManager:
    _f_keepalive = None
    @staticmethod
    def initialize(base_dir: str, log_filename: str, logger_name: str):
        logger = MergerLogManager.setup_logger(base_dir, log_filename, logger_name)
        log_dir = os.path.join(base_dir, "logs")
        try:
            import mpv
        except ImportError:
            class MockMPV: pass
            mpv = MockMPV()
        source_tag = "video_merger"
        if str(logger_name) == "Video_Merger":
            mpv.log_path = os.path.join(log_dir, "mpv.log")
            raw_log_path = os.path.join(log_dir, f"mpv_{source_tag}.raw.log")
            try:
                MergerConsoleManager._f_keepalive = open(raw_log_path, 'w', buffering=1, encoding='utf-8')
                f = MergerConsoleManager._f_keepalive
                os.dup2(f.fileno(), sys.stdout.fileno())
                os.dup2(f.fileno(), sys.stderr.fileno())

                import faulthandler
                faulthandler.enable(f)
                source_tag = "video_merger"
            except: pass
            
        def global_exception_handler(exc_type, exc_value, exc_traceback):
            if issubclass(exc_type, KeyboardInterrupt):
                sys.__excepthook__(exc_type, exc_value, exc_traceback)
                return
            error_msg = "".join(traceback.format_exception(exc_type, exc_value, exc_traceback))
            logger.critical(f"UNCAUGHT EXCEPTION:\n{error_msg}")
            try:
                from PyQt5.QtWidgets import QMessageBox, QApplication
                from system.utils import UIManager
                if QApplication.instance():
                    msg = QMessageBox()
                    msg.setIcon(QMessageBox.Critical)
                    msg.setWindowTitle("Critical Error")
                    msg.setText(f"An unexpected error occurred.\n\n{exc_value}\n\nDetails saved to log.")
                    UIManager.style_and_size_msg_box(msg, error_msg)
                    msg.exec_()
            except: pass
        sys.excepthook = global_exception_handler
        if sys.platform == "win32":
            try:
                import ctypes
                hwnd = ctypes.windll.kernel32.GetConsoleWindow()
                if hwnd != 0:
                    ctypes.windll.user32.ShowWindow(hwnd, 0)
            except: pass
        return logger

class MergerLogManager:
    @staticmethod
    def setup_logger(base_dir: str, log_filename: str, logger_name: str) -> logging.Logger:
        logger = logging.getLogger(logger_name)
        if logger.handlers:
            return logger
        logger.setLevel(logging.INFO)
        log_dir = os.path.join(base_dir, "logs")
        os.makedirs(log_dir, exist_ok=True)
        log_path = os.path.join(log_dir, log_filename)
        handler = RotatingFileHandler(log_path, maxBytes=5*1024*1024, backupCount=3, encoding='utf-8')
        formatter = logging.Formatter('%(asctime)s - %(name)s - %(levelname)s - %(message)s')
        handler.setFormatter(formatter)
        logger.addHandler(handler)
        console = logging.StreamHandler()
        console.setFormatter(formatter)
        logger.addHandler(console)
        return logger
