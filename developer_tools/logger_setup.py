import sys
import os
sys.dont_write_bytecode = True
os.environ['PYTHONDONTWRITEBYTECODE'] = '1'
os.environ['PYTHONPYCACHEPREFIX'] = os.path.join(os.path.expanduser('~'), '.null_cache_dir')

import sys
import os
import logging
from logging.handlers import RotatingFileHandler
current_dir = os.path.abspath(os.path.dirname(__file__))
project_root = current_dir
if project_root not in sys.path:
    sys.path.insert(0, project_root)
parent_dir = os.path.abspath(os.path.join(current_dir, '..'))
if parent_dir not in sys.path:
    sys.path.insert(0, parent_dir)

from enhanced_logger import EnhancedCropLogger

class StreamToLogger(object):
    """
    Fake file-like stream object that redirects writes to a logger instance.
    """

    def __init__(self, logger, level):
        self.logger = logger
        self.level = level
        self.linebuf = ''
        self.processing = False

    def write(self, buf):
        if self.processing:
            return
        buf = buf.strip()
        if not buf:
            return
        self.processing = True
        try:
            for line in buf.splitlines():
                if line.strip():
                    self.logger.log(self.level, line.rstrip())
        except Exception:
            pass
        finally:
            self.processing = False

    def flush(self):
        pass
    
    def fileno(self):
        """
        Return a file descriptor for compatibility with libraries that expect it.
        Returns 1 for stdout-like streams, 2 for stderr-like streams.
        """
        if self.level == logging.INFO:
            return 1
        else:
            return 2

import threading

class SafeStreamHandler(logging.StreamHandler):
    """
    A StreamHandler that suppresses OSError during flush and emit, 
    common when dealing with Windows pipes or detached processes.
    """

    def emit(self, record):
        try:
            if self.stream and not getattr(self.stream, 'closed', False):
                if getattr(self.stream, 'processing', False):
                    return
                super().emit(record)
        except (OSError, ValueError, RecursionError):
            pass
        except Exception:
            pass

    def flush(self):
        try:
            if self.stream and hasattr(self.stream, "flush") and not getattr(self.stream, 'closed', False):
                self.stream.flush()
        except (OSError, ValueError):
            pass
        except Exception:
            pass

    def handleError(self, record):
        """Quietly ignore errors during logging to prevent infinite loops or console clutter."""
        pass

class AggressiveFileHandler(RotatingFileHandler):
    """
    Ensures logs are written to disk AND the file handle is released 
    after every entry, allowing other processes to read/lock the file on Windows.
    Uses an internal lock to prevent race conditions during close/open.
    """

    def __init__(self, *args, **kwargs):
        super().__init__(*args, **kwargs)
        self._aggressive_lock = threading.Lock()

    def emit(self, record):
        with self._aggressive_lock:
            try:
                if self.shouldRollover(record):
                    self.doRollover()
                if self.stream is None or getattr(self.stream, 'closed', False):
                    self.stream = self._open()
                msg = self.format(record)
                self.stream.write(msg + self.terminator)
                self.flush()
                try:
                    os.fsync(self.stream.fileno())
                except (OSError, ValueError):
                    pass
                self.stream.close()
                self.stream = None
            except Exception:
                self.handleError(record)

def setup_logger():
    """
    Initializes the shared base logger and redirects stdout/stderr to it.
    Configures BOTH the specific "Crop_Tools_Base" logger and the root logger 
    to use the AggressiveFileHandler for consistent, non-buffered logging.
    """
    original_stdout = sys.stdout
    original_stderr = sys.stderr
    log_file_path = os.path.join(parent_dir, 'logs', "crop_tools.log")
    os.makedirs(os.path.dirname(log_file_path), exist_ok=True)
    file_handler = AggressiveFileHandler(log_file_path, maxBytes=10*1024*1024, backupCount=5, encoding='utf-8')
    formatter = logging.Formatter('%(asctime)s | %(name)-12s | %(levelname)-8s | %(message)s')
    file_handler.setFormatter(formatter)
    root_logger = logging.getLogger()
    root_logger.setLevel(logging.INFO)
    for handler in root_logger.handlers[:]:
        root_logger.removeHandler(handler)
    root_logger.addHandler(file_handler)
    base_logger = logging.getLogger("Crop_Tools_Base")
    base_logger.setLevel(logging.INFO)
    for handler in base_logger.handlers[:]:
        base_logger.removeHandler(handler)
    base_logger.addHandler(file_handler)
    base_logger.propagate = False
    if original_stdout:
        console_handler = SafeStreamHandler(original_stdout)
        console_handler.setFormatter(formatter)
        base_logger.addHandler(console_handler)
        root_logger.addHandler(console_handler)
    sys.stdout = StreamToLogger(base_logger, logging.INFO)
    sys.stderr = StreamToLogger(base_logger, logging.ERROR)
    enhanced_logger_instance = EnhancedCropLogger(base_logger)
    return enhanced_logger_instance
