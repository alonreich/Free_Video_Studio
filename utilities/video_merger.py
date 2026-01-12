import sys
import os
import ctypes
from pathlib import Path
SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
BASE_DIR = os.path.abspath(os.path.join(SCRIPT_DIR, '..'))
if BASE_DIR not in sys.path:
    sys.path.insert(0, BASE_DIR)

from PyQt5.QtWidgets import QApplication, QMessageBox
from PyQt5.QtCore import Qt
from PyQt5.QtGui import QIcon
try:
    from system.logger import setup_logger
    from system.config import ConfigManager
    from utilities.merger_window import VideoMergerWindow
except ImportError as e:
    app = QApplication(sys.argv)
    QMessageBox.critical(None, "Import Error", 
        f"Could not import project modules.\n\nError: {e}\n\n"
        "Ensure you are running this from the project root or via the Main App.")
    sys.exit(1)

def main():
    if sys.platform.startswith("win"):
        try:
            myappid = 'FortniteVideoTool.VideoMerger.1.0'
            ctypes.windll.shell32.SetCurrentProcessExplicitAppUserModelID(myappid)
        except Exception:
            pass
    if hasattr(Qt, 'AA_EnableHighDpiScaling'):
        QApplication.setAttribute(Qt.AA_EnableHighDpiScaling, True)
    if hasattr(Qt, 'AA_UseHighDpiPixmaps'):
        QApplication.setAttribute(Qt.AA_UseHighDpiPixmaps, True)
    app = QApplication(sys.argv)
    app.setStyle('Fusion')
    logger = setup_logger(BASE_DIR, "Video_Merger.log", "Video_Merger")
    logger.info("=== Video Merger Started ===")
    logger.info(f"Root Directory: {BASE_DIR}")
    config_path = os.path.join(BASE_DIR, 'config', 'main_app.conf')
    config_manager = ConfigManager(config_path)
    bin_dir = os.path.join(BASE_DIR, 'binaries')
    ffmpeg_exe = "ffmpeg.exe" if sys.platform == "win32" else "ffmpeg"
    ffmpeg_path = os.path.join(bin_dir, ffmpeg_exe)
    if not os.path.exists(ffmpeg_path):
        import shutil
        if shutil.which(ffmpeg_exe):
            ffmpeg_path = ffmpeg_exe
        else:
            logger.critical("FFmpeg not found.")
            QMessageBox.critical(None, "Dependency Error", 
                f"FFmpeg not found at:\n{ffmpeg_path}\n\nPlease check your 'binaries' folder.")
            sys.exit(1)
    vlc_instance = None
    try:
        import vlc
        plugin_path = os.path.join(bin_dir, 'plugins')
        args = ['--no-xlib', '--quiet']
        if os.path.exists(plugin_path):
            args.append(f'--plugin-path={plugin_path}')
        vlc_instance = vlc.Instance(args)
        logger.info("VLC Instance created successfully.")
    except Exception as e:
        logger.warning(f"VLC initialization failed: {e}. Preview might not work.")
    icon_path = os.path.join(BASE_DIR, "icons", "Video_Icon_File.ico")
    if os.path.exists(icon_path):
        app.setWindowIcon(QIcon(icon_path))
    try:
        window = VideoMergerWindow(
            ffmpeg_path=ffmpeg_path,
            parent=None,
            vlc_instance=vlc_instance,
            bin_dir=bin_dir,
            config_manager=config_manager,
            base_dir=BASE_DIR
        )
        geo = config_manager.config.get('merger_window_geometry')
        if geo and isinstance(geo, dict):
            try:
                window.setGeometry(
                    geo.get('x', 100), geo.get('y', 100),
                    geo.get('w', 900), geo.get('h', 600)
                )
            except Exception:
                pass
        else:
            window.resize(900, 600)
        window.show()

        def restart_main_app():
            logger.info("Returning to Main App...")
            main_app_path = os.path.join(BASE_DIR, 'app.py')
            subprocess.Popen([sys.executable, main_app_path], cwd=BASE_DIR)
            window.close()
        if hasattr(window, 'return_to_main'):
            import subprocess
            window.return_to_main.connect(restart_main_app)
        sys.exit(app.exec_())
    except Exception as e:
        logger.critical(f"Unhandled exception in Merger Main Loop: {e}", exc_info=True)
        QMessageBox.critical(None, "Crash", f"An unexpected error occurred:\n{e}")
        sys.exit(1)
if __name__ == "__main__":
    main()