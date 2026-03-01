import sys
import os
import struct
import platform

# [STRICT] Prevent bytecode generation BEFORE any other imports
sys.dont_write_bytecode = True
os.environ['PYTHONDONTWRITEBYTECODE'] = '1'

# [STRICT] Ensure MPV DLLs and dependencies can be found by ctypes on Windows (Python 3.8+)
# This MUST happen before any project-specific imports that might trigger mpv loading.
BASE_DIR  = os.path.dirname(os.path.abspath(__file__))
BIN_DIR   = os.path.join(BASE_DIR, 'binaries')
PLUGINS   = os.path.join(BIN_DIR, 'plugins')
os.environ["MPV_HOME"] = BIN_DIR
os.environ["MPV_DYLIB_PATH"] = os.path.join(BIN_DIR, "libmpv-2.dll")
if sys.platform == 'win32' and hasattr(os, 'add_dll_directory'):
    try:
        os.add_dll_directory(BIN_DIR)
    except Exception:
        pass
os.environ['PATH'] = BIN_DIR + os.pathsep + PLUGINS + os.pathsep + os.environ.get('PATH','')

if BASE_DIR not in sys.path:
    sys.path.insert(0, BASE_DIR)

from PyQt5.QtWidgets import QApplication, QMessageBox, QProgressDialog, QStyle
from PyQt5.QtCore import QCoreApplication, QObject, QThread, pyqtSignal, QTimer, Qt, QLocale
from PyQt5.QtGui import QIcon
from ui.styles import UIStyles

from system.utils import ConsoleManager, DependencyDoctor, ProcessManager, LogManager
logger = ConsoleManager.initialize(BASE_DIR, "main_app.log", "Main_App")

import tempfile, psutil, traceback
import threading
import subprocess, ctypes

PID_APP_NAME = "fortnite_video_software_main"
ORIGINAL_PATH = os.environ.get("PATH", "")
DEBUG_ENABLED = "--debug" in sys.argv or os.environ.get("FVS_DEBUG") == "1"
ENCODER_TEST_TIMEOUT = int(os.environ.get("FVS_ENCODER_TIMEOUT", "15"))
FORCE_GPU = os.environ.get("FVS_FORCE_GPU")
LOCALE_CODE = QLocale.system().name().split("_")[0].lower()
HARDWARE_SCAN_DETAILS = {"errors": {}, "timed_out": []}

def _has_ffmpeg_encoder(ffmpeg_path: str, encoder_name: str) -> bool:
    """Fast capability hint: checks whether FFmpeg binary advertises a given encoder."""
    try:
        startupinfo = None
        if sys.platform == "win32":
            startupinfo = subprocess.STARTUPINFO()
            startupinfo.dwFlags |= subprocess.STARTF_USESHOWWINDOW
            startupinfo.wShowWindow = subprocess.SW_HIDE
        r = subprocess.run(
            [ffmpeg_path, "-hide_banner", "-encoders"],
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            startupinfo=startupinfo,
            creationflags=(subprocess.CREATE_NO_WINDOW if sys.platform == "win32" else 0),
            timeout=8,
        )
        blob = (r.stdout or b"") + b"\n" + (r.stderr or b"")
        return encoder_name.lower() in blob.decode(errors="ignore").lower()
    except Exception:
        return False

def _has_gpu_adapter(vendor: str) -> bool:
    """Universal GPU presence check for NVIDIA, AMD, or Intel."""
    if sys.platform != "win32":
        return False
    v = vendor.lower()
    candidates = [
        ["powershell", "-NoProfile", "-Command", f"Get-CimInstance Win32_VideoController | Where-Object {{ $_.Name -like '*{vendor}*' }} | Select-Object -ExpandProperty Name"],
        ["wmic", "path", "win32_VideoController", "get", "name"],
    ]
    for cmd in candidates:
        try:
            startupinfo = None
            if sys.platform == "win32":
                startupinfo = subprocess.STARTUPINFO()
                startupinfo.dwFlags |= subprocess.STARTF_USESHOWWINDOW
                startupinfo.wShowWindow = subprocess.SW_HIDE
            r = subprocess.run(
                cmd,
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
                startupinfo=startupinfo,
                creationflags=(subprocess.CREATE_NO_WINDOW if sys.platform == "win32" else 0),
                timeout=6,
            )
            text = ((r.stdout or b"") + b"\n" + (r.stderr or b"")).decode(errors="ignore").lower()
            if v in text:
                if v == "nvidia" and any(k in text for k in ("geforce", "rtx", "quadro", "tesla")): return True
                if v == "amd" and any(k in text for k in ("radeon", "ryzen", "rx")): return True
                if v == "intel" and any(k in text for k in ("graphics", "arc", "iris", "xe")): return True
        except Exception:
            continue
    return False

