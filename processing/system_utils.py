import subprocess
import sys
import psutil
import re
import os
import time
_job_handle = None
if sys.platform == "win32":
    try:
        import ctypes
        from ctypes import wintypes
        kernel32 = ctypes.windll.kernel32
        _job_handle = kernel32.CreateJobObjectW(None, None)
        if _job_handle:
            class JOBOBJECT_BASIC_LIMIT_INFORMATION(ctypes.Structure):
                _fields_ = [
                    ("PerProcessUserTimeLimit", wintypes.LARGE_INTEGER),
                    ("PerJobUserTimeLimit", wintypes.LARGE_INTEGER),
                    ("LimitFlags", wintypes.DWORD),
                    ("MinimumWorkingSetSize", ctypes.c_size_t),
                    ("MaximumWorkingSetSize", ctypes.c_size_t),
                    ("ActiveProcessLimit", wintypes.DWORD),
                    ("Affinity", ctypes.c_size_t),
                    ("PriorityClass", wintypes.DWORD),
                    ("SchedulingClass", wintypes.DWORD),
                ]

            class IO_COUNTERS(ctypes.Structure):
                _fields_ = [("ReadOperationCount", ctypes.c_ulonglong), ("WriteOperationCount", ctypes.c_ulonglong), ("OtherOperationCount", ctypes.c_ulonglong), ("ReadTransferCount", ctypes.c_ulonglong), ("WriteTransferCount", ctypes.c_ulonglong), ("OtherTransferCount", ctypes.c_ulonglong)]

            class JOBOBJECT_EXTENDED_LIMIT_INFORMATION(ctypes.Structure):
                _fields_ = [
                    ("BasicLimitInformation", JOBOBJECT_BASIC_LIMIT_INFORMATION),
                    ("IoInfo", IO_COUNTERS),
                    ("ProcessMemoryLimit", ctypes.c_size_t),
                    ("JobMemoryLimit", ctypes.c_size_t),
                    ("PeakProcessMemoryUsed", ctypes.c_size_t),
                    ("PeakJobMemoryUsed", ctypes.c_size_t),
                ]
            JobObjectExtendedLimitInformation = 9
            JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000
            limits = JOBOBJECT_EXTENDED_LIMIT_INFORMATION()
            res = kernel32.QueryInformationJobObject(_job_handle, JobObjectExtendedLimitInformation, ctypes.byref(limits), ctypes.sizeof(limits), None)
            if res:
                limits.BasicLimitInformation.LimitFlags |= JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                kernel32.SetInformationJobObject(_job_handle, JobObjectExtendedLimitInformation, ctypes.byref(limits), ctypes.sizeof(limits))
    except Exception:
        pass

def parse_time_to_seconds(time_str: str) -> float:
    try:
        s = str(time_str or "").strip()
        if not s:
            return 0.0
        parts = s.split(":")
        count = len(parts)
        if count == 3:
            h = int(parts[0])
            m = int(parts[1])
            sec = float(parts[2])
            return float((h * 3600) + (m * 60) + sec)
        if count == 2:
            m = int(parts[0])
            sec = float(parts[1])
            return float((m * 60) + sec)
        if count == 1:
            return float(parts[0])
    except Exception:
        return 0.0
    return 0.0

def create_subprocess(cmd, logger=None):
    if logger:
        clean_cmd = [os.path.basename(cmd[0])] + cmd[1:]
        logger.info(f"Starting process: {' '.join(clean_cmd[:5])}...")
    startupinfo = None
    creationflags = 0
    if sys.platform == "win32":
        startupinfo = subprocess.STARTUPINFO()
        startupinfo.dwFlags |= subprocess.STARTF_USESHOWWINDOW
        creationflags = subprocess.CREATE_NO_WINDOW | 0x00000200
    proc = subprocess.Popen(
        cmd,
        stdout=subprocess.PIPE,
        stderr=subprocess.STDOUT,
        stdin=subprocess.DEVNULL,
        text=True,
        creationflags=creationflags,
        startupinfo=startupinfo,
        encoding="utf-8",
        errors="replace"
    )
    if _job_handle and sys.platform == "win32":
        try:
            import ctypes
            kernel32 = ctypes.windll.kernel32
            kernel32.AssignProcessToJobObject(_job_handle, int(proc._handle))
        except Exception:
            pass
    return proc

