import json
from pathlib import Path
import sys
import os

# Enforce no-cache policy
sys.dont_write_bytecode = True
os.environ['PYTHONDONTWRITEBYTECODE'] = '1'

import traceback
import psutil
import time
import subprocess
import shutil
from typing import Optional, Callable, Any

def _proj_root() -> Path:
    return Path(__file__).resolve().parents[1]

def _conf_path() -> Path:
    return _proj_root() / "config" / "merger_app.conf"

def _human(n_bytes: int) -> str:
    """Returns human-readable size."""
    if n_bytes is None: return "0 B"
    units = ["B","KB","MB","GB","TB"]
    s = 0
    n = float(n_bytes)
    while n >= 1024.0 and s < len(units)-1:
        n /= 1024.0; s += 1
    return f"{n:.2f} {units[s]}"

def _get_logger():
    """Gets or sets up the logger safely."""
    project_root = str(Path(__file__).resolve().parents[1])
    if project_root not in sys.path:
        sys.path.insert(0, project_root)
    try:
        from system.logger import setup_logger as setup_main_logger
        return setup_main_logger(project_root, "Video_Merger.log", "Video_Merger")
    except ImportError:
        import logging
        logging.basicConfig(level=logging.INFO)
        return logging.getLogger("Video_Merger")

def _load_conf() -> dict:
    p = _conf_path()
    logger = _get_logger()
    try:
        if p.exists():
            content = p.read_text(encoding="utf-8")
            cfg = json.loads(content)
            return cfg
        return {}
    except Exception as e:
        logger.error(f"Failed to load config from {p}: {e}")
        return {}

def _save_conf(cfg: dict) -> None:
    """Atomic save configuration."""
    p = _conf_path()
    logger = _get_logger()
    try:
        p.parent.mkdir(parents=True, exist_ok=True)
        import tempfile
        with tempfile.NamedTemporaryFile(mode='w', encoding='utf-8', dir=str(p.parent), prefix=f"{p.name}.tmp.", delete=False) as tmp_file:
            tmp_path = tmp_file.name
            json.dump(cfg, tmp_file, indent=2)
            tmp_file.flush()
            os.fsync(tmp_file.fileno())
        if os.name == 'nt':
            if os.path.exists(p):
                os.replace(tmp_path, str(p))
            else:
                os.rename(tmp_path, str(p))
        else:
            os.replace(tmp_path, str(p))
    except Exception as e:
        logger.error(f"Failed to save config to {p}: {e}")
        if 'tmp_path' in locals() and os.path.exists(tmp_path):
            try: os.unlink(tmp_path)
            except: pass

def _ffprobe(ffmpeg_path) -> str:
    """Robustly find ffprobe."""
    try:
        ffmpeg_dir = Path(ffmpeg_path).parent
        for name in ("ffprobe", "ffprobe.exe"):
            p = ffmpeg_dir / name
            if p.exists():
                return str(p)
    except Exception:
        pass
    return "ffprobe" # System PATH fallback

def get_disk_free_space(path_str):
    """Returns free space in bytes for the drive containing path."""
    try:
        return shutil.disk_usage(os.path.dirname(os.path.abspath(path_str))).free
    except Exception:
        return 10**12 # Assume 1TB if check fails to avoid blocking

def escape_ffmpeg_path(path: str) -> str:
    """
    Robustly escapes paths for FFmpeg concat demuxer.
    Single quotes are the main enemy. 
    ' -> '\'' 
    """
    # Windows paths need forward slashes in concat files for safety
    p = path.replace('\\', '/')
    return p.replace("'", "'\\''")

def kill_process_tree(pid: int) -> None:
    """Kills a process tree safely."""
    try:
        if not pid or pid <= 0: return
        parent = psutil.Process(pid)
        for child in parent.children(recursive=True):
            try: child.kill()
            except: pass
        parent.kill()
    except: pass