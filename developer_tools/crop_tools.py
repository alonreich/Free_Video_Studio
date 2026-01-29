import sys
import os
import traceback
os.environ['PYTHONDONTWRITEBYTECODE'] = '1'
os.environ['PYTHONPYCACHEPREFIX'] = os.devnull
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
sys.dont_write_bytecode = True

import ctypes
import tempfile
import subprocess
import psutil
from PyQt5.QtWidgets import QApplication, QWidget
from PyQt5.QtCore import Qt, QTimer
from utils import PersistentWindowMixin
from Keyboard_Mixing import KeyboardShortcutMixin
from media_processor import MediaProcessor
from ui_setup import Ui_CropApp
from app_handlers import CropAppHandlers
from config import CROP_APP_STYLESHEET
from logger_setup import setup_logger
from enhanced_logger import get_enhanced_logger
from config_manager import get_config_manager
import time
try:
    import win32gui
    import win32process
    import win32con
    HAS_WIN32 = True
except ImportError:
    HAS_WIN32 = False
PID_FILE_NAME = "fortnite_video_software_app.pid"
PID_FILE_PATH = os.path.join(tempfile.gettempdir(), PID_FILE_NAME)

class CropApp(KeyboardShortcutMixin, PersistentWindowMixin, QWidget, CropAppHandlers):
    def __init__(self, logger_instance, file_path=None):
        super().__init__()
        self.logger = logger_instance
        self.enhanced_logger = get_enhanced_logger(self.logger)
        self.base_title = "Crop Tool - Wizard Mode"
        self.script_dir = os.path.dirname(os.path.abspath(__file__))
        self.base_dir = os.path.abspath(os.path.join(self.script_dir, '..'))
        self.config_path = os.path.join(self.base_dir, 'processing', 'crops_coordinations.conf')
        self.config_manager = get_config_manager(self.config_path, self.logger)
        self.last_dir = None
        self.bin_dir = os.path.abspath(os.path.join(self.base_dir, 'binaries'))
        self.snapshot_path = os.path.join(tempfile.gettempdir(), "snapshot.png")
        self.media_processor = MediaProcessor(self.bin_dir)
        self.portrait_window = None
        self.background_crop_width = 0
        self.arrow_key_press_counter = 0
        self.ui = Ui_CropApp()
        self.ui.setupUi(self)
        self.connect_signals()
        self.timer = QTimer(self)
        self.timer.setInterval(100)
        self.timer.timeout.connect(self.update_ui)
        self.timer.start()
        if hasattr(self, 'set_style'):
            self.set_style()
        else:
            self.setStyleSheet(CROP_APP_STYLESHEET)
        self.setFocusPolicy(Qt.StrongFocus)
        self.setFocus()
        self.setup_persistence(
            config_path=self.config_path,
            settings_key='window_geometry',
            default_geo={'x': 100, 'y': 100, 'w': 1280, 'h': 720},
            title_info_provider=self.get_title_info
        )
        if file_path and os.path.exists(file_path):
            self.load_file(file_path)

    def _deferred_launch_main_app(self):
        self.logger.info("F12 pressed in Crop Tool. Attempting to switch to Main App.")
        main_app_found = False
        main_app_path = os.path.normcase(os.path.join(self.base_dir, 'app.py'))
        if os.path.exists(PID_FILE_PATH):
            try:
                with open(PID_FILE_PATH, "r") as f:
                    pid = int(f.read().strip())
                if psutil.pid_exists(pid):
                    proc = psutil.Process(pid)
                    cmdline = [os.path.normcase(arg) for arg in proc.cmdline()]
                    if any(main_app_path in arg for arg in cmdline):
                        self.logger.info(f"Found running main app via PID file with PID: {pid}. Bringing to front.")
                        if HAS_WIN32:
                            self._bring_app_to_foreground(pid)
                        main_app_found = True
                    else:
                        self.logger.warning(f"A process with PID {pid} exists, but it's not the main app. Removing stale PID file.")
                        try: os.remove(PID_FILE_PATH)
                        except OSError: pass
                else:
                    self.logger.info(f"PID from file does not exist. Removing stale PID file.")
                    try: os.remove(PID_FILE_PATH)
                    except OSError: pass
            except (ValueError, FileNotFoundError, psutil.NoSuchProcess, psutil.AccessDenied, psutil.ZombieProcess) as e:
                self.logger.warning(f"Error reading or verifying PID file: {e}. Assuming app is not running.")
        else:
            self.logger.info("Main app PID file not found.")
        if not main_app_found:
            self.logger.info("Main app not found or PID was stale. Launching it now.")
            try:
                executable = sys.executable.replace("python.exe", "pythonw.exe")
                if not os.path.exists(executable):
                    executable = sys.executable
                command = [executable, main_app_path]
                flags = subprocess.DETACHED_PROCESS | subprocess.CREATE_NEW_PROCESS_GROUP
                subprocess.Popen(command, creationflags=flags, cwd=self.base_dir)
                self.logger.info(f"Launched app.py: {main_app_path}")
            except Exception as e:
                self.logger.error(f"Failed to launch app.py: {e}")
        self.logger.info("Quitting Crop Tool application.")
        QApplication.instance().quit()

    def keyPressEvent(self, event):
        key = event.key()
        if key == Qt.Key_F12:
            self.timer.singleShot(0, self._deferred_launch_main_app)
            event.accept()
            return
        super().keyPressEvent(event)

    def _bring_app_to_foreground(self, pid):
        if not HAS_WIN32:
            self.logger.warning("HAS_WIN32 is False, _bring_app_to_foreground cannot function.")
            return

        def foreach_window(hwnd, ctx):
            if win32gui.IsWindowVisible(hwnd) and win32gui.GetWindowText(hwnd) != '':
                try:
                    _, found_pid = win32process.GetWindowThreadProcessId(hwnd)
                    if found_pid == pid:
                        window_title = win32gui.GetWindowText(hwnd)
                        self.logger.info(f"Found target window for PID {pid}: '{window_title}'. Bringing to front.")
                        try:
                            win32gui.SetForegroundWindow(hwnd)
                            win32gui.ShowWindow(hwnd, win32con.SW_RESTORE)
                            return None
                        except Exception as e:
                            self.logger.error(f"Error setting foreground/showing window for HWND {hwnd}, PID {pid}: {e}")
                except Exception as e:
                    pass
            return 1
        try:
            win32gui.EnumWindows(foreach_window, None)
        except Exception as e:
            self.logger.error(f"Error during EnumWindows for PID {pid}: {e}")

    def closeEvent(self, event):
        if self.portrait_window:
            self.portrait_window.close()
        super().closeEvent(event)

