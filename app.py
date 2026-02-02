

import os, sys, tempfile, psutil, traceback
# Prevent creation of __pycache__ folders
os.environ['PYTHONDONTWRITEBYTECODE'] = '1'
# Use a non-existent directory to prevent cache creation
os.environ['PYTHONPYCACHEPREFIX'] = os.path.join(os.path.expanduser('~'), '.null_cache_dir')
sys.dont_write_bytecode = True

try:
    import audioop
    AUDIOOP_AVAILABLE = True
except ImportError:
    try:
        import audioop_lts as audioop_shim
        sys.modules['audioop'] = audioop_shim
        AUDIOOP_AVAILABLE = True
    except ImportError:
        AUDIOOP_AVAILABLE = False
from PyQt5.QtWidgets import QApplication, QMessageBox, QProgressDialog, QStyle
from PyQt5.QtCore import QCoreApplication, QObject, QThread, pyqtSignal, QTimer, Qt, QLocale
from PyQt5.QtGui import QIcon
import subprocess, ctypes

BASE_DIR  = os.path.dirname(os.path.abspath(__file__))
BIN_DIR   = os.path.join(BASE_DIR, 'binaries')
PLUGINS   = os.path.join(BIN_DIR, 'plugins')
PID_FILE_NAME = "fortnite_video_software_app.pid"
PID_FILE_PATH = os.path.join(tempfile.gettempdir(), PID_FILE_NAME)
ORIGINAL_PATH = os.environ.get("PATH", "")
DEBUG_ENABLED = "--debug" in sys.argv or os.environ.get("FVS_DEBUG") == "1"
ENCODER_TEST_TIMEOUT = int(os.environ.get("FVS_ENCODER_TIMEOUT", "8"))
LOCALE_CODE = QLocale.system().name().split("_")[0].lower()
HARDWARE_SCAN_DETAILS = {"errors": {}, "timed_out": []}

TRANSLATIONS = {
    "en": {
        "app_name": "Fortnite Video Software",
        "dependency_error_title": "Dependency Error",
        "dependency_error_text": "FFmpeg or FFprobe is missing or not working.\n\nPlease ensure both 'ffmpeg.exe' and 'ffprobe.exe' are in the 'binaries' folder next to this app.",
        "dependency_error_open_folder": "Open Binaries Folder",
        "dependency_error_retry": "Retry",
        "dependency_error_exit": "Exit",
        "dependency_error_details": "FFmpeg Path: {ffmpeg}\nFFprobe Path: {ffprobe}\nBundled BIN_DIR added to PATH: {bin_dir}\n\nError:\n{error}",
        "single_instance_title": "Already Running",
        "single_instance_text": "The app is already running. Please close the other window before opening another.",
        "audioop_warning_title": "Audio Feature Warning",
        "audioop_warning_text": "An audio component is missing. Some audio features may not work as expected.",
        "pid_warning_title": "Startup Warning",
        "pid_warning_text": "The app could not create a temporary PID file. It will still run, but single-instance detection might be limited.",
        "hardware_scan_title": "Hardware Scan",
        "hardware_scan_text": "Detecting hardware acceleration...",
        "hardware_scan_done": "Hardware mode detected: {mode}",
        "hardware_scan_cpu": "Hardware acceleration not available. Using CPU mode.",
        "hardware_scan_cpu_details": "GPU encoders not detected. You can update your graphics driver and try again.",
        "ffmpeg_path_message": "Using FFmpeg: {ffmpeg}",
        "diagnostics_title": "Diagnostics",
    },
    "he": {
        "app_name": "תוכנת וידאו פורטנייט",
        "dependency_error_title": "שגיאת תלות",
        "dependency_error_text": "FFmpeg או FFprobe חסרים או לא עובדים.\n\nודא שהקבצים 'ffmpeg.exe' ו-'ffprobe.exe' נמצאים בתיקיית 'binaries' ליד האפליקציה.",
        "dependency_error_open_folder": "פתח תיקיית Binaries",
        "dependency_error_retry": "נסה שוב",
        "dependency_error_exit": "יציאה",
        "dependency_error_details": "נתיב FFmpeg: {ffmpeg}\nנתיב FFprobe: {ffprobe}\nתיקיית BIN_DIR נוספה ל-PATH: {bin_dir}\n\nשגיאה:\n{error}",
        "single_instance_title": "כבר פועל",
        "single_instance_text": "האפליקציה כבר פתוחה. סגור את החלון האחר לפני פתיחה נוספת.",
        "audioop_warning_title": "אזהרת אודיו",
        "audioop_warning_text": "רכיב אודיו חסר. חלק מתכונות האודיו עלולות לא לעבוד כמצופה.",
        "pid_warning_title": "אזהרת פתיחה",
        "pid_warning_text": "לא ניתן ליצור קובץ PID זמני. האפליקציה תמשיך לעבוד, אך זיהוי מופע יחיד עלול להיות מוגבל.",
        "hardware_scan_title": "סריקת חומרה",
        "hardware_scan_text": "בודק האצת חומרה...",
        "hardware_scan_done": "מצב חומרה זוהה: {mode}",
        "hardware_scan_cpu": "האצת חומרה אינה זמינה. מעבר למצב CPU.",
        "hardware_scan_cpu_details": "לא נמצאו מקודדי GPU. אפשר לעדכן דרייבר ולנסות שוב.",
        "ffmpeg_path_message": "משתמש ב-FFmpeg: {ffmpeg}",
        "diagnostics_title": "אבחון",
    }
}

