import sys
import os
import logging
from enhanced_logger import EnhancedCropLogger
current_dir = os.path.abspath(os.path.dirname(__file__))
project_root = current_dir
if project_root not in sys.path:
    sys.path.insert(0, project_root)
parent_dir = os.path.abspath(os.path.join(current_dir, '..'))
if parent_dir not in sys.path:
    sys.path.insert(0, parent_dir)

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
        if not buf.strip():
            return
        self.processing = True
        try:
            for line in buf.rstrip().splitlines():
                if line:
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

class SafeStreamHandler(logging.StreamHandler):
    """
    A StreamHandler that suppresses OSError during flush, 
    common when dealing with Windows pipes or detached processes.
    """

    def flush(self):
        try:
            if self.stream and hasattr(self.stream, "flush"):
                self.stream.flush()
        except OSError:
            pass
        except Exception:
            pass

def setup_logger():
    """
    Initializes the shared base logger and redirects stdout/stderr to it.
    Then, it sets up and returns the EnhancedCropLogger.
    """
    original_stdout = sys.stdout
    original_stderr = sys.stderr
    log_file_path = os.path.join(parent_dir, 'logs', "crop_tools.log")
    os.makedirs(os.path.dirname(log_file_path), exist_ok=True)
    base_logger = logging.getLogger("Crop_Tools_Base")
    base_logger.setLevel(logging.INFO)
    if base_logger.handlers:
        for handler in base_logger.handlers:
            base_logger.removeHandler(handler)
    
    from logging.handlers import RotatingFileHandler
    file_handler = RotatingFileHandler(log_file_path, maxBytes=10*1024*1024, backupCount=5, encoding='utf-8')
    formatter = logging.Formatter('%(asctime)s | %(name)-12s | %(levelname)-8s | %(message)s')
    file_handler.setFormatter(formatter)
    base_logger.addHandler(file_handler)
    if original_stdout:
        console_handler = SafeStreamHandler(original_stdout)
        console_handler.setFormatter(formatter)
        base_logger.addHandler(console_handler)
    sys.stdout = StreamToLogger(base_logger, logging.INFO)
    sys.stderr = StreamToLogger(base_logger, logging.ERROR)
    enhanced_logger_instance = EnhancedCropLogger(base_logger)
    return enhanced_logger_instance
