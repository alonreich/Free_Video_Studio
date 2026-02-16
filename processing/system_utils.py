import subprocess
import sys
import psutil
import re
import os
import time

def parse_time_to_seconds(time_str: str) -> float:
    """Parses HH:MM:SS.ms or MM:SS.ms string to total seconds."""
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
    """Creates a subprocess with proper flags to hide the console window on Windows."""
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
    return proc

def kill_process_tree(pid, logger=None):
    """Terminates a process and all its children/descendants."""
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
    """Checks if the drive containing 'path' has at least 'required_gb' free."""
    try:
        target = path
        if not os.path.exists(target):
            target = os.path.dirname(os.path.abspath(target))
        usage = psutil.disk_usage(target)
        free_gb = usage.free / (1024**3)
        return free_gb >= required_gb
    except Exception:
        return True

def monitor_ffmpeg_progress(proc, duration_sec, progress_signal, check_disk_space_callback, logger):
    """
    Monitors FFmpeg stdout for progress stats and handles cancellation/disk checks.
    """
    last_poll_time = time.time()
    while True:
        current_time = time.time()
        if current_time - last_poll_time > 0.5:
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
            if "error" in s.lower() or "failed" in s.lower():
                logger.error(f"FFmpeg Output: {s}")