def tr(key: str) -> str:
    return TRANSLATIONS.get(LOCALE_CODE, TRANSLATIONS["en"]).get(key, TRANSLATIONS["en"].get(key, key))

def debug_log(message: str):
    if DEBUG_ENABLED:
        print(message)

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
    debug_log(f"DEBUG: Attempting to write PID {os.getpid()} to {PID_FILE_PATH}")
    try:
        with open(PID_FILE_PATH, "w") as f:
            f.write(str(os.getpid()))
        debug_log(f"DEBUG: Successfully wrote PID {os.getpid()} to {PID_FILE_PATH}")
        return True, None
    except Exception as e:
        debug_log(f"ERROR: Failed to write PID file {PID_FILE_PATH}: {e}")
        return False, str(e)

def remove_pid_file():
    debug_log(f"DEBUG: Attempting to remove PID file {PID_FILE_PATH}")
    try:
        if os.path.exists(PID_FILE_PATH):
            os.remove(PID_FILE_PATH)
            debug_log(f"DEBUG: Successfully removed PID file {PID_FILE_PATH}")
        else:
            debug_log(f"DEBUG: PID file {PID_FILE_PATH} does not exist, nothing to remove.")
    except Exception as e:
        debug_log(f"ERROR: Failed to remove PID file {PID_FILE_PATH}: {e}")
os.environ['PATH'] = BIN_DIR + os.pathsep + PLUGINS + os.pathsep + os.environ.get('PATH','')
from ui.main_window import VideoCompressorApp

def check_encoder_capability(ffmpeg_path: str, encoder_name: str) -> bool:
    """
    Truly verifies GPU support by attempting to encode a single dummy frame.
    This prevents false positives where drivers are installed but the GPU is disabled/detached.
    """
    debug_log(f"DEBUG: Testing encoder '{encoder_name}' with dummy frame...")
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
            timeout=ENCODER_TEST_TIMEOUT
        )
        if result.returncode == 0:
            debug_log(f"DEBUG: Encoder '{encoder_name}' is WORKING.")
            return True
        else:
            HARDWARE_SCAN_DETAILS["errors"][encoder_name] = result.stderr.decode(errors="ignore")[:500]
            debug_log(f"DEBUG: Encoder '{encoder_name}' failed test.")
            return False
    except (subprocess.TimeoutExpired, Exception) as e:
        if isinstance(e, subprocess.TimeoutExpired):
            HARDWARE_SCAN_DETAILS["timed_out"].append(encoder_name)
        else:
            HARDWARE_SCAN_DETAILS["errors"][encoder_name] = str(e)
        debug_log(f"DEBUG: Exception testing '{encoder_name}': {e}")
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

def build_diagnostics(ffmpeg_path: str, ffprobe_path: str, error_text: str) -> str:
    return tr("dependency_error_details").format(
        ffmpeg=ffmpeg_path,
        ffprobe=ffprobe_path,
        bin_dir=BIN_DIR,
        error=error_text or "Unknown error"
    )

