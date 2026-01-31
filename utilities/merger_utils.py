import json
from pathlib import Path
import sys
import os
import traceback
from typing import Optional, Callable, Any

def _proj_root() -> Path:
    return Path(__file__).resolve().parents[1]

def _conf_path() -> Path:
    return _proj_root() / "config" / "merger_app.conf"

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
    logger = _get_logger()
    try:
        if p.exists():
            content = p.read_text(encoding="utf-8")
            cfg = json.loads(content)
            logger.info(f"Loaded config from {p}: {cfg}")
            return cfg
        else:
            logger.info(f"Config file not found: {p}")
            return {}
    except Exception as e:
        logger.error(f"Failed to load config from {p}: {e}")
        return {}

def _save_conf(cfg: dict) -> None:
    """
    Save configuration with atomic write to prevent corruption.
    Uses write-to-temp-then-rename pattern for atomicity.
    """
    p = _conf_path()
    logger = _get_logger()
    try:
        p.parent.mkdir(parents=True, exist_ok=True)

        import tempfile
        import os
        with tempfile.NamedTemporaryFile(
            mode='w',
            encoding='utf-8',
            dir=str(p.parent),
            prefix=f"{p.name}.tmp.",
            delete=False
        ) as tmp_file:
            tmp_path = tmp_file.name
            json.dump(cfg, tmp_file, indent=2)
            tmp_file.flush()
            os.fsync(tmp_file.fileno())
        if os.name == 'nt':
            import ctypes
            ctypes.windll.kernel32.MoveFileExW(
                str(tmp_path),
                str(p),
                0x00000001
            )
        else:
            os.replace(tmp_path, str(p))
        logger.info(f"Saved config to {p}: {cfg}")
    except Exception as e:
        logger.error(f"Failed to save config to {p}: {e}")
        try:
            if 'tmp_path' in locals() and os.path.exists(tmp_path):
                os.unlink(tmp_path)
        except Exception:
            pass

def _mp3_dir() -> Path:
    """Return the absolute path to the project's central MP3 folder."""
    d = _proj_root() / "mp3"
    try:
        d.mkdir(exist_ok=True)
    except (OSError, PermissionError) as e:
        logger = _get_logger()
        logger.warning(f"Could not create MP3 directory {d}: {e}")
    return d

def _ffprobe(ffmpeg_path) -> str:
    """Gets the path to the ffprobe executable, assuming it's next to ffmpeg."""
    try:
        ffmpeg_dir = Path(ffmpeg_path).parent
        for name in ("ffprobe", "ffprobe.exe"):
            p = ffmpeg_dir / name
            if p.exists():
                return str(p)
    except Exception as e:
        logger = _get_logger()
        logger.debug(f"ffprobe detection failed: {e}")
    return "ffprobe"

def safe_execute(func: Callable, *args, default: Any = None, 
                 log_error: bool = True, raise_error: bool = False, 
                 error_message: Optional[str] = None) -> Any:
    """
    Safely execute a function with proper error handling.
    Args:
        func: Function to execute
        *args: Arguments to pass to function
        default: Default value to return on error
        log_error: Whether to log the error
        raise_error: Whether to re-raise the error after logging
        error_message: Custom error message prefix
    Returns:
        Function result or default value on error
    """
    try:
        return func(*args)
    except Exception as e:
        if log_error:
            logger = _get_logger()
            msg = error_message or f"Error in {func.__name__}"
            logger.error(f"{msg}: {e}\n{traceback.format_exc()}")
        if raise_error:
            raise
        return default

def safe_property(default: Any = None, log_error: bool = True):
    """
    Decorator for safely accessing properties that might fail.
    Args:
        default: Default value to return on error
        log_error: Whether to log errors
    """

    def decorator(func):
        def wrapper(*args, **kwargs):
            return safe_execute(func, *args, default=default, 
                               log_error=log_error, raise_error=False)
        return wrapper
    return decorator

def validate_range(value: float, min_val: float, max_val: float, 
                  default: float, name: str = "value") -> float:
    """
    Validate that a value is within specified range.
    Args:
        value: Value to validate
        min_val: Minimum allowed value
        max_val: Maximum allowed value
        default: Default value if validation fails
        name: Name of the value for error messages
    Returns:
        Validated value or default
    """
    if min_val <= value <= max_val:
        return value
    else:
        logger = _get_logger()
        logger.warning(f"{name} {value} out of range [{min_val}, {max_val}], using default {default}")
        return default
