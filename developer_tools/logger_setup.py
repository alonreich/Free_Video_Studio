import sys
import os
import logging
current_dir = os.path.abspath(os.path.dirname(__file__))
project_root = current_dir
if project_root not in sys.path:
    sys.path.insert(0, project_root)
parent_dir = os.path.abspath(os.path.join(current_dir, '..'))
if parent_dir not in sys.path:
    sys.path.insert(0, parent_dir)

from system.logger import setup_logger as setup_main_logger

class StreamToLogger(object):
    """
    Fake file-like stream object that redirects writes to a logger instance.
    """

    def __init__(self, logger, level):
        self.logger = logger
        self.level = level
        self.linebuf = ''

    def write(self, buf):
        for line in buf.rstrip().splitlines():
            self.logger.log(self.level, line.rstrip())

    def flush(self):
        pass

def setup_logger():
    """
    Initializes the shared logger and redirects stdout/stderr to it
    to capture 'hard' crashes and console output.
    """
    logger = setup_main_logger(project_root, "Crop_Tools.log", "Crop_Tools")
    sys.stdout = StreamToLogger(logger, logging.INFO)
    sys.stderr = StreamToLogger(logger, logging.ERROR)
    return logger
