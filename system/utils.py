import os
import sys
import psutil
import logging
import tempfile
import traceback
import subprocess
import shutil
import time
import threading
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
        [FIX #10] Aggressively kills stray processes associated with this project.
        """
        my_pid = os.getpid()
        base_dir = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
        bin_dir = os.path.join(base_dir, 'binaries')
        for proc in psutil.process_iter(['pid', 'name', 'exe', 'cmdline']):
            try:
                if proc.info['pid'] == my_pid:
                    continue
                name = proc.info['name']
                if name in process_names:
                    proc_exe = proc.info.get('exe')
                    cmdline = " ".join(proc.info.get('cmdline') or [])
                    if (proc_exe and bin_dir in os.path.abspath(proc_exe)) or (base_dir in cmdline):
                        proc.kill()
            except (psutil.NoSuchProcess, psutil.AccessDenied):
                pass
    @staticmethod
    def cleanup_temp_files(prefix: str = "fvs_"):
        """
        [FIX #3] Aggressively cleans up temporary files from previous or failed sessions.
        No longer waits for 6 hours; if they match our patterns, they are deleted.
        """
        temp_dir = tempfile.gettempdir()
        patterns = [
            prefix, "core-", "intro-", "ffmpeg2pass-", "drawtext-", 
            "filter_complex-", "concat-", "thumb-", "snapshot-"
        ]
        try:
            for filename in os.listdir(temp_dir):
                if any(filename.startswith(p) for p in patterns):
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
            local_tmp = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), ".tmp")
            if os.path.exists(local_tmp):
                shutil.rmtree(local_tmp)
                os.makedirs(local_tmp, exist_ok=True)
        except:
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
        [FIX #5] Acquires a named lock file with a small retry logic to handle OS lag.
        Returns (success, file_handle).
        """
        pid_file = os.path.join(tempfile.gettempdir(), f"{app_name}.pid")
        for attempt in range(3):
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
                if attempt < 2:
                    time.sleep(0.2)
                continue
        return False, None

class ConsoleManager:
    @staticmethod
    def _source_tag(logger_name: str) -> str:
        mapping = {
            "Main_App": "main_app",
            "Crop_Tool": "crop_tools",
            "Advanced_Editor": "advanced_editor",
        }
        return mapping.get(str(logger_name), str(logger_name).strip().lower())
    @staticmethod
    def _start_native_bridge(source_tag: str, raw_log_path: str, shared_vlc_log_path: str):
        bridge_logger = logging.getLogger(f"VLC_Aggregator_{source_tag}_{os.getpid()}")
        for h in bridge_logger.handlers[:]:
            bridge_logger.removeHandler(h)
        bridge_logger.setLevel(logging.INFO)
        bridge_logger.propagate = False
        bridge_handler = RotatingFileHandler(
            shared_vlc_log_path,
            maxBytes=5*1024*1024,
            backupCount=1,
            encoding='utf-8',
        )
        bridge_handler.setFormatter(logging.Formatter("%(asctime)s | %(message)s", datefmt="%Y-%m-%d %H:%M:%S"))
        bridge_logger.addHandler(bridge_handler)

        def _tail_raw_forever():
            pos = 0
            while True:
                try:
                    if not os.path.exists(raw_log_path):
                        time.sleep(0.5)
                        continue
                    with open(raw_log_path, 'rb') as rf:
                        rf.seek(pos)
                        chunk_bytes = rf.read()
                        if chunk_bytes:
                            pos += len(chunk_bytes)
                            chunk = chunk_bytes.decode('utf-8', errors='ignore')
                            for line in chunk.splitlines():
                                clean_line = line.strip()
                                if clean_line:
                                    bridge_logger.info("[%s] %s", source_tag, clean_line)
                        else:
                            time.sleep(0.2)
                except Exception:
                    time.sleep(1.0)
        t = threading.Thread(target=_tail_raw_forever, daemon=True)
        t.start()
    @staticmethod
    def initialize(base_dir: str, log_filename: str, logger_name: str):
        app_prefix = logger_name.lower().replace(" ", "_")
        LogManager.truncate_vlc_log(base_dir, app_prefix, max_size_mb=5)
        if not log_filename.startswith(app_prefix):
            final_log_filename = f"{app_prefix}_{log_filename}"
        else:
            final_log_filename = log_filename
        logger = LogManager.setup_logger(base_dir, final_log_filename, logger_name)
        log_dir = os.path.join(base_dir, "logs")
        os.makedirs(log_dir, exist_ok=True)
        vlc_log_path = os.path.join(log_dir, f"{app_prefix}_vlc.log")
        source_tag = ConsoleManager._source_tag(logger_name)
        raw_log_path = os.path.join(log_dir, f"vlc_{source_tag}.raw.log")
        os.environ["FVS_VLC_SOURCE_TAG"] = source_tag
        os.environ["FVS_VLC_RAW_LOG"] = raw_log_path
        ConsoleManager._start_native_bridge(source_tag, raw_log_path, vlc_log_path)

        def global_exception_handler(exc_type, exc_value, exc_traceback):
            if issubclass(exc_type, KeyboardInterrupt):
                sys.__excepthook__(exc_type, exc_value, exc_traceback)
                return
            error_msg = "".join(traceback.format_exception(exc_type, exc_value, exc_traceback))
            logger.critical(f"UNCAUGHT EXCEPTION:\n{error_msg}")
            try:
                from PyQt5.QtWidgets import QMessageBox, QApplication, QSpacerItem, QSizePolicy, QGridLayout
                if QApplication.instance():
                    msg_box = QMessageBox(None)
                    msg_box.setIcon(QMessageBox.Critical)
                    msg_box.setWindowTitle("Critical Error")
                    msg_box.setText(f"An unexpected error occurred.\n\n{exc_value}\n\nDetails saved to log.")
                    layout = msg_box.layout()
                    if isinstance(layout, QGridLayout):
                        layout.addItem(QSpacerItem(600, 0, QSizePolicy.Minimum, QSizePolicy.Expanding), layout.rowCount(), 0, 1, layout.columnCount())
                    msg_box.exec_()
            except: pass
        sys.excepthook = global_exception_handler
        try:
            ConsoleManager._f_keepalive = open(raw_log_path, 'w', buffering=1, encoding='utf-8')
            f = ConsoleManager._f_keepalive
            os.dup2(f.fileno(), sys.stdout.fileno())
            os.dup2(f.fileno(), sys.stderr.fileno())

            import faulthandler
            faulthandler.enable(f)
            stamp = time.strftime("%Y-%m-%d %H:%M:%S")
            f.write(f"\n[{stamp}] [{source_tag}] [pid={os.getpid()}] --- NATIVE DEBUG LOGGING ACTIVE ---\n")
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
    def truncate_vlc_log(base_dir: str, app_prefix: str, max_size_mb: int = 5):
        """
        Maintains logs size by keeping only the last N MB (FIFO).
        """
        try:
            log_path = os.path.join(base_dir, "logs", f"{app_prefix}_vlc.log")
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
        handler = RotatingFileHandler(log_path, maxBytes=5*1024*1024, backupCount=3, encoding='utf-8')
        formatter = logging.Formatter('%(asctime)s - %(name)s - %(levelname)s - %(message)s')
        handler.setFormatter(formatter)
        logger.addHandler(handler)
        console = logging.StreamHandler()
        console.setFormatter(formatter)
        logger.addHandler(console)
        return logger