def _has_nvidia_adapter(): return _has_gpu_adapter("NVIDIA")
def _has_amd_adapter():    return _has_gpu_adapter("AMD")
def _has_intel_adapter():  return _has_gpu_adapter("Intel")

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
        "copy_to_clipboard": "Copy to Clipboard",
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
        "copy_to_clipboard": "העתק ללוח",
    }
}

from system.utils import UIManager

def tr(key: str) -> str:
    return TRANSLATIONS.get(LOCALE_CODE, TRANSLATIONS["en"]).get(key, TRANSLATIONS["en"].get(key, key))

def debug_log(message: str):
    if DEBUG_ENABLED:
        print(message)

# Global PID Handle
PID_FILE_HANDLE = None

def cleanup_pid_lock():
    global PID_FILE_HANDLE
    if PID_FILE_HANDLE:
        try:
            PID_FILE_HANDLE.close()
        except Exception:
            pass
        PID_FILE_HANDLE = None

def _pe_machine_name(pe_path: str):
    """Return PE machine architecture name for a .dll/.exe file."""
    try:
        with open(pe_path, "rb") as f:
            head = f.read(4096)
        pe_off = int.from_bytes(head[0x3C:0x40], "little")
        if pe_off + 6 >= len(head):
            with open(pe_path, "rb") as f:
                f.seek(pe_off + 4)
                machine = int.from_bytes(f.read(2), "little")
        else:
            machine = int.from_bytes(head[pe_off + 4:pe_off + 6], "little")
        return {
            0x014C: "x86",
            0x8664: "x64",
            0xAA64: "arm64",
        }.get(machine, f"unknown(0x{machine:04x})")
    except Exception:
        return "unknown"

def _python_machine_name():
    m = (platform.machine() or "").lower()
    if "arm" in m:
        return "arm64"
    return "x64" if struct.calcsize("P") * 8 == 64 else "x86"

def check_mpv_dependencies():
    """Checks for essential MPV binaries."""
    # Check for libmpv-2.dll
    required = ["libmpv-2.dll"]
    missing = [f for f in required if not os.path.exists(os.path.join(BIN_DIR, f))]
    
    if missing:
        msg_box = QMessageBox()
        msg_box.setIcon(QMessageBox.Critical)
        msg_box.setWindowTitle("MPV Not Found")
        msg_box.setText("MPV is required for video playback.\n\nPlease ensure 'libmpv-2.dll' is in the 'binaries' folder.")
        msg_box.exec_()
        return False
    
    libmpv_path = os.path.join(BIN_DIR, "libmpv-2.dll")
    py_arch = _python_machine_name()
    dll_arch = _pe_machine_name(libmpv_path)
    if dll_arch in ("x86", "x64", "arm64") and dll_arch != py_arch:
        msg_box = QMessageBox()
        msg_box.setIcon(QMessageBox.Critical)
        msg_box.setWindowTitle("MPV Architecture Mismatch")
        msg_box.setText(
            "Failed to import mpv library:\n\n"
            f"Python architecture: {py_arch}\n"
            f"libmpv architecture: {dll_arch}\n\n"
            "They must match exactly.\n"
            "Replace 'binaries\\libmpv-2.dll' with a matching build."
        )
        msg_box.exec_()
        return False

    try:
        import mpv
        return True
    except Exception as e:
        arch = platform.machine()
        msg_box = QMessageBox()
        msg_box.setIcon(QMessageBox.Critical)
        msg_box.setWindowTitle("MPV Import Error")
        
        err_msg = str(e)
        if "WinError 193" in err_msg or "could not load" in err_msg:
            err_msg = (f"Architecture Mismatch: The MPV DLL in 'binaries' is not compatible with this Python version.\n\n"
                       f"System Arch: {arch} / Python: {py_arch} / libmpv: {dll_arch}\n"
                       f"Error: {e}\n\n"
                       f"Please ensure you have a matching version of 'libmpv-2.dll' in the 'binaries' folder.")
        
        msg_box.setText(f"Failed to import mpv library:\n\n{err_msg}")
        msg_box.exec_()
        return False