def show_dependency_error_dialog(ffmpeg_path: str, ffprobe_path: str, error_text: str):
    msg_box = QMessageBox()
    msg_box.setIcon(QMessageBox.Critical)
    msg_box.setWindowTitle(tr("dependency_error_title"))
    msg_box.setText(tr("dependency_error_text"))
    msg_box.setDetailedText(build_diagnostics(ffmpeg_path, ffprobe_path, error_text))
    open_button = msg_box.addButton(tr("dependency_error_open_folder"), QMessageBox.ActionRole)
    retry_button = msg_box.addButton(tr("dependency_error_retry"), QMessageBox.AcceptRole)
    exit_button = msg_box.addButton(tr("dependency_error_exit"), QMessageBox.RejectRole)
    msg_box.exec_()
    clicked = msg_box.clickedButton()
    if clicked == open_button:
        try:
            subprocess.Popen(["explorer", BIN_DIR])
        except Exception:
            pass
        return "open"
    if clicked == retry_button:
        return "retry"
    if clicked == exit_button:
        return "exit"
    return "exit"

def check_single_instance(app: QApplication):
    if not os.path.exists(PID_FILE_PATH):
        return True
    try:
        with open(PID_FILE_PATH, "r") as f:
            pid_text = f.read().strip()
        if not pid_text.isdigit():
            os.remove(PID_FILE_PATH)
            return True
        pid = int(pid_text)
        if psutil.pid_exists(pid):
            try:
                proc = psutil.Process(pid)
                cmdline = " ".join(proc.cmdline()).lower()
                expected = os.path.abspath(__file__).lower()
                if expected in cmdline or os.path.basename(expected) in cmdline:
                    msg_box = QMessageBox()
                    msg_box.setIcon(QMessageBox.Information)
                    msg_box.setWindowTitle(tr("single_instance_title"))
                    msg_box.setText(tr("single_instance_text"))
                    msg_box.exec_()
                    return False
            except Exception:
                pass
        os.remove(PID_FILE_PATH)
        return True
    except Exception:
        return True

def show_startup_warning(app: QApplication, title: str, text: str):
    if hasattr(app, "activeWindow") and app.activeWindow():
        window = app.activeWindow()
        if hasattr(window, "statusBar"):
            try:
                window.statusBar().showMessage(text, 8000)
                return
            except Exception:
                pass
    msg = QMessageBox()
    msg.setIcon(QMessageBox.Warning)
    msg.setWindowTitle(title)
    msg.setText(text)
    msg.exec_()

def exception_hook(exctype, value, tb):
    error_text = "".join(traceback.format_exception(exctype, value, tb))
    debug_log(error_text)
    try:
        app = QCoreApplication.instance()
        if app is not None:
            msg_box = QMessageBox()
            msg_box.setIcon(QMessageBox.Critical)
            msg_box.setWindowTitle(tr("diagnostics_title"))
            msg_box.setText(str(value))
            msg_box.setDetailedText(error_text)
            msg_box.exec_()
    except Exception:
        pass
    sys.__excepthook__(exctype, value, tb)