def kill_process_tree(pid, logger=None):
    if not pid:
        return
    if sys.platform == "win32":
        try:
            subprocess.run(
                ["taskkill", "/F", "/T", "/PID", str(pid)], 
                stdout=subprocess.DEVNULL, 
                stderr=subprocess.DEVNULL
            )
        except Exception:
            pass
    try:
        parent = psutil.Process(pid)
        children = parent.children(recursive=True)
        for child in children:
            try: child.kill()
            except psutil.NoSuchProcess: pass
        try: parent.kill()
        except psutil.NoSuchProcess: pass
        if logger:
            logger.info("Process terminated.")
    except psutil.NoSuchProcess:
        if logger:
            logger.warning("Process not found, might have already finished.")
    except Exception as e:
        if logger:
            logger.error(f"psutil failed to kill process tree: {e}.")

def check_disk_space(path: str, required_gb: float) -> bool:
    try:
        target = path
        if not os.path.exists(target):
            target = os.path.dirname(os.path.abspath(target))
        usage = psutil.disk_usage(target)
        free_gb = usage.free / (1024**3)
        return free_gb >= required_gb
    except Exception:
        return True

def check_filter_option(ffmpeg_path: str, filter_name: str, option_name: str) -> bool:
    try:
        startupinfo = None
        if sys.platform == "win32":
            startupinfo = subprocess.STARTUPINFO()
            startupinfo.dwFlags |= subprocess.STARTF_USESHOWWINDOW
            startupinfo.wShowWindow = subprocess.SW_HIDE
        result = subprocess.run(
            [ffmpeg_path, '-h', f'filter={filter_name}'],
            capture_output=True,
            text=True,
            check=True,
            startupinfo=startupinfo,
            creationflags=(subprocess.CREATE_NO_WINDOW if sys.platform == "win32" else 0),
            timeout=5
        )
        return option_name.lower() in result.stdout.lower()
    except Exception:
        return False

def monitor_ffmpeg_progress(proc, duration_sec, progress_signal, check_disk_space_callback, logger, on_error_line=None):
    last_poll_time = time.time()
    critical_signatures = (
        "error",
        "failed",
        "option not found",
        "invalid data found",
        "non-monotonous dts",
        "moov atom not found",
        "corrupt",
        "decode slice",
        "error while decoding",
        "timestamp",
        "audio sync",
        "queue input is backward in time",
        "application provided invalid",
    )
    while True:
        current_time = time.time()
        if current_time - last_poll_time > 0.05:
            if check_disk_space_callback and check_disk_space_callback():
                logger.warning("MONITOR: Disk full or Cancellation detected. Terminating FFmpeg.")
                kill_process_tree(proc.pid, logger)
                break
            last_poll_time = current_time
        line = proc.stdout.readline()
        if not line:
            if proc.poll() is not None:
                break
            continue
        s = line.strip()
        if not s:
            continue
        low = s.lower()
        if on_error_line and any(sig in low for sig in critical_signatures):
            on_error_line(s)
        if '=' in s:
            key, _, val = s.partition('=')
            key = key.strip()
            val = val.strip()
            if key == 'out_time_us':
                try:
                    us = int(val)
                    current_seconds = us / 1000000.0
                    if duration_sec > 0:
                        percent = current_seconds / float(duration_sec)
                        calc_prog = int(max(0, min(100, percent * 100)))
                        progress_signal.emit(calc_prog)
                except ValueError:
                    pass
            elif key == 'error':
                 logger.error(f"FFmpeg reported error: {val}")
        else:
            if any(sig in low for sig in critical_signatures):
                logger.error(f"FFmpeg Output: {s}")
