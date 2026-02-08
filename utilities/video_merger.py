import sys
import os
import traceback
import faulthandler
import logging
sys.dont_write_bytecode = True
os.environ['PYTHONDONTWRITEBYTECODE'] = '1'

from PyQt5.QtWidgets import QApplication, QMessageBox
from PyQt5.QtGui import QIcon
import subprocess
import ctypes
import shutil
project_root = os.path.abspath(os.path.join(os.path.dirname(__file__), '..'))
if project_root not in sys.path:
    sys.path.insert(0, project_root)

from system.utils import ProcessManager, LogManager, DependencyDoctor
from system.state_transfer import StateTransfer

def setup_environment(base_dir):
    """Setup logging and paths using centralized utils."""
    try:
        logger = LogManager.setup_logger(base_dir, "Video_Merger.log", "Video_Merger")
        logger.info("=== Video Merger Environment Setup Complete ===")
        return logger
    except Exception as e:
        logging.basicConfig(level=logging.INFO)
        logger = logging.getLogger("Video_Merger")
        logger.error(f"Central logger setup failed: {e}")
        return logger

def global_exception_handler(exc_type, exc_value, exc_traceback):
    """Global exception handler (Fix #13, #2)."""
    error_msg = "".join(traceback.format_exception(exc_type, exc_value, exc_traceback))
    print(error_msg, file=sys.stderr)
    logging.getLogger("Video_Merger").critical(f"Unhandled Exception:\n{error_msg}")
    if QApplication.instance():
        error_summary = str(exc_value)
        if isinstance(exc_value, ImportError):
            error_summary = f"Missing module: {error_summary}"
        elif isinstance(exc_value, FileNotFoundError):
            error_summary = f"File not found: {error_summary}"
        elif isinstance(exc_value, PermissionError):
            error_summary = f"Permission denied: {error_summary}"
        QMessageBox.critical(
            None,
            "Critical Error",
            f"Something went wrong unexpectedly.\n\n"
            f"Error: {error_summary}\n\n"
            "Please restart the app. If the problem persists:\n"
            "1. Check log file for details\n"
            "2. Report bug using /reportbug command\n"
            "3. Try different input files\n\n"
            "Technical details were saved to the log file.",
        )

def main():
    SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
    BASE_DIR = os.path.abspath(os.path.join(SCRIPT_DIR, '..'))
    ProcessManager.kill_orphans()
    ProcessManager.cleanup_temp_files(prefix="fvs_merger_")
    logger = setup_environment(BASE_DIR)
    sys.excepthook = global_exception_handler
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

    from system.config import ConfigManager
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
        session_data = StateTransfer.load_state()

        def restart_main_app():
            logger.info("Returning to Main App...")
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