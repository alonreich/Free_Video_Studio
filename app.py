

import os, sys, tempfile, psutil
os.environ['PYTHONDONTWRITEBYTECODE'] = '1'
os.environ['PYTHONPYCACHEPREFIX'] = ''
sys.dont_write_bytecode = True
try:
    import audioop
except ImportError:
    try:
        import audioop_lts as audioop_shim
        sys.modules['audioop'] = audioop_shim
    except ImportError:
        pass
from PyQt5.QtWidgets import QApplication, QMessageBox, QWidget
from PyQt5.QtCore import QCoreApplication, QObject, QThread, pyqtSignal
from PyQt5.QtGui import QIcon
import subprocess, ctypes

BASE_DIR  = os.path.dirname(os.path.abspath(__file__))
BIN_DIR   = os.path.join(BASE_DIR, 'binaries')
PLUGINS   = os.path.join(BIN_DIR, 'plugins')
PID_FILE_NAME = "fortnite_video_software_app.pid"
PID_FILE_PATH = os.path.join(tempfile.gettempdir(), PID_FILE_NAME)

class HardwareWorker(QObject):
    """
    Worker thread to offload slow hardware capability checks from the main UI thread.
    """
    finished = pyqtSignal(str)

    def __init__(self, ffmpeg_path):
        super().__init__()
        self.ffmpeg_path = ffmpeg_path

    def run(self):
        """Performs the hardware scan and emits the result."""
        detected_mode = determine_hardware_strategy(self.ffmpeg_path)
        self.finished.emit(detected_mode)

def write_pid_file():
    print(f"DEBUG: Attempting to write PID {os.getpid()} to {PID_FILE_PATH}")
    try:
        with open(PID_FILE_PATH, "w") as f:
            f.write(str(os.getpid()))
        print(f"DEBUG: Successfully wrote PID {os.getpid()} to {PID_FILE_PATH}")
    except Exception as e:
        print(f"ERROR: Failed to write PID file {PID_FILE_PATH}: {e}")

def remove_pid_file():
    print(f"DEBUG: Attempting to remove PID file {PID_FILE_PATH}")
    try:
        if os.path.exists(PID_FILE_PATH):
            os.remove(PID_FILE_PATH)
            print(f"DEBUG: Successfully removed PID file {PID_FILE_PATH}")
        else:
            print(f"DEBUG: PID file {PID_FILE_PATH} does not exist, nothing to remove.")
    except Exception as e:
        print(f"ERROR: Failed to remove PID file {PID_FILE_PATH}: {e}")
os.environ['PATH'] = BIN_DIR + os.pathsep + PLUGINS + os.pathsep + os.environ.get('PATH','')
from ui.main_window import VideoCompressorApp

def check_encoder_capability(ffmpeg_path: str, encoder_name: str) -> bool:
    """
    Truly verifies GPU support by attempting to encode a single dummy frame.
    This prevents false positives where drivers are installed but the GPU is disabled/detached.
    """
    print(f"DEBUG: Testing encoder '{encoder_name}' with dummy frame...")
    try:
        cmd = [
            ffmpeg_path, "-y", "-hide_banner", "-loglevel", "error",
            "-f", "lavfi", "-i", "color=c=black:s=1920x1080",
            "-vframes", "1", "-c:v", encoder_name, "-f", "null", "-"
        ]
        startupinfo = None
        if sys.platform == "win32":
            startupinfo = subprocess.STARTUPINFO()
            startupinfo.dwFlags |= subprocess.STARTF_USESHOWWINDOW
            startupinfo.wShowWindow = subprocess.SW_HIDE
        result = subprocess.run(
            cmd,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            startupinfo=startupinfo,
            creationflags=(subprocess.CREATE_NO_WINDOW if sys.platform == "win32" else 0),
            timeout=15
        )
        if result.returncode == 0:
            print(f"DEBUG: Encoder '{encoder_name}' is WORKING.")
            return True
        else:
            print(f"DEBUG: Encoder '{encoder_name}' failed test.")
            return False
    except (subprocess.TimeoutExpired, Exception) as e:
        print(f"DEBUG: Exception testing '{encoder_name}': {e}")
        return False

