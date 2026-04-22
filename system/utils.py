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
import json
from PyQt5.QtCore import QTimer, QThread
try:
    import sip
except ImportError:
    sip = None
if sys.platform == 'win32':
    _base_dir = os.path.abspath(os.path.join(os.path.dirname(__file__), '..'))
    _bin_dir = os.path.join(_base_dir, 'binaries')
    if os.path.exists(_bin_dir):
        os.environ["MPV_HOME"] = _bin_dir
        os.environ["MPV_DYLIB_PATH"] = os.path.join(_bin_dir, "libmpv-2.dll")
        if hasattr(os, 'add_dll_directory'):
            try: os.add_dll_directory(_bin_dir)
            except: pass
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
    def get_bin_dir(base_dir: str) -> str: return os.path.join(base_dir, 'binaries')
    @staticmethod
    def check_ffmpeg(base_dir: str) -> Tuple[bool, str, str]:
        bin_dir = DependencyDoctor.get_bin_dir(base_dir)
        f_exe = "ffmpeg.exe" if sys.platform == "win32" else "ffmpeg"
        p_exe = "ffprobe.exe" if sys.platform == "win32" else "ffprobe"
        f_p = os.path.join(bin_dir, f_exe)
        p_p = os.path.join(bin_dir, p_exe)
        if os.path.exists(f_p) and os.path.exists(p_p): return True, f_p, ""
        s_f = shutil.which(f_exe); s_p = shutil.which(p_exe)
        if s_f and s_p: return True, s_f, ""
        return False, "", "FFmpeg or FFprobe binaries are missing."

class ProcessManager:
    @staticmethod
    def kill_orphans(process_names: list = ["ffmpeg.exe", "ffprobe.exe", "mpv.exe", "ffplay.exe"]):
        my_pid = os.getpid()
        base_dir = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
        bin_dir = os.path.join(base_dir, 'binaries')
        for proc in psutil.process_iter(['pid', 'name', 'exe', 'cmdline', 'ppid']):
            try:
                if proc.info['pid'] == my_pid: continue
                name = (proc.info['name'] or "").lower()
                target_names = [n.lower() for n in process_names]
                if name in target_names or any(tn in name for tn in target_names):
                    p_e = proc.info.get('exe'); cmd = " ".join(proc.info.get('cmdline') or [])
                    is_our = p_e and bin_dir.lower() in os.path.abspath(p_e).lower()
                    is_proj = base_dir.lower() in cmd.lower()
                    if is_our or is_proj:
                        try: proc.kill()
                        except: pass
            except: pass
    @staticmethod
    def start_parent_watchdog():
        p_pid = os.getppid()
        if p_pid <= 1: return

        def watchdog():
            while True:
                try:
                    if not psutil.pid_exists(p_pid): break
                    p = psutil.Process(p_pid)
                    if p.status() == psutil.STATUS_ZOMBIE: break
                except: break
                time.sleep(2.0)
            ProcessManager.kill_orphans()

            from PyQt5.QtWidgets import QApplication
            if QApplication.instance(): QApplication.instance().quit()
            else: sys.exit(1)
        t = threading.Thread(target=watchdog, daemon=True)
        t.start()
    @staticmethod
    def cleanup_temp_files(prefix: str = "fvs_"):
        t_d = tempfile.gettempdir(); pats = [prefix, "fvs_job_"]
        try:
            for n in os.listdir(t_d):
                if any(n.startswith(p) for p in pats):
                    p_h = os.path.join(t_d, n)
                    try:
                        if os.path.isfile(p_h): os.remove(p_h)
                        elif os.path.isdir(p_h): shutil.rmtree(p_h, ignore_errors=True)
                    except: pass
        except: pass
        try:
            p_r = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
            for r, ds, fs in os.walk(p_r):
                if "__pycache__" in ds:
                    try: shutil.rmtree(os.path.join(r, "__pycache__"))
                    except: pass
        except: pass
    @staticmethod
    def acquire_pid_lock(app_name: str) -> Tuple[bool, Optional[object]]:
        p_f = os.path.join(tempfile.gettempdir(), f"{app_name}.pid")
        for i in range(3):
            try:
                f = open(p_f, "a+")
                f.seek(0)
                if sys.platform == "win32":
                    import msvcrt
                    msvcrt.locking(f.fileno(), msvcrt.LK_NBLCK, 1)
                else:
                    import fcntl
                    fcntl.lockf(f, fcntl.LOCK_EX | fcntl.LOCK_NB)
                f.seek(0); f.truncate(); f.write(str(os.getpid())); f.flush()
                return True, f
            except:
                if i < 2: time.sleep(0.2)
                continue
        return False, None

