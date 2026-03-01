import os
import sys
import tempfile
import shutil
from typing import Dict, Any, Optional, List
from PyQt5.QtCore import QObject, pyqtSignal

os.environ['PYTHONDONTWRITEBYTECODE'] = '1'
os.environ['PYTHONPYCACHEPREFIX'] = os.path.join(tempfile.gettempdir(), 'pycache_disabled')
sys.dont_write_bytecode = True

def cleanup_pycache():
    current_dir = os.path.dirname(os.path.abspath(__file__))
    for root, dirs, files in os.walk(current_dir):
        if '__pycache__' in dirs:
            try: shutil.rmtree(os.path.join(root, '__pycache__'))
            except Exception: pass
        for file in files:
            if file.endswith('.pyc') or file.endswith('.pyo'):
                try: os.remove(os.path.join(root, file))
                except Exception: pass

cleanup_pycache()

from .config_data import VideoConfig; from .media_utils import MediaProber; from .filter_builder import FilterBuilder
from .encoders import EncoderManager; from .text_ops import TextWrapper, fix_hebrew_text, apply_bidi_formatting
from .system_utils import create_subprocess, monitor_ffmpeg_progress, kill_process_tree
from .step_intro import IntroProcessor; from .step_concat import ConcatProcessor
from .worker import ProcessThread; from .processing_models import ProcessingJob, ProcessingResult
from .manager import ProcessingManager, create_processing_manager

try:
    from state_manager import get_state_manager, OperationType, with_transaction
    STATE_MANAGER_AVAILABLE = True
except ImportError:
    STATE_MANAGER_AVAILABLE = False

try:
    from resource_manager import get_resource_manager
    RESOURCE_MANAGER_AVAILABLE = True
except ImportError:
    RESOURCE_MANAGER_AVAILABLE = False

try:
    from validation_system import ValidationLevel, ValidationFeedback
    VALIDATION_SYSTEM_AVAILABLE = True
except ImportError:
    VALIDATION_SYSTEM_AVAILABLE = True

__all__ = [
    'ProcessingJob',
    'ProcessingResult',
    'ProcessingManager',
    'create_processing_manager',
    'VideoConfig',
    'MediaProber',
    'FilterBuilder',
    'EncoderManager',
    'TextWrapper',
    'ProcessThread',
    'IntroProcessor',
    'ConcatProcessor'
]
