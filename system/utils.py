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
import weakref
from PyQt5.QtCore import QTimer
if sys.platform == 'win32':
    _base_dir = os.path.abspath(os.path.join(os.path.dirname(__file__), '..'))
    _bin_dir = os.path.join(_base_dir, 'binaries')
    if os.path.exists(_bin_dir):
        os.environ["MPV_HOME"] = _bin_dir
        os.environ["MPV_DYLIB_PATH"] = os.path.join(_bin_dir, "libmpv-2.dll")
        if hasattr(os, 'add_dll_directory'):
            try:
                os.add_dll_directory(_bin_dir)
            except Exception:
                pass
        os.environ['PATH'] = _bin_dir + os.pathsep + os.environ.get('PATH','')
try:
    import mpv
except (ImportError, OSError):
    class MockMPV: 
        log_path = None

        class MPV:
            def __init__(self, *args, **kwargs): pass

            def terminate(self): pass
    mpv = MockMPV()

from logging.handlers import RotatingFileHandler
from typing import Optional, Tuple

class DependencyDoctor:
    @staticmethod
    def get_bin_dir(base_dir: str) -> str:
        return os.path.join(base_dir, 'binaries')
    @staticmethod
    def check_ffmpeg(base_dir: str) -> Tuple[bool, str, str]:
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
    @staticmethod
    def kill_orphans(process_names: list = ["ffmpeg.exe", "ffprobe.exe", "mpv.exe", "ffplay.exe"]):
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

            from PyQt5.QtWidgets import QApplication
            if QApplication.instance():
                QApplication.instance().quit()
            else:
                sys.exit(1)
        t = threading.Thread(target=watchdog, daemon=True)
        t.start()
    @staticmethod
    def cleanup_temp_files(prefix: str = "fvs_"):
        temp_dir = tempfile.gettempdir()
        patterns = [prefix, "fvs_job_"]
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
    @staticmethod
    def style_and_size_msg_box(msg_box: 'QMessageBox', copy_text: str, copy_btn_label: str = "Copy to Clipboard"):
        from PyQt5.QtWidgets import QSpacerItem, QSizePolicy, QGridLayout, QApplication
        from PyQt5.QtCore import Qt, QTimer, QUrl
        from PyQt5.QtGui import QDesktopServices
        layout = msg_box.layout()
        if isinstance(layout, QGridLayout):
            layout.addItem(QSpacerItem(800, 500, QSizePolicy.Minimum, QSizePolicy.Expanding), layout.rowCount(), 0, 1, layout.columnCount())
        copy_btn = msg_box.addButton(copy_btn_label, msg_box.ActionRole)

        from app import tr
        report_label = tr("report_to_alon")
        report_btn = msg_box.addButton(report_label, msg_box.ActionRole)

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

        def on_report():
            clipboard = QApplication.clipboard()
            clipboard.setText(copy_text)
            subject = "[Fortnite Video Software] Error Report"
            body = f"The following error occurred:\n\n{copy_text}"

            import urllib.parse
            mailto_url = f"mailto:alon.reich@gmail.com?subject={urllib.parse.quote(subject)}&body={urllib.parse.quote(body)}"
            QDesktopServices.openUrl(QUrl(mailto_url))
            msg_box.close()
        copy_btn.clicked.connect(on_copy)
        report_btn.clicked.connect(on_report)
        for btn in msg_box.buttons():
            btn.setCursor(Qt.PointingHandCursor)

