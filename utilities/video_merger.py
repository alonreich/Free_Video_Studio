import sys
import os
sys.dont_write_bytecode = True
os.environ['PYTHONDONTWRITEBYTECODE'] = '1'

from PyQt5.QtWidgets import QApplication, QMessageBox
from PyQt5.QtGui import QIcon
project_root = os.path.abspath(os.path.join(os.path.dirname(__file__), '..'))
if project_root not in sys.path:
    sys.path.insert(0, project_root)

from utilities.merger_system import MergerConsoleManager, MergerProcessManager as ProcessManager, MergerDependencyDoctor as DependencyDoctor
logger = MergerConsoleManager.initialize(project_root, "video_merger.log", "Video_Merger")

import traceback
import faulthandler
import logging
import subprocess
import ctypes
import shutil

def main():
    SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
    BASE_DIR = os.path.abspath(os.path.join(SCRIPT_DIR, '..'))
    ProcessManager.kill_orphans()
    ProcessManager.cleanup_temp_files(min_age_seconds=300)
    if sys.platform.startswith("win") and os.environ.get("FVS_DEBUG_CONSOLE", "0") == "1":
        try:
            kernel32 = ctypes.windll.kernel32
            if kernel32.GetConsoleWindow() == 0:
                kernel32.AllocConsole()
                sys.stdout = open('CONOUT$', 'w')
                sys.stderr = open('CONOUT$', 'w')
            myappid = 'FortniteVideoTool.VideoMerger.1.0'
            ctypes.windll.shell32.SetCurrentProcessExplicitAppUserModelID(myappid)
        except Exception:
            pass
    app = QApplication(sys.argv)
    app.setStyle('Fusion')
    logger.info("=== Video Merger Started ===")
    success, pid_handle = ProcessManager.acquire_pid_lock("fortnite_video_merger")
    if not success:
        QMessageBox.information(None, "Already Running", "Video Merger is already running.")
        sys.exit(0)
    is_valid_deps, ffmpeg_path, dep_error = DependencyDoctor.check_ffmpeg(BASE_DIR)
    if not is_valid_deps:
        QMessageBox.critical(None, "Dependency Error", f"FFmpeg is missing: {dep_error}\nPlease run the Main App to diagnose.")
        sys.exit(1)

    from utilities.merger_config import MergerConfigManager as ConfigManager
    from utilities.merger_window import VideoMergerWindow
    config_path = os.path.join(BASE_DIR, 'config', 'video_merger.conf')
    config_manager = ConfigManager(config_path)
    bin_dir = os.path.join(BASE_DIR, 'binaries')
    try:
        window = VideoMergerWindow(
            ffmpeg_path=ffmpeg_path,
            parent=None,
            vlc_instance=None,
            bin_dir=bin_dir,
            config_manager=config_manager,
            base_dir=BASE_DIR
        )
        window.show()
        window.activateWindow()
        window.raise_()

        def restart_main_app():
            main_app_path = os.path.join(BASE_DIR, 'app.py')
            subprocess.Popen([sys.executable, main_app_path], cwd=BASE_DIR)
            window.close()
        if hasattr(window, 'return_to_main'):
            window.return_to_main.connect(restart_main_app)
        exit_code = app.exec_()
        window.close()
        if pid_handle: pid_handle.close()
        sys.exit(exit_code)
    except Exception as e:
        logger.critical(f"Unhandled exception in main loop: {e}", exc_info=True)
        QMessageBox.critical(None, "Crash", f"An unexpected error occurred:\n{e}")
        if pid_handle: pid_handle.close()
        sys.exit(1)
if __name__ == "__main__":
    main()
