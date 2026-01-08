import logging
import os
from logging.handlers import RotatingFileHandler

def setup_logger(base_dir, log_filename, logger_name):
    """
    Configures and returns a logger with a rotating file handler.
    """
    logger = logging.getLogger(logger_name)
    if logger.handlers:
        return logger
    logger.setLevel(logging.INFO)
    log_dir = os.path.join(base_dir, "logs")
    os.makedirs(log_dir, exist_ok=True)
    log_path = os.path.join(log_dir, log_filename)
    handler = RotatingFileHandler(log_path, maxBytes=10 * 1024 * 1024, backupCount=5, encoding="utf-8")
    fmt = logging.Formatter("%(asctime)s | %(name)-12s | %(levelname)-8s | %(message)s", datefmt="%Y-%m-%d %H:%M:%S")
    handler.setFormatter(fmt)
    logger.addHandler(handler)
    logging.addLevelName(logging.CRITICAL, "FATAL")
    logging.captureWarnings(True)
    return logger