def determine_hardware_strategy(ffmpeg_path):
    """
    Failover logic: NVIDIA -> AMD -> Intel -> Force CPU.
    """
    os.environ.pop("VIDEO_HW_ENCODER", None)
    os.environ.pop("VIDEO_FORCE_CPU", None)
    if check_encoder_capability(ffmpeg_path, "h264_nvenc"):
        os.environ["VIDEO_HW_ENCODER"] = "h264_nvenc"
        return "NVIDIA"
    if check_encoder_capability(ffmpeg_path, "h264_amf"):
        os.environ["VIDEO_HW_ENCODER"] = "h264_amf"
        return "AMD"
    if check_encoder_capability(ffmpeg_path, "h264_qsv"):
        os.environ["VIDEO_HW_ENCODER"] = "h264_qsv"
        return "INTEL"
    os.environ["VIDEO_FORCE_CPU"] = "1"
    return "CPU"

if __name__ == "__main__":
    ffmpeg_path = os.path.join(BIN_DIR, 'ffmpeg.exe')
    ffprobe_path = os.path.join(BIN_DIR, 'ffprobe.exe')
    try:
        startupinfo = None
        if sys.platform == "win32":
            startupinfo = subprocess.STARTUPINFO()
            startupinfo.dwFlags |= subprocess.STARTF_USESHOWWINDOW
            startupinfo.wShowWindow = subprocess.SW_HIDE

        subprocess.run([ffmpeg_path, '-version'], check=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                        startupinfo=startupinfo, creationflags=(subprocess.CREATE_NO_WINDOW if sys.platform == "win32" else 0))
        subprocess.run([ffprobe_path, '-version'], check=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                        startupinfo=startupinfo, creationflags=(subprocess.CREATE_NO_WINDOW if sys.platform == "win32" else 0))
    except (FileNotFoundError, subprocess.CalledProcessError):
        temp_app = QApplication(sys.argv)
        msg_box = QMessageBox()
        msg_box.setIcon(QMessageBox.Critical)
        msg_box.setWindowTitle("Dependency Error")
        msg_box.setText(
            "FFmpeg or FFprobe not found. Please ensure both 'ffmpeg.exe' and 'ffprobe.exe' are in the same folder as this application.")
        msg_box.exec_()
        sys.exit(1)
    app = QCoreApplication.instance()
    if app is None:
        app = QApplication(sys.argv)
    print("DEBUG: Connecting remove_pid_file to app.aboutToQuit signal.")
    app.aboutToQuit.connect(remove_pid_file)
    write_pid_file()
    print("DEBUG: Called write_pid_file().")
    if sys.platform.startswith("win"):
        try:
            ctypes.windll.shell32.SetCurrentProcessExplicitAppUserModelID("FortniteVideoTool.VideoCompressor")
        except Exception:
            pass
        try:
            hwnd = ctypes.windll.kernel32.GetConsoleWindow()
            if hwnd:
                ctypes.windll.user32.ShowWindow(hwnd, 0) # 0 = SW_HIDE
        except Exception:
            pass
    icon_path = ""
    try:
        preferred = os.path.join(BASE_DIR, "icons", "Video_Icon_File.ico")
        fallback  = os.path.join(BASE_DIR, "icons", "app_icon.ico")
        icon_path = preferred if os.path.exists(preferred) else fallback
        if os.path.exists(icon_path):
            app.setWindowIcon(QIcon(icon_path))
    except Exception:
        pass
    
    file_arg = sys.argv[1] if len(sys.argv) > 1 else None
    ex = VideoCompressorApp(file_arg, "Scanning...")
    app.installEventFilter(ex)
    try:
        if icon_path and os.path.exists(icon_path):
            ex.setWindowIcon(QIcon(icon_path))
    except Exception:
        pass
    ex.show()


    hw_thread = QThread()
    hw_worker = HardwareWorker(ffmpeg_path)
    hw_worker.moveToThread(hw_thread)
    hw_thread.started.connect(hw_worker.run)
    hw_worker.finished.connect(ex.on_hardware_scan_finished)
    hw_worker.finished.connect(hw_thread.quit)
    hw_worker.finished.connect(hw_worker.deleteLater)
    hw_thread.finished.connect(hw_thread.deleteLater)
    hw_thread.start()

    print("DEBUG: Main window shown. Entering app.exec_().")
    ret = app.exec_()
    print(f"DEBUG: app.exec_() returned with code: {ret}. App is exiting.")
    sys.exit(ret)