class ConsoleManager:
    _log_files = []
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
        try:
            log_dir = os.path.join(base_dir, "logs")
            os.makedirs(log_dir, exist_ok=True)
            source_tag = ConsoleManager._source_tag(logger_name)
            raw_log_path = os.path.join(log_dir, f"mpv_{source_tag}.raw.log")
            mpv.log_path = os.path.join(log_dir, f"{app_prefix}_mpv.log")
            f = open(raw_log_path, 'a', encoding='utf-8')
            ConsoleManager._log_files.append(f)
            try:
                import faulthandler
                os.dup2(f.fileno(), sys.stdout.fileno())
                os.dup2(f.fileno(), sys.stderr.fileno())
                faulthandler.enable(f)
            except Exception: pass
            logger.info("NATIVE DEBUG LOGGING ACTIVE")
        except Exception as e:
            logger.error(f"Failed to setup native logging: {e}")

        def global_exception_handler(exc_type, exc_value, exc_traceback):
            if issubclass(exc_type, KeyboardInterrupt):
                sys.__excepthook__(exc_type, exc_value, exc_traceback)
                return
            error_msg = "".join(traceback.format_exception(exc_type, exc_value, exc_traceback))
            logger.critical(f"UNCAUGHT EXCEPTION:\n{error_msg}")
            try:
                from PyQt5.QtWidgets import QMessageBox, QApplication
                if QApplication.instance():
                    msg_box = QMessageBox(None)
                    msg_box.setIcon(QMessageBox.Critical)
                    msg_box.setWindowTitle("Critical Error")
                    msg_box.setText(f"An unexpected error occurred.\n\n{exc_value}\n\nDetails saved to log.")
                    UIManager.style_and_size_msg_box(msg_box, error_msg)
                    msg_box.exec_()
                    QApplication.instance().quit()
                else:
                    sys.exit(1)
            except: 
                sys.exit(1)
        sys.excepthook = global_exception_handler
        if sys.platform == "win32":
            try:
                import ctypes
                hwnd = ctypes.windll.kernel32.GetConsoleWindow()
                if hwnd != 0:
                    ctypes.windll.user32.ShowWindow(hwnd, 0)
            except: pass

        import atexit

        def close_logs():
            for f in ConsoleManager._log_files:
                try:
                    f.close()
                except: pass
        atexit.register(close_logs)
        return logger

class LogManager:
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