if __name__ == "__main__":
    ffmpeg_path = os.path.join(BIN_DIR, 'ffmpeg.exe')
    ffprobe_path = os.path.join(BIN_DIR, 'ffprobe.exe')
    app = QCoreApplication.instance()
    if app is None:
        app = QApplication(sys.argv)
    app.setApplicationName(tr("app_name"))
    QCoreApplication.setOrganizationName("FortniteVideoSoftware")
    sys.excepthook = exception_hook

    if not check_single_instance(app):
        sys.exit(0)
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
    except (FileNotFoundError, subprocess.CalledProcessError) as e:
        error_text = str(e)
        if isinstance(e, subprocess.CalledProcessError):
            error_text = e.stderr.decode(errors="ignore") if e.stderr else str(e)
        while True:
            action = show_dependency_error_dialog(ffmpeg_path, ffprobe_path, error_text)
            if action == "open":
                continue
            if action == "retry":
                try:
                    subprocess.run([ffmpeg_path, '-version'], check=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                                    startupinfo=startupinfo, creationflags=(subprocess.CREATE_NO_WINDOW if sys.platform == "win32" else 0))
                    subprocess.run([ffprobe_path, '-version'], check=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                                    startupinfo=startupinfo, creationflags=(subprocess.CREATE_NO_WINDOW if sys.platform == "win32" else 0))
                    break
                except (FileNotFoundError, subprocess.CalledProcessError) as retry_error:
                    error_text = str(retry_error)
                    if isinstance(retry_error, subprocess.CalledProcessError):
                        error_text = retry_error.stderr.decode(errors="ignore") if retry_error.stderr else str(retry_error)
                    continue
            sys.exit(1)
    debug_log("DEBUG: Connecting remove_pid_file to app.aboutToQuit signal.")
    app.aboutToQuit.connect(remove_pid_file)
    app.aboutToQuit.connect(lambda: os.environ.__setitem__("PATH", ORIGINAL_PATH))
    success, pid_error = write_pid_file()
    if not success:
        show_startup_warning(app, tr("pid_warning_title"), tr("pid_warning_text"))
    debug_log("DEBUG: Called write_pid_file().")
    if sys.platform.startswith("win"):
        try:
            ctypes.windll.shell32.SetCurrentProcessExplicitAppUserModelID("FortniteVideoTool.VideoCompressor")
        except Exception:
            pass
        try:
            hwnd = ctypes.windll.kernel32.GetConsoleWindow()
            if hwnd:
                # Hide the console window
                # 0 = SW_HIDE, 1 = SW_SHOWNORMAL, 5 = SW_SHOW, 9 = SW_RESTORE
                ctypes.windll.user32.ShowWindow(hwnd, 0)
        except Exception:
            pass
    icon_path = ""
    try:
        preferred = os.path.join(BASE_DIR, "icons", "Video_Icon_File.ico")
        fallback  = os.path.join(BASE_DIR, "icons", "app_icon.ico")
        icon_path = preferred if os.path.exists(preferred) else fallback
        if os.path.exists(icon_path):
            app.setWindowIcon(QIcon(icon_path))
        else:
            app.setWindowIcon(app.style().standardIcon(QStyle.SP_ComputerIcon))
    except Exception:
        pass
    
    file_arg = sys.argv[1] if len(sys.argv) > 1 else None
    ex = VideoCompressorApp(file_arg, "Scanning...")
    app.installEventFilter(ex)
    try:
        if icon_path and os.path.exists(icon_path):
            ex.setWindowIcon(QIcon(icon_path))
        elif hasattr(app, "style"):
            ex.setWindowIcon(app.style().standardIcon(QStyle.SP_ComputerIcon))
    except Exception:
        pass
    ex.show()

    try:
        if hasattr(ex, "statusBar"):
            ex.statusBar().showMessage(tr("ffmpeg_path_message").format(ffmpeg=ffmpeg_path), 8000)
    except Exception:
        pass

    if not AUDIOOP_AVAILABLE:
        QTimer.singleShot(0, lambda: show_startup_warning(app, tr("audioop_warning_title"), tr("audioop_warning_text")))

    scan_dialog = QProgressDialog(tr("hardware_scan_text"), "", 0, 0, ex)
    scan_dialog.setWindowTitle(tr("hardware_scan_title"))
    scan_dialog.setWindowModality(Qt.NonModal)
    scan_dialog.setCancelButton(None)
    scan_dialog.setMinimumDuration(0)
    scan_dialog.show()


    hw_thread = QThread()
    hw_worker = HardwareWorker(ffmpeg_path)
    hw_worker.moveToThread(hw_thread)
    hw_thread.started.connect(hw_worker.run)
    hw_worker.finished.connect(ex.on_hardware_scan_finished)
    def handle_hardware_scan_result(mode: str):
        try:
            scan_dialog.close()
        except Exception:
            pass
        title_suffix = f" — {tr('hardware_scan_done').format(mode=mode)}"
        try:
            ex.setWindowTitle(tr("app_name") + title_suffix)
        except Exception:
            pass
        try:
            if hasattr(ex, "statusBar"):
                message = tr("hardware_scan_done").format(mode=mode)
                if mode == "CPU":
                    message = tr("hardware_scan_cpu")
                    details = tr("hardware_scan_cpu_details")
                    if HARDWARE_SCAN_DETAILS["timed_out"]:
                        timeouts = ", ".join(HARDWARE_SCAN_DETAILS["timed_out"])
                        details = f"{details} ({timeouts})"
                    message = f"{message} {details}"
                ex.statusBar().showMessage(message, 10000)
        except Exception:
            pass
    hw_worker.finished.connect(handle_hardware_scan_result)
    hw_worker.finished.connect(hw_thread.quit)
    hw_worker.finished.connect(hw_worker.deleteLater)
    hw_thread.finished.connect(hw_thread.deleteLater)
    hw_thread.start()

    debug_log("DEBUG: Main window shown. Entering app.exec_().")
    ret = app.exec_()
    debug_log(f"DEBUG: app.exec_() returned with code: {ret}. App is exiting.")
    sys.exit(ret)
