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

    def write(self, buf):
        if not buf.strip():
            return
        for line in buf.rstrip().splitlines():
            if line:
                self.logger.log(self.level, line.rstrip())

    def flush(self):
        pass

def setup_logger():
    """
    Initializes the shared base logger and redirects stdout/stderr to it.
    Then, it sets up and returns the EnhancedCropLogger.
    """
    log_file_path = os.path.join(project_root, 'logs', "Crop_Tools.log")
    os.makedirs(os.path.dirname(log_file_path), exist_ok=True)
    base_logger = logging.getLogger("Crop_Tools_Base")
    base_logger.setLevel(logging.INFO)
    if base_logger.handlers:
        for handler in base_logger.handlers:
            base_logger.removeHandler(handler)
    file_handler = logging.FileHandler(log_file_path)
    formatter = logging.Formatter('%(asctime)s | %(name)-12s | %(levelname)-8s | %(message)s')
    file_handler.setFormatter(formatter)
    base_logger.addHandler(file_handler)
    console_handler = logging.StreamHandler(sys.stdout)
    console_handler.setFormatter(formatter)
    base_logger.addHandler(console_handler)
    sys.stdout = StreamToLogger(base_logger, logging.INFO)
    sys.stderr = StreamToLogger(base_logger, logging.ERROR)
    enhanced_logger_instance = EnhancedCropLogger(base_logger)
    return enhanced_logger_instance