class FlushHandler(logging.Handler):
    def __init__(self, handler):
        super().__init__()
        self.handler = handler

    def emit(self, record):
        self.handler.emit(record)
        self.handler.flush()

class UIManager:
    @staticmethod
    def style_and_size_msg_box(msg_box: 'QMessageBox', copy_text: str, copy_btn_label: str = "Copy to Clipboard"):
        from PyQt5.QtWidgets import QSpacerItem, QSizePolicy, QGridLayout, QApplication
        from PyQt5.QtCore import Qt, QTimer, QUrl
        from PyQt5.QtGui import QDesktopServices
        l = msg_box.layout()
        if isinstance(l, QGridLayout):
            l.addItem(QSpacerItem(800, 500, QSizePolicy.Minimum, QSizePolicy.Expanding), l.rowCount(), 0, 1, l.columnCount())
        c_b = msg_box.addButton(copy_btn_label, msg_box.ActionRole)
        try:
            from app import tr
            r_l = tr("report_to_alon")
        except: r_l = "Report Error"
        r_b = msg_box.addButton(r_l, msg_box.ActionRole)

        def on_copy():
            if sip and sip.isdeleted(c_b): return
            QApplication.clipboard().setText(copy_text)
            c_b.setText("✓ Copied!")

            def reset_text():
                if sip and not sip.isdeleted(c_b): c_b.setText(copy_btn_label)
            QTimer.singleShot(2000, reset_text)

        def on_report():
            sub = "[Fortnite Video Software] Error Report"
            body = f"The following error occurred:\n\n{copy_text}"

            import urllib.parse
            m_u = f"mailto:alon.reich@gmail.com?subject={urllib.parse.quote(sub)}&body={urllib.parse.quote(body)}"
            QDesktopServices.openUrl(QUrl(m_u))
            msg_box.close()
        c_b.clicked.connect(on_copy); r_b.clicked.connect(on_report)
        for b in msg_box.buttons(): b.setCursor(Qt.PointingHandCursor)

class ConsoleManager:
    _log_files = []
    @staticmethod
    def _source_tag(name: str) -> str:
        m = {"Main_App": "main_app", "Crop_Tool": "crop_tools", "Advanced_Editor": "advanced_editor"}
        return m.get(str(name), str(name).strip().lower())
    @staticmethod
    def initialize(base_dir: str, log_filename: str, logger_name: str):
        app_p = logger_name.lower().replace(" ", "_")
        f_l_n = f"{app_p}_{log_filename}" if not log_filename.startswith(app_p) else log_filename
        logger = LogManager.setup_logger(base_dir, f_l_n, logger_name)
        try:
            l_d = os.path.join(base_dir, "logs")
            os.makedirs(l_d, exist_ok=True)
            s_t = ConsoleManager._source_tag(logger_name)
            r_l_p = os.path.join(l_d, f"mpv_{s_t}.raw.log")
            mpv.log_path = os.path.join(l_d, f"{app_p}_mpv.log")
            f = open(r_l_p, 'a', encoding='utf-8', buffering=1)
            ConsoleManager._log_files.append(f)
            try:
                import faulthandler
                os.dup2(f.fileno(), sys.stdout.fileno())
                os.dup2(f.fileno(), sys.stderr.fileno())
                faulthandler.enable(f)
            except: pass
            if hasattr(sys.stdout, 'reconfigure'):
                sys.stdout.reconfigure(line_buffering=True)
                sys.stderr.reconfigure(line_buffering=True)
            logger.info("NATIVE DEBUG LOGGING ACTIVE - REALTIME MODE")
        except Exception as e:
            logger.error(f"Failed to setup native logging: {e}")

        def exc_h(t, v, tb):
            if issubclass(t, KeyboardInterrupt):
                sys.__excepthook__(t, v, tb)
                return
            msg = "".join(traceback.format_exception(t, v, tb))
            logger.critical(f"UNCAUGHT EXCEPTION:\n{msg}")
            try:
                from PyQt5.QtWidgets import QMessageBox, QApplication
                if QApplication.instance():
                    m_b = QMessageBox(None)
                    m_b.setIcon(QMessageBox.Critical); m_b.setWindowTitle("Critical Error")
                    m_b.setText(f"An unexpected error occurred.\n\n{v}\n\nDetails saved to log.")
                    UIManager.style_and_size_msg_box(m_b, msg)
                    m_b.exec_(); QApplication.instance().quit()
                else: sys.exit(1)
            except: sys.exit(1)
        sys.excepthook = exc_h
        if sys.platform == "win32":
            try:
                import ctypes
                hwnd = ctypes.windll.kernel32.GetConsoleWindow()
                if hwnd != 0: ctypes.windll.user32.ShowWindow(hwnd, 0)
            except: pass

        import atexit

        def close_logs():
            for f in ConsoleManager._log_files:
                try: f.flush(); f.close()
                except: pass
        atexit.register(close_logs)
        return logger