class MPVSafetyManager:
    _mpv_creation_lock = threading.Lock()
    _instances = weakref.WeakSet()
    @staticmethod
    def log_mpv_diagnostics(player, logger, context_tag="GENERAL"):
        if not player or not logger: return
        try:
            p_id = id(player)
            handle = getattr(player, 'handle', 'UNKNOWN')
            is_shutdown = getattr(player, '_core_shutdown', False)
            try:
                path = getattr(player, 'path', 'NONE')
                paused = getattr(player, 'pause', 'UNKNOWN')
                time_pos = getattr(player, 'time_pos', 'UNKNOWN')
            except:
                path, paused, time_pos = "UNREADABLE", "UNREADABLE", "UNREADABLE"
            logger.info(f"DIAGNOSTICS [{context_tag}]: MPV Object ID={p_id} | Handle={handle} | CoreShutdown={is_shutdown} | Path={path} | Paused={paused} | Time={time_pos}")
        except Exception as e:
            logger.error(f"DIAGNOSTICS [{context_tag}]: Failed to extract MPV state: {e}")
    @staticmethod
    def safe_mpv_shutdown(player, timeout=2.0, lock=None):
        if not player:
            return True

        import mpv
        try:
            if getattr(player, '_core_shutdown', False) or getattr(player, '_safe_shutdown_initiated', False):
                return True
            player._safe_shutdown_initiated = True
            
            def _do_shutdown():
                try:
                    if lock:
                        if lock.acquire(timeout=0.2):
                            try:
                                player.pause = True
                                player.stop()
                                player.terminate()
                            finally:
                                lock.release()
                        else:
                            player.pause = True
                            player.terminate()
                    else:
                        player.pause = True
                        player.stop()
                        player.terminate()
                except (AttributeError, mpv.ShutdownError, SystemError):
                    pass
            t = threading.Thread(target=_do_shutdown, daemon=True)
            t.start()
            confirm_timeout = min(0.5, timeout)
            start_time = time.time()
            while time.time() - start_time < confirm_timeout:
                try:
                    if getattr(player, '_core_shutdown', True):
                        break
                except (AttributeError, mpv.ShutdownError):
                    break
                time.sleep(0.05)
            MPVSafetyManager.cleanup_mpv_event_callbacks(player)
            return True
        except Exception:
            return False
    @staticmethod
    def safe_mpv_set(player, property_name, value, max_attempts=3, lock=None):
        if not player or getattr(player, '_core_shutdown', False): return False
        for attempt in range(max_attempts):
            try:
                if lock:
                    if not lock.acquire(timeout=0.1):
                        continue
                try:
                    setattr(player, property_name, value)
                    return True
                finally:
                    if lock: lock.release()
            except Exception:
                time.sleep(0.02)
        return False
    @staticmethod
    def safe_mpv_get(player, property_name, default=None, max_attempts=3, lock=None):
        if not player or getattr(player, '_core_shutdown', False): return default
        for attempt in range(max_attempts):
            try:
                if lock:
                    if not lock.acquire(timeout=0.1):
                        continue
                try:
                    return getattr(player, property_name, default)
                finally:
                    if lock: lock.release()
            except Exception:
                time.sleep(0.02)
        return default
    @staticmethod
    def safe_mpv_command(player, command, *args, max_attempts=3, lock=None):
        if not player or getattr(player, '_core_shutdown', False): return False
        for attempt in range(max_attempts):
            try:
                if lock:
                    if not lock.acquire(timeout=0.1):
                        continue
                try:
                    player.command(command, *args)
                    return True
                finally:
                    if lock: lock.release()
            except Exception:
                time.sleep(0.02)
        return False
    @staticmethod
    def cleanup_mpv_event_callbacks(player):
        if not player: return
        try:
            if hasattr(player, '_event_callbacks'):
                player._event_callbacks.clear()
            if hasattr(player, '_property_observers'):
                player._property_observers.clear()
            if hasattr(player, 'event_callback'):
                player.event_callback = None
        except:
            pass
    @staticmethod
    def create_safe_mpv(**kwargs):
        with MPVSafetyManager._mpv_creation_lock:
            try:
                import mpv
                original_log_handler = kwargs.pop('log_handler', None)
                if original_log_handler:
                    def safe_log_proxy(level, prefix, text):
                        try:
                            original_log_handler(level, prefix, text)
                        except:
                            pass
                    kwargs['log_handler'] = safe_log_proxy
                wid = kwargs.get('wid')
                if wid is not None:
                    try:
                        wid_int = int(wid)
                        if wid_int <= 0:
                            kwargs.pop('wid')
                        else:
                            kwargs['wid'] = wid_int
                    except (ValueError, TypeError):
                        kwargs.pop('wid')
                safe_kwargs = {
                    'hr_seek': 'yes',
                    'osc': False,
                    'ytdl': False,
                    'load_scripts': False,
                    'config': False,
                    'keep_open': kwargs.get('keep_open', 'yes'),
                    'loglevel': 'error',
                }
                extra_flags = kwargs.pop('extra_mpv_flags', [])
                kwargs.pop('load_scripts', None)
                kwargs.pop('config', None)
                kwargs.pop('osc', None)
                kwargs.pop('ytdl', None)
                safe_kwargs.update(kwargs)
                player = mpv.MPV(**safe_kwargs)
                time.sleep(0.1)
                player._safe_shutdown_initiated = False
                MPVSafetyManager._instances.add(player)
                for prop, val in extra_flags:
                    try:
                        player.set_property(prop, val)
                    except:
                        pass
                return player
            except Exception as e:
                print(f"Failed to create safe MPV instance: {e}")
                return None
    @staticmethod
    def register_global_shutdown_handler():
        import atexit

        def global_shutdown():
            for player in list(MPVSafetyManager._instances):
                try:
                    if hasattr(player, '_safe_shutdown_initiated') and not player._safe_shutdown_initiated:
                        player._safe_shutdown_initiated = True
                        MPVSafetyManager.safe_mpv_shutdown(player, timeout=1.0)
                except: pass
        atexit.register(global_shutdown)
