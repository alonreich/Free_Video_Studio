
import sys
import os
# [STRICT] Prevent bytecode generation BEFORE any other imports
sys.dont_write_bytecode = True
os.environ['PYTHONDONTWRITEBYTECODE'] = '1'
os.environ['PYTHONPYCACHEPREFIX'] = os.path.join(os.path.expanduser('~'), '.null_cache_dir')

import tempfile, psutil, traceback

from PyQt5.QtWidgets import QApplication, QMessageBox, QProgressDialog, QStyle
from PyQt5.QtCore import QCoreApplication, QObject, QThread, pyqtSignal, QTimer, Qt, QLocale
from PyQt5.QtGui import QIcon
import subprocess, ctypes

BASE_DIR  = os.path.dirname(os.path.abspath(__file__))
# Ensure system module is in path
if BASE_DIR not in sys.path:
    sys.path.insert(0, BASE_DIR)

from system.utils import DependencyDoctor, ProcessManager, LogManager

BIN_DIR   = os.path.join(BASE_DIR, 'binaries')
PLUGINS   = os.path.join(BIN_DIR, 'plugins')
PID_APP_NAME = "fortnite_video_software_main"
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

# Global PID Handle
PID_FILE_HANDLE = None

def check_vlc_dependencies():
    """[FIX #1] Checks for essential VLC binaries using DependencyDoctor logic."""
    vlc_path = DependencyDoctor.find_vlc_path()
    
    # If we found a system install, add it to PATH/DLL directory
    if vlc_path:
        os.environ["PATH"] = vlc_path + os.pathsep + os.environ["PATH"]
        if hasattr(os, 'add_dll_directory'):
            try:
                os.add_dll_directory(vlc_path)
            except Exception: pass
        return True

    # Fallback to local binaries
    required = ["libvlc.dll", "libvlccore.dll"]
    missing = [f for f in required if not os.path.exists(os.path.join(BIN_DIR, f))]
    
    if missing:
        msg_box = QMessageBox()
        msg_box.setIcon(QMessageBox.Critical)
        msg_box.setWindowTitle("Missing Components")
        msg_box.setText("Critical VLC components are missing.")
        msg_box.setDetailedText(f"The following files are missing from {BIN_DIR} and no system VLC installation was found:\n" + "\n".join(missing))
        msg_box.exec_()
        return False
    return True

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
    # [FIX #3 & #7] Clean up orphans and temps
    ProcessManager.kill_orphans()
    ProcessManager.cleanup_temp_files()
    
    # [FIX] Standardized Log Filename
    logger = LogManager.setup_logger(BASE_DIR, "main_app.log", "Main_App")
    
    # [FIX #1] Dependency Doctor Check
    is_valid_deps, ffmpeg_path, dep_error = DependencyDoctor.check_ffmpeg(BASE_DIR)
    
    # Set paths for QProcess to inherit
    ffprobe_path = os.path.join(os.path.dirname(ffmpeg_path), "ffprobe.exe" if sys.platform == "win32" else "ffprobe")

    app = QCoreApplication.instance()
    if app is None:
        app = QApplication(sys.argv)
    app.setApplicationName(tr("app_name"))
    QCoreApplication.setOrganizationName("FortniteVideoSoftware")
    sys.excepthook = exception_hook

    # [FIX #5 & #6] Robust single instance check with ProcessManager and Retry
    import time
    pid_retries = 3
    success = False
    pid_handle = None
    
    for attempt in range(pid_retries):
        success, pid_handle = ProcessManager.acquire_pid_lock(PID_APP_NAME)
        if success:
            break
        # Wait for previous instance to close if handoff is happening
        time.sleep(0.5)

    if not success:
        msg_box = QMessageBox()
        msg_box.setIcon(QMessageBox.Information)
        msg_box.setWindowTitle(tr("single_instance_title"))
        msg_box.setText(tr("single_instance_text"))
        msg_box.exec_()
        sys.exit(0)
    PID_FILE_HANDLE = pid_handle

    # [FIX #16] VLC Dynamic Path Check
    if not check_vlc_dependencies():
        if PID_FILE_HANDLE: PID_FILE_HANDLE.close()
        sys.exit(1)

    if not is_valid_deps:
         # Fallback error handling logic
         while True:
            action = show_dependency_error_dialog(ffmpeg_path, ffprobe_path, dep_error)
            if action == "open": continue
            if action == "retry":
                 is_valid_retry, ffmpeg_path, dep_error = DependencyDoctor.check_ffmpeg(BASE_DIR)
                 if is_valid_retry: break
                 continue
            sys.exit(1)

    app.aboutToQuit.connect(lambda: os.environ.__setitem__("PATH", ORIGINAL_PATH))
    
    if sys.platform.startswith("win"):
        try:
            ctypes.windll.shell32.SetCurrentProcessExplicitAppUserModelID("FortniteVideoTool.VideoCompressor")
        except Exception:
            pass
        try:
            hwnd = ctypes.windll.kernel32.GetConsoleWindow()
            if hwnd:
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
    try:
        if icon_path and os.path.exists(icon_path):
            ex.setWindowIcon(QIcon(icon_path))
        elif hasattr(app, "style"):
            ex.setWindowIcon(app.style().standardIcon(QStyle.SP_ComputerIcon))
    except Exception:
        pass
    ex.show()

    # [FIX] Initial active state for hint
    if not file_arg:
        ex._set_upload_hint_active(True)

    try:
        if hasattr(ex, "statusBar"):
            ex.statusBar().showMessage(tr("ffmpeg_path_message").format(ffmpeg=ffmpeg_path), 8000)
    except Exception:
        pass

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
            if scan_dialog:
                scan_dialog.close()
        except Exception:
            pass
            
        try:
            if ex and not ex.isHidden():
                title_suffix = f" — {tr('hardware_scan_done').format(mode=mode)}"
                ex.setWindowTitle(tr("app_name") + title_suffix)
                
                if hasattr(ex, "statusBar"):
                    message = tr("hardware_scan_done").format(mode=mode)
                    if mode == "CPU":
                        message = tr("hardware_scan_cpu")
                        details = tr("hardware_scan_cpu_details")
                        if HARDWARE_SCAN_DETAILS["timed_out"]:
                            timeouts = ", ".join(HARDWARE_SCAN_DETAILS["timed_out"])
                            details = f"{details} ({timeouts})"
                        message = f"{message} {details}"
                    
                    if hasattr(ex, "hardware_status_label"):
                        # [FIX #8] Persistent hardware badge text
                        badge_icon = "🚀" if mode in ["NVIDIA", "AMD", "INTEL"] else "⚠️"
                        ex.hardware_status_label.setText(f"{badge_icon} {mode} Mode")
                        ex.hardware_status_label.setStyleSheet("color: #43b581; font-weight: bold;" if mode != "CPU" else "color: #ffa500; font-weight: bold;")
                    else:
                        ex.statusBar().showMessage(message, 10000)
        except (RuntimeError, NameError):
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
