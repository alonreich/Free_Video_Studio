"""
Developer Tools Package
Prevents __pycache__ creation and cleans up existing cache.
"""

import os
import sys
import tempfile
import shutil

os.environ['PYTHONDONTWRITEBYTECODE'] = '1'
os.environ['PYTHONPYCACHEPREFIX'] = os.path.join(tempfile.gettempdir(), 'pycache_disabled')
sys.dont_write_bytecode = True

def cleanup_pycache():
    """Remove any __pycache__ directories in the current directory and subdirectories."""
    current_dir = os.path.dirname(os.path.abspath(__file__))
    for root, dirs, files in os.walk(current_dir):
        if '__pycache__' in dirs:
            cache_dir = os.path.join(root, '__pycache__')
            try:
                shutil.rmtree(cache_dir)
                print(f"Cleaned up: {cache_dir}")
            except Exception as e:
                print(f"Failed to remove {cache_dir}: {e}")
        for file in files:
            if file.endswith('.pyc') or file.endswith('.pyo'):
                try:
                    os.remove(os.path.join(root, file))
                    print(f"Removed: {os.path.join(root, file)}")
                except Exception as e:
                    print(f"Failed to remove {file}: {e}")

cleanup_pycache()

from .config import *
from .utils import *
from .coordinate_math import *
from .config_manager import *
from .state_manager import *
from .enhanced_logger import *
from .validation_system import *
from .resource_manager import *
from .graphics_items import *
from .crop_widgets import *
from .crop_tools import *
from .magic_wand import *
from .media_processor import *
from .portrait_window import *
from .app_handlers import *
from .ui_setup import *
from .logger_setup import *
from .guidance_text import *
from .Keyboard_Mixing import *
from .Find_and_Remove_Comments import *

__all__ = [
    'config',
    'utils',
    'coordinate_math',
    'config_manager',
    'state_manager',
    'enhanced_logger',
    'validation_system',
    'resource_manager',
    'graphics_items',
    'crop_widgets',
    'crop_tools',
    'magic_wand',
    'media_processor',
    'portrait_window',
    'app_handlers',
    'ui_setup',
    'logger_setup',
    'guidance_text',
    'Keyboard_Mixing',
    'Find_and_Remove_Comments',
]