def get_ffmpeg_hwaccels(ffmpeg_path: str) -> list:
    """Detects available FFmpeg hardware acceleration methods."""
    try:
        startupinfo = None
        if sys.platform == "win32":
            startupinfo = subprocess.STARTUPINFO()
            startupinfo.dwFlags |= subprocess.STARTF_USESHOWWINDOW
            startupinfo.wShowWindow = subprocess.SW_HIDE
        result = subprocess.run(
            [ffmpeg_path, '-hwaccels'],
            capture_output=True,
            text=True,
            check=True,
            startupinfo=startupinfo,
            creationflags=(subprocess.CREATE_NO_WINDOW if sys.platform == "win32" else 0),
            timeout=5
        )
        hwaccels = []
        for line in result.stdout.splitlines():
            line = line.strip()
            if line and not line.startswith("Hardware acceleration methods:"):
                hwaccels.append(line)
        return hwaccels
    except Exception:
        return []

class HardwareWorker(QObject):
    """
    Worker thread to offload slow hardware capability checks from the main UI thread.
    Dynamically identifies the absolute best path for the current machine.
    """
    finished = pyqtSignal(str)

    def __init__(self, ffmpeg_path):
        super().__init__()
        self.ffmpeg_path = ffmpeg_path
        self.stop_requested = False
        self.watchdog_timer = None

    def stop(self):
        """Request the worker to stop as soon as possible."""
        self.stop_requested = True

    def run(self):
        """Performs the hardware scan and emits the result."""
        import threading
        def watchdog():
            self.stop_requested = True
        self.watchdog_timer = threading.Timer(15.0, watchdog)
        self.watchdog_timer.daemon = True
        self.watchdog_timer.start()
        
        try:
            available = get_ffmpeg_hwaccels(self.ffmpeg_path)
            debug_log(f"DEBUG: Available FFmpeg hwaccels: {available}")
            detected_mode = self._determine_hardware_strategy_with_stop(available)
            self.finished.emit(detected_mode)
        except Exception as e:
            debug_log(f"Hardware scan error: {e}")
            self.finished.emit("CPU")
        finally:
            if self.watchdog_timer:
                self.watchdog_timer.cancel()

    def _determine_hardware_strategy_with_stop(self, available_accels):
        """
        Failover logic with stop flag checking and multi-accel support.
        """
        os.environ.pop("VIDEO_HW_ENCODER", None)
        os.environ.pop("VIDEO_FORCE_CPU", None)
        
        if FORCE_GPU:
            if FORCE_GPU == "NVIDIA": os.environ["VIDEO_HW_ENCODER"] = "h264_nvenc"; return "NVIDIA"
            if FORCE_GPU == "AMD":    os.environ["VIDEO_HW_ENCODER"] = "h264_amf";   return "AMD"
            if FORCE_GPU == "INTEL":  os.environ["VIDEO_HW_ENCODER"] = "h264_qsv";   return "INTEL"

        # 1. TEST NVIDIA
        if not self.stop_requested and "cuda" in available_accels:
            if check_encoder_capability(self.ffmpeg_path, "h264_nvenc"):
                os.environ["VIDEO_HW_ENCODER"] = "h264_nvenc"
                return "NVIDIA"

        # 2. TEST AMD
        if not self.stop_requested and "d3d11va" in available_accels:
            if check_encoder_capability(self.ffmpeg_path, "h264_amf"):
                os.environ["VIDEO_HW_ENCODER"] = "h264_amf"
                return "AMD"

        # 3. TEST INTEL
        if not self.stop_requested and ("qsv" in available_accels or "d3d11va" in available_accels):
            if check_encoder_capability(self.ffmpeg_path, "h264_qsv"):
                os.environ["VIDEO_HW_ENCODER"] = "h264_qsv"
                return "INTEL"

        # 4. FINAL FALLBACK CHECKS
        if not self.stop_requested:
            if _has_nvidia_adapter(): return "NVIDIA"
            if _has_amd_adapter():    return "AMD"
            if _has_intel_adapter():  return "INTEL"

        os.environ["VIDEO_FORCE_CPU"] = "1"
        return "CPU"

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
            timeout=4.5 # [FIX #5] Aggressive 4.5s probe timeout for fast discovery
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
    
    if FORCE_GPU == "NVIDIA":
        os.environ["VIDEO_HW_ENCODER"] = "h264_nvenc"
        return "NVIDIA"

    if check_encoder_capability(ffmpeg_path, "h264_nvenc"):
        os.environ["VIDEO_HW_ENCODER"] = "h264_nvenc"
        return "NVIDIA"
    if _has_ffmpeg_encoder(ffmpeg_path, "h264_nvenc") and _has_nvidia_adapter():
        os.environ["VIDEO_HW_ENCODER"] = "h264_nvenc"
        HARDWARE_SCAN_DETAILS["errors"]["h264_nvenc"] = (
            "Strict NVENC probe failed, but NVIDIA adapter + FFmpeg NVENC encoder were detected. "
            "Using NVIDIA mode via fallback."
        )
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
    details = build_diagnostics(ffmpeg_path, ffprobe_path, error_text)
    msg_box.setDetailedText(details)
    
    # [FIX] Make the dialog larger and add Copy button and Hand Cursor
    UIManager.style_and_size_msg_box(msg_box, details, tr("copy_to_clipboard"))
    
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
            
            # [FIX] Make the dialog larger and add Copy button and Hand Cursor
            UIManager.style_and_size_msg_box(msg_box, f"Value: {value}\n\nTraceback:\n{error_text}", tr("copy_to_clipboard"))
                
            msg_box.exec_()
    except Exception:
        pass
    sys.__excepthook__(exctype, value, tb)

