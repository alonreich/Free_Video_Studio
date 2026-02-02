import sys
import os
import traceback
import faulthandler
import logging

# Enforce no-cache policy (User Requirement: "never allow")
sys.dont_write_bytecode = True
os.environ['PYTHONDONTWRITEBYTECODE'] = '1'

# Cleanup #19: Top-level imports
from PyQt5.QtWidgets import QApplication, QMessageBox
from PyQt5.QtGui import QIcon
import subprocess
import ctypes
import shutil

# Local imports handled safely below
def setup_environment(base_dir):
    """Setup logging and paths."""
    if base_dir not in sys.path:
        sys.path.insert(0, base_dir)
    
    # Setup Logger
    logger = None
    try:
        from system.logger import setup_logger
        logger = setup_logger(base_dir, "Video_Merger.log", "Video_Merger")
        logger.info("=== Early logger setup complete ===")
    except Exception as e:
        # Fallback logger
        log_dir = os.path.join(base_dir, "logs")
        os.makedirs(log_dir, exist_ok=True)
        logging.basicConfig(
            filename=os.path.join(log_dir, "Video_Merger_fallback.log"),
            level=logging.INFO,
            format='%(asctime)s - %(name)s - %(levelname)s - %(message)s'
        )
        logger = logging.getLogger("Video_Merger")
        logger.error(f"Primary logger setup failed: {e}")
        
    # Crash Trace
    try:
        crash_log = open(os.path.join(base_dir, "hard_crash_trace.txt"), "w", encoding="utf-8")
        faulthandler.enable(file=crash_log, all_threads=True)
    except Exception:
        faulthandler.enable()
        
    return logger

def global_exception_handler(exc_type, exc_value, exc_traceback):
    """Global exception handler (Fix #13)."""
    error_msg = "".join(traceback.format_exception(exc_type, exc_value, exc_traceback))
    print(error_msg, file=sys.stderr)
    logging.getLogger("Video_Merger").critical(f"Unhandled Exception:\n{error_msg}")
    
    # Show Dialog if QApplication exists
    if QApplication.instance():
        QMessageBox.critical(None, "Critical Error", 
            f"An unexpected error occurred:\n{exc_value}\n\nSee log for details.")

def main():
    # Environment Setup
    SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
    BASE_DIR = os.path.abspath(os.path.join(SCRIPT_DIR, '..'))
    
    logger = setup_environment(BASE_DIR)
    sys.excepthook = global_exception_handler

    # Windows Console Fix
    if sys.platform.startswith("win"):
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

    # Dependency Check
    try:
        from system.config import ConfigManager
        from utilities.merger_window import VideoMergerWindow
    except ImportError as e:
        QMessageBox.critical(None, "Import Error", f"Could not import project modules.\n{e}")
        sys.exit(1)

    config_path = os.path.join(BASE_DIR, 'config', 'main_app.conf')
    config_manager = ConfigManager(config_path)
    bin_dir = os.path.join(BASE_DIR, 'binaries')
    
    ffmpeg_exe = "ffmpeg.exe" if sys.platform == "win32" else "ffmpeg"
    ffmpeg_path = os.path.join(bin_dir, ffmpeg_exe)
    
    if not os.path.exists(ffmpeg_path):
        if shutil.which(ffmpeg_exe):
            ffmpeg_path = ffmpeg_exe
        else:
            logger.critical("FFmpeg not found.")
            QMessageBox.critical(None, "Dependency Error", "FFmpeg not found. Please install FFmpeg or check 'binaries' folder.")
            sys.exit(1)

    try:
        window = VideoMergerWindow(
            ffmpeg_path=ffmpeg_path,
            parent=None,
            vlc_instance=None,
            bin_dir=bin_dir,
            config_manager=config_manager,
            base_dir=BASE_DIR
        )
        
        # Geometry Restore
        geo = config_manager.config.get('merger_window_geometry')
        if geo and isinstance(geo, dict):
            try:
                screen = QApplication.primaryScreen().availableGeometry()
                w = min(geo.get('w', 1000), screen.width())
                h = min(geo.get('h', 700), screen.height())
                x = max(screen.x(), min(geo.get('x', 100), screen.right() - w))
                y = max(screen.y(), min(geo.get('y', 100), screen.bottom() - h))
                window.setGeometry(x, y, w, h)
            except Exception:
                pass
                
        window.show()
        window.activateWindow()
        window.raise_()

        def restart_main_app():
            logger.info("Returning to Main App...")
            main_app_path = os.path.join(BASE_DIR, 'app.py')
            # Fix #11: Clean exit before spawn
            subprocess.Popen([sys.executable, main_app_path], cwd=BASE_DIR)
            window.close()
            
        if hasattr(window, 'return_to_main'):
            window.return_to_main.connect(restart_main_app)
            
        sys.exit(app.exec_())
        
    except Exception as e:
        logger.critical(f"Unhandled exception in main loop: {e}", exc_info=True)
        QMessageBox.critical(None, "Crash", f"An unexpected error occurred:\n{e}")
        sys.exit(1)

if __name__ == "__main__":
    main()