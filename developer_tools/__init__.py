"""
Developer Tools Package
Prevents __pycache__ creation and cleans up existing cache.
"""

import os
import sys
import tempfile
import shutil
import atexit

# Prevent __pycache__ creation completely
os.environ['PYTHONDONTWRITEBYTECODE'] = '1'
os.environ['PYTHONPYCACHEPREFIX'] = os.devnull  # Use null device to prevent any cache creation
sys.dont_write_bytecode = True

def cleanup_pycache():
    """Remove any __pycache__ directories in the current directory and subdirectories."""
    current_dir = os.path.dirname(os.path.abspath(__file__))
    parent_dir = os.path.dirname(current_dir)
    
    # Clean both developer_tools and processing directories
    for base_dir in [current_dir, os.path.join(parent_dir, 'processing')]:
        if not os.path.exists(base_dir):
            continue
            
        for root, dirs, files in os.walk(base_dir):
            if '__pycache__' in dirs:
                cache_dir = os.path.join(root, '__pycache__')
                try:
                    shutil.rmtree(cache_dir, ignore_errors=True)
                    # print(f"Cleaned up: {cache_dir}")
                except Exception:
                    pass
            for file in files:
                if file.endswith('.pyc') or file.endswith('.pyo'):
                    try:
                        os.remove(os.path.join(root, file))
                        # print(f"Removed: {os.path.join(root, file)}")
                    except Exception:
                        pass

def cleanup_temp_files():
    """Clean up temporary files from temp directory."""
    temp_dir = tempfile.gettempdir()
    
    # Patterns to match temporary files created by this application
    patterns = [
        'fortnite_', 'snapshot_', 'vlc_', '.vlclog',
        'tmp', 'temp_', '_temp'
    ]
    
    try:
        for filename in os.listdir(temp_dir):
            file_path = os.path.join(temp_dir, filename)
            try:
                # Check if file matches any pattern or is a temporary file
                if any(filename.startswith(pattern) or filename.endswith(pattern) for pattern in patterns):
                    if os.path.isfile(file_path):
                        os.unlink(file_path)
                        # print(f"Cleaned temp file: {file_path}")
            except Exception:
                pass
    except Exception:
        pass

# Register cleanup functions to run at exit
atexit.register(cleanup_pycache)
atexit.register(cleanup_temp_files)

# Also clean up immediately on import
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
from .portrait_view import *
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
    'portrait_view',
    'app_handlers',
    'ui_setup',
    'logger_setup',
    'guidance_text',
    'Keyboard_Mixing',
    'Find_and_Remove_Comments',
]
