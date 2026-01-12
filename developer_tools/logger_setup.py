import sys
import os
import logging
project_root = os.path.abspath(os.path.join(os.path.dirname(__file__), '..'))
if project_root not in sys.path:
    sys.path.insert(0, project_root)

from system.logger import setup_logger as setup_main_logger

def setup_logger():
    """
    Initializes the shared logger and returns a specific logger for the
    Crop Tool, ensuring its messages are identifiable.
    """
    return setup_main_logger(project_root, "Crop_Tools.log", "Crop_Tools")