class LogManager:
    @staticmethod
    def setup_logger(base_dir: str, log_filename: str, logger_name: str) -> logging.Logger:
        logger = logging.getLogger(logger_name)
        if logger.handlers: return logger
        logger.setLevel(logging.INFO)
        l_d = os.path.join(base_dir, "logs")
        os.makedirs(l_d, exist_ok=True)
        l_p = os.path.join(l_d, log_filename)
        h = RotatingFileHandler(l_p, maxBytes=5*1024*1024, backupCount=3, encoding='utf-8')
        h.setFormatter(logging.Formatter('%(asctime)s - %(name)s - %(levelname)s - %(message)s'))
        logger.addHandler(h)
        c = logging.StreamHandler(sys.stdout)
        c.setFormatter(logging.Formatter('%(asctime)s - %(name)s - %(levelname)s - %(message)s'))
        logger.addHandler(c)

        def force_flush_emit(record):
            try:
                h.emit(record)
                h.flush()
                if hasattr(h.stream, 'flush'): h.stream.flush()
                c.emit(record)
                c.flush()
                if sys.stdout: sys.stdout.flush()
            except: pass
        logger.handle = force_flush_emit
        return logger

class MPVSafetyManager:
    _mpv_creation_lock = threading.Lock()
    _instances = weakref.WeakSet()
    @staticmethod
    def log_mpv_diagnostics(player, logger, context_tag="GENERAL"):
        if not player or not logger: return
        try:
            p_id = id(player); h = getattr(player, 'handle', 'UNKNOWN'); s = getattr(player, '_core_shutdown', False)
            try:
                p = getattr(player, 'path', 'NONE'); pz = getattr(player, 'pause', 'UNKNOWN'); t = getattr(player, 'time_pos', 'UNKNOWN')
            except: p, pz, t = "UNREADABLE", "UNREADABLE", "UNREADABLE"
            logger.info(f"DIAGNOSTICS [{context_tag}]: MPV Object ID={p_id} | Handle={h} | CoreShutdown={s} | Path={p} | Paused={pz} | Time={t}")
        except Exception as e: logger.error(f"DIAGNOSTICS [{context_tag}]: Failed to extract MPV state: {e}")
    @staticmethod
    def safe_mpv_shutdown(player, timeout=2.0, lock=None):
        if not player: return True

        import mpv
        try:
            if getattr(player, '_core_shutdown', False) or getattr(player, '_safe_shutdown_initiated', False): return True
            player._safe_shutdown_initiated = True

            def _do_shutdown():
                try:
                    if lock:
                        if lock.acquire(timeout=0.2):
                            try: player.pause = True; player.stop(); player.terminate()
                            finally: lock.release()
                        else: player.pause = True; player.terminate()
                    else: player.pause = True; player.stop(); player.terminate()
                except: pass
            t = threading.Thread(target=_do_shutdown, daemon=True)
            t.start()
            c_t = min(0.5, timeout); s_t = time.time()
            while time.time() - s_t < c_t:
                try:
                    if getattr(player, '_core_shutdown', True): break
                except: break
                time.sleep(0.05)
            MPVSafetyManager.cleanup_mpv_event_callbacks(player)
            return True
        except: return False
    @staticmethod
    def safe_mpv_set(player, property_name, value, max_attempts=3, lock=None):
        if not player or getattr(player, '_core_shutdown', False): return False
        for i in range(max_attempts):
            try:
                if lock:
                    if not lock.acquire(timeout=0.1): continue
                try: setattr(player, property_name, value); return True
                finally:
                    if lock: lock.release()
            except: time.sleep(0.02)
        return False
    @staticmethod
    def safe_mpv_get(player, property_name, default=None, max_attempts=3, lock=None):
        if not player or getattr(player, '_core_shutdown', False): return default
        for i in range(max_attempts):
            try:
                if lock:
                    if not lock.acquire(timeout=0.1): continue
                try: return getattr(player, property_name, default)
                finally:
                    if lock: lock.release()
            except: time.sleep(0.02)
        return default
    @staticmethod
    def safe_mpv_command(player, command, *args, max_attempts=3, lock=None):
        if not player or getattr(player, '_core_shutdown', False): return False
        for i in range(max_attempts):
            try:
                if lock:
                    if not lock.acquire(timeout=0.1): continue
                try: player.command(command, *args); return True
                finally:
                    if lock: lock.release()
            except: time.sleep(0.02)
        return False
    @staticmethod
    def cleanup_mpv_event_callbacks(player):
        if not player: return
        try:
            if hasattr(player, '_event_callbacks'): player._event_callbacks.clear()
            if hasattr(player, '_property_observers'): player._property_observers.clear()
            if hasattr(player, 'event_callback'): player.event_callback = None
        except: pass
    @staticmethod
    def create_safe_mpv(**kwargs):
        with MPVSafetyManager._mpv_creation_lock:
            QThread.msleep(200)
            try:
                import mpv
                l_h = kwargs.pop('log_handler', None)
                if l_h:
                    def s_l_p(lvl, pref, txt):
                        try: l_h(lvl, pref, txt)
                        except: pass
                    kwargs['log_handler'] = s_l_p
                wid = kwargs.pop('wid', None)
                s_k = {'hr_seek': 'yes', 'osc': False, 'ytdl': False, 'load_scripts': False, 'config': False, 'keep_open': kwargs.get('keep_open', 'yes'), 'loglevel': 'error'}
                e_f = kwargs.pop('extra_mpv_flags', [])
                kwargs.pop('load_scripts', None); kwargs.pop('config', None); kwargs.pop('osc', None); kwargs.pop('ytdl', None)
                s_k.update(kwargs); player = mpv.MPV(**s_k)
                if wid is not None:
                    try:
                        wid_int = int(wid)
                        if wid_int > 0: player.wid = wid_int
                    except: pass
                time.sleep(0.1); player._safe_shutdown_initiated = False; MPVSafetyManager._instances.add(player)
                for p, v in e_f:
                    try: player.set_property(p, v)
                    except: pass
                return player
            except: return None
    @staticmethod
    def register_global_shutdown_handler():
        import atexit

        def global_shutdown():
            for p in list(MPVSafetyManager._instances):
                try:
                    if hasattr(p, '_safe_shutdown_initiated') and not p._safe_shutdown_initiated:
                        p._safe_shutdown_initiated = True; MPVSafetyManager.safe_mpv_shutdown(p, timeout=1.0)
                except: pass
        atexit.register(global_shutdown)

class MediaProber:
    @staticmethod
    def probe_duration(bin_dir, path):
        try:
            ffp = os.path.join(bin_dir, 'ffprobe.exe') if sys.platform == 'win32' else 'ffprobe'
            cmd = [ffp, "-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", path]
            r = subprocess.run(cmd, text=True, capture_output=True, creationflags=0x08000000 if sys.platform == 'win32' else 0)
            return max(0.0, float(r.stdout.strip() or 0.0))
        except: return 0.0
    @staticmethod
    def probe_metadata(bin_dir, path):
        try:
            ffp = os.path.join(bin_dir, 'ffprobe.exe') if sys.platform == 'win32' else 'ffprobe'
            cmd = [ffp, "-v", "error", "-select_streams", "v:0", "-show_entries", "stream=duration,width,height", "-of", "json", path]
            r = subprocess.run(cmd, text=True, capture_output=True, creationflags=0x08000000 if sys.platform == 'win32' else 0)
            data = json.loads(r.stdout)
            stream = data.get('streams', [{}])[0]
            dur = float(stream.get('duration', 0.0) or 0.0)
            res = f"{stream.get('width', 0)}x{stream.get('height', 0)}"
            return dur, res
        except: return 0.0, "0x0"
