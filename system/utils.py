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
import faulthandler
try:
    import mpv
except ImportError:
    class MockMPV: pass
    mpv = MockMPV()

from logging.handlers import RotatingFileHandler
from typing import Optional, Tuple

class DependencyDoctor:
    """
    Centralized health check for external dependencies (FFmpeg).
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

class ProcessManager:
    """
    Manages application lifecycle, PID locking, and zombie cleanup.
    """
    @staticmethod
    def kill_orphans(process_names: list = ["ffmpeg.exe", "ffprobe.exe", "mpv.exe", "ffplay.exe"]):
        """
        [FIX #1 & #3] Aggressively kills stray processes associated with this project.
        Enhanced to be more precise and avoid killing unrelated system processes.
        """
        my_pid = os.getpid()
        base_dir = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
        bin_dir = os.path.join(base_dir, 'binaries')
        for proc in psutil.process_iter(['pid', 'name', 'exe', 'cmdline', 'ppid']):
            try:
                if proc.info['pid'] == my_pid:
                    continue
                is_orphan = proc.info.get('ppid') == 1
                name = (proc.info['name'] or "").lower()
                target_names = [n.lower() for n in process_names]
                if name in target_names or any(tn in name for tn in target_names):
                    proc_exe = proc.info.get('exe')
                    cmdline = " ".join(proc.info.get('cmdline') or [])
                    is_our_binary = proc_exe and bin_dir.lower() in os.path.abspath(proc_exe).lower()
                    is_our_project = base_dir.lower() in cmdline.lower()
                    if is_our_binary or is_our_project:
                        try:
                            proc.kill()
                        except (psutil.NoSuchProcess, psutil.AccessDenied):
                            pass
            except (psutil.NoSuchProcess, psutil.AccessDenied):
                pass
    @staticmethod
    def start_parent_watchdog():
        """
        [FIX #1] Starts a background thread that monitors the parent process.
        If the parent dies, this process will immediately kill itself.
        Used by sub-tools like Crop Tool to avoid becoming zombies.
        """
        parent_pid = os.getppid()
        if parent_pid <= 1:
            return
            
        def watchdog():
            while True:
                try:
                    if not psutil.pid_exists(parent_pid):
                        break
                    p = psutil.Process(parent_pid)
                    if p.status() == psutil.STATUS_ZOMBIE:
                        break
                except (psutil.NoSuchProcess, psutil.AccessDenied):
                    break
                time.sleep(2.0)
            ProcessManager.kill_orphans()
            os._exit(1)
        t = threading.Thread(target=watchdog, daemon=True)
        t.start()
    @staticmethod
    def cleanup_temp_files(prefix: str = "fvs_"):
        """
        [FIX #3] Aggressively cleans up temporary files from previous or failed sessions.
        """
        temp_dir = tempfile.gettempdir()
        patterns = [
            prefix, "core-", "intro-", "ffmpeg2pass-", "drawtext-", 
            "filter_complex-", "concat-", "thumb-", "snapshot-", "fvs_job_", "thumb_preview_"
        ]
        try:
            for filename in os.listdir(temp_dir):
                if any(filename.startswith(p) for p in patterns):
                    file_path = os.path.join(temp_dir, filename)
                    try:
                        if os.path.isfile(file_path):
                            os.remove(file_path)
                        elif os.path.isdir(file_path):
                            shutil.rmtree(file_path, ignore_errors=True)
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

class UIManager:
    """
    Shared UI helper for standardizing diagnostic dialogs.
    """
    @staticmethod
    def style_and_size_msg_box(msg_box: 'QMessageBox', copy_text: str, copy_btn_label: str = "Copy to Clipboard"):
        """
        Standardizes diagnostic popups: 800x500 size, adds 'Copy to Clipboard',
        and ensures all buttons have a pointing hand cursor.
        """

        from PyQt5.QtWidgets import QSpacerItem, QSizePolicy, QGridLayout, QApplication
        from PyQt5.QtCore import Qt, QTimer
        layout = msg_box.layout()
        if isinstance(layout, QGridLayout):
            layout.addItem(QSpacerItem(800, 500, QSizePolicy.Minimum, QSizePolicy.Expanding), layout.rowCount(), 0, 1, layout.columnCount())
        copy_btn = msg_box.addButton(copy_btn_label, msg_box.ActionRole)
        
        def on_copy():
            clipboard = QApplication.clipboard()
            clipboard.setText(copy_text)
            copy_btn.setText("✓ Copied!")
            
            def _reset_text():
                try:
                    copy_btn.setText(copy_btn_label)
                except RuntimeError:
                    pass
            QTimer.singleShot(2000, _reset_text)
        copy_btn.clicked.connect(on_copy)
        for btn in msg_box.buttons():
            btn.setCursor(Qt.PointingHandCursor)

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
    def initialize(base_dir: str, log_filename: str, logger_name: str):
        app_prefix = logger_name.lower().replace(" ", "_")
        if not log_filename.startswith(app_prefix):
            final_log_filename = f"{app_prefix}_{log_filename}"
        else:
            final_log_filename = log_filename
        logger = LogManager.setup_logger(base_dir, final_log_filename, logger_name)
        
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
                    UIManager.style_and_size_msg_box(msg_box, error_msg)
                    msg_box.exec_()
            except: pass
        sys.excepthook = global_exception_handler
        log_dir = os.path.join(base_dir, "logs")
        os.makedirs(log_dir, exist_ok=True)
        source_tag = ConsoleManager._source_tag(logger_name)
        if str(logger_name) == "Video_Merger":
            source_tag = "video_merger"
            mpv.log_path = os.path.join(log_dir, "mpv.log")
            raw_log_path = os.path.join(log_dir, f"mpv_{source_tag}.raw.log")
            try:
                ConsoleManager._f_keepalive = open(raw_log_path, 'w', buffering=1, encoding='utf-8')
            except: pass
        else:
            mpv.log_path = os.path.join(log_dir, f"{app_prefix}_mpv.log")
            raw_log_path = os.path.join(log_dir, f"mpv_{source_tag}.raw.log")
            try:
                ConsoleManager._f_keepalive = open(raw_log_path, 'w', buffering=1, encoding='utf-8')
            except: pass
        try:
            stamp = time.strftime("%Y-%m-%d %H:%M:%S")
            if 'f' in locals():
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