if __name__ == "__main__":
    # [FIX #3 & #7] Clean up orphans and temps
    ProcessManager.kill_orphans()
    ProcessManager.cleanup_temp_files()
    
    # [FIX #1] Dependency Doctor Check
    is_valid_deps, ffmpeg_path, dep_error = DependencyDoctor.check_ffmpeg(BASE_DIR)
    
    # Set paths for QProcess to inherit
    ffprobe_path = os.path.join(os.path.dirname(ffmpeg_path), "ffprobe.exe" if sys.platform == "win32" else "ffprobe")

    app = QCoreApplication.instance()
    if app is None:
        app = QApplication(sys.argv)
    app.setStyleSheet(UIStyles.GLOBAL_STYLE)
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

    # [FIX #16] MPV Dynamic Path Check
    if not check_mpv_dependencies():
        logger.warning("MPV dependencies missing or incompatible. Playback will be disabled.")
        # if PID_FILE_HANDLE: PID_FILE_HANDLE.close()
        # sys.exit(1)

    from ui.main_window import VideoCompressorApp

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
    
    # [FIX #4] Reuse last hardware strategy if available (But never lock into CPU mode permanently)
    from system.config import ConfigManager
    config_path = os.path.join(BASE_DIR, 'config', 'main_app', 'main_app.conf')
    cm = ConfigManager(config_path)
    cached_hw = cm.config.get("last_hardware_strategy")
    
    # If the last scan resulted in CPU, we re-scan every time to give the GPU another chance (e.g. after driver update)
    if str(cached_hw or "").upper() == "CPU":
        cached_hw = None
    
    file_arg = sys.argv[1] if len(sys.argv) > 1 else None
    
    # If we have a cached GPU strategy, use it immediately for a fast start
    initial_strategy = cached_hw if cached_hw else "Scanning..."
    
    if cached_hw == "NVIDIA":
        os.environ["VIDEO_HW_ENCODER"] = "h264_nvenc"
    elif cached_hw == "AMD":
        os.environ["VIDEO_HW_ENCODER"] = "h264_amf"
    elif cached_hw == "INTEL":
        os.environ["VIDEO_HW_ENCODER"] = "h264_qsv"

    ex = VideoCompressorApp(file_arg, initial_strategy)
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

    # [FIX #1] Delayed non-blocking hardware scan to prevent UI freeze
    def start_hw_scan():
        if cached_hw:
            debug_log(f"DEBUG: Using cached hardware strategy: {cached_hw}")
            ex.on_hardware_scan_finished(cached_hw)
            ex.scan_complete = True
            return

        hw_thread = QThread()
        hw_worker = HardwareWorker(ffmpeg_path)
        hw_worker.moveToThread(hw_thread)
        hw_thread.started.connect(hw_worker.run)
        hw_worker.finished.connect(ex.on_hardware_scan_finished)
        hw_worker.finished.connect(hw_thread.quit)
        hw_worker.finished.connect(hw_worker.deleteLater)
        hw_thread.finished.connect(hw_thread.deleteLater)
        
        # Reference to prevent GC
        ex._hw_thread = hw_thread
        ex._hw_worker = hw_worker
        
        hw_thread.start()
        debug_log("DEBUG: Background hardware scan started.")

    # [FIX] Start scan after UI loop has settled (500ms delay)
    QTimer.singleShot(500, start_hw_scan)

    debug_log("DEBUG: Main window shown. Entering app.exec_().")
    ret = app.exec_()
    debug_log(f"DEBUG: app.exec_() returned with code: {ret}. App is exiting.")
    sys.exit(ret)
