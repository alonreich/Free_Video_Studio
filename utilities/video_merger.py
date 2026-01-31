import sys
import os
sys.dont_write_bytecode = True
os.environ['PYTHONDONTWRITEBYTECODE'] = '1'
if __name__ == "__main__":
    SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
    BASE_DIR = os.path.abspath(os.path.join(SCRIPT_DIR, '..'))
    if BASE_DIR not in sys.path:
        sys.path.insert(0, BASE_DIR)

from PyQt5.QtWidgets import QApplication, QMessageBox
from PyQt5.QtGui import QIcon
import subprocess
try:
    from system.logger import setup_logger
    from system.config import ConfigManager
    from utilities.merger_window import VideoMergerWindow
except ImportError as e:
    if __name__ == "__main__":
        app = QApplication(sys.argv)
        QMessageBox.critical(None, "Import Error", 
            f"Could not import project modules.\n\nError: {e}\n\n"
            "Please run the application from the project root.")
        sys.exit(1)
    else:
        raise

def main():
    if sys.platform.startswith("win"):
        try:
            import ctypes
            myappid = 'FortniteVideoTool.VideoMerger.1.0'
            ctypes.windll.shell32.SetCurrentProcessExplicitAppUserModelID(myappid)
        except Exception:
            pass
    app = QApplication(sys.argv)
    app.setStyle('Fusion')
    base_dir = os.path.abspath(os.path.join(os.path.dirname(__file__), '..'))
    try:
        logger = setup_logger(base_dir, "Video_Merger.log", "Video_Merger")
    except Exception:
        import logging
        logger = logging.getLogger("Video_Merger")
    logger.info("=== Video Merger Started ===")
    config_path = os.path.join(base_dir, 'config', 'main_app.conf')
    config_manager = ConfigManager(config_path)
    bin_dir = os.path.join(base_dir, 'binaries')
    ffmpeg_exe = "ffmpeg.exe" if sys.platform == "win32" else "ffmpeg"
    ffmpeg_path = os.path.join(bin_dir, ffmpeg_exe)
    if not os.path.exists(ffmpeg_path):
        import shutil
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
            base_dir=base_dir
        )
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

        def restart_main_app():
            logger.info("Returning to Main App...")
            main_app_path = os.path.join(base_dir, 'app.py')
            subprocess.Popen([sys.executable, main_app_path], cwd=base_dir)
            window.close()
        if hasattr(window, 'return_to_main'):
            window.return_to_main.connect(restart_main_app)
        sys.exit(app.exec_())
    except Exception as e:
        logger.critical(f"Unhandled exception: {e}", exc_info=True)
        QMessageBox.critical(None, "Crash", f"An unexpected error occurred:\n{e}")
        sys.exit(1)
if __name__ == "__main__":
    main()