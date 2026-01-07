import json
from pathlib import Path
import sys
import os

def _proj_root() -> Path:
    return Path(__file__).resolve().parents[1]

def _conf_path() -> Path:
    return _proj_root() / "config" / "main_app.conf"

def _human(n_bytes: int) -> str:
    units = ["B","KB","MB","GB","TB"]
    s = 0
    n = float(n_bytes)
    while n >= 1024.0 and s < len(units)-1:
        n /= 1024.0; s += 1
    return f"{n:.2f} {units[s]}"

def _get_logger():
    """
    Initializes the shared logger and returns a specific logger for the
    Video Merger, ensuring its messages are identifiable.
    """
    project_root = str(Path(__file__).resolve().parents[1])
    if project_root not in sys.path:
        sys.path.insert(0, project_root)
    from system.logger import setup_logger as setup_main_logger
    return setup_main_logger(project_root, "Video_Merger.log", "Video_Merger")

def _load_conf() -> dict:
    p = _conf_path()
    try:
        return json.loads(p.read_text(encoding="utf-8"))
    except Exception:
        return {}

def _save_conf(cfg: dict) -> None:
    p = _conf_path()
    try:
        p.parent.mkdir(parents=True, exist_ok=True)
        p.write_text(json.dumps(cfg, indent=2), encoding="utf-8")
    except Exception:
        pass

def _mp3_dir() -> Path:
    """Return the absolute path to the project's central MP3 folder."""
    d = _proj_root() / "mp3"
    try:
        d.mkdir(exist_ok=True)
    except Exception:
        pass
    return d

def _ffprobe(ffmpeg_path) -> str:
    """Gets the path to the ffprobe executable, assuming it's next to ffmpeg."""
    try:
        ffmpeg_dir = Path(ffmpeg_path).parent
        for name in ("ffprobe", "ffprobe.exe"):
            p = ffmpeg_dir / name
            if p.exists():
                return str(p)
    except Exception:
        pass
    return "ffprobe"
