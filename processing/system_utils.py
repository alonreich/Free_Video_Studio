import subprocess
import sys
import psutil
import re
import os

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
            total = (h * 3600) + (m * 60) + sec
            return float(total)
        if count == 2:
            m = int(parts[0])
            sec = float(parts[1])
            total = (m * 60) + sec
            return float(total)
        if count == 1:
            val = float(parts[0])
            return val
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
    return proc

def kill_process_tree(pid, logger=None):
    if not pid:
        return
    try:
        parent = psutil.Process(pid)
        children = parent.children(recursive=True)
        for child in children:
            child.kill()
        parent.kill()
        if logger:
            logger.info("Process terminated.")
    except psutil.NoSuchProcess:
        if logger:
            logger.warning("Process not found, might have already finished.")
    except Exception as e:
        if logger:
            logger.error(f"psutil failed to kill process tree: {e}. Attempting 'taskkill' fallback.")
        if sys.platform == "win32":
            subprocess.run(["taskkill", "/F", "/T", "/PID", str(pid)], 
                           stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)

def check_disk_space(path: str, required_gb: float) -> bool:
    try:
        usage = psutil.disk_usage(os.path.dirname(os.path.abspath(path)))
        free_gb = usage.free / (1024**3)
        return free_gb >= required_gb
    except Exception:
        return True

def monitor_ffmpeg_progress(proc, duration_sec, progress_signal, is_canceled_func, logger):
    time_regex = re.compile(r'time=(\S+)')
    while True:
        if is_canceled_func():
            break
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
            if key in ['error', 'speed']:
                logger.info(f"FFmpeg: {key}={val}")
        else:
            if "error" in s.lower():
                logger.error(s)
            match = time_regex.search(s)
            if match:
                current_time_str = match.group(1).split('.')[0]
                current_seconds = parse_time_to_seconds(current_time_str)
                if duration_sec > 0:
                    percent = current_seconds / float(duration_sec)
                    calc_prog = int(max(0, min(100, percent * 100)))
                    progress_signal.emit(calc_prog)