def main():
    logger = setup_logger()
    logger.info("Application starting...")
    BASE_DIR = os.path.abspath(os.path.join(os.path.dirname(__file__), '..'))
    BIN_DIR = os.path.join(BASE_DIR, 'binaries')
    PLUGINS_DIR = os.path.join(BIN_DIR, 'plugins')
    os.environ['VLC_PLUGIN_PATH'] = PLUGINS_DIR
    if hasattr(os, 'add_dll_directory'):
        if os.path.isdir(BIN_DIR): os.add_dll_directory(BIN_DIR)
        if os.path.isdir(PLUGINS_DIR): os.add_dll_directory(PLUGINS_DIR)
    os.environ['PATH'] = f"{BIN_DIR}{os.pathsep}{PLUGINS_DIR}{os.pathsep}{os.environ.get('PATH', '')}"
    try:
        ctypes.WinDLL(os.path.join(BIN_DIR, 'libvlccore.dll'))
        ctypes.WinDLL(os.path.join(BIN_DIR, 'libvlc.dll'))
    except Exception as e:
        print(f"Error loading VLC DLLs: {e}")
    app = QApplication(sys.argv)
    app.setStyle('Fusion')
    file_path = sys.argv[1] if len(sys.argv) > 1 else None
    player = CropApp(logger, file_path=file_path)
    player.show()
    sys.exit(app.exec_())
if __name__ == '__main__':
    main()
