"""
Unified Processing Module Interface
Provides a clean, abstracted API for video processing operations.
"""

import os
import sys
import tempfile
import shutil

# ---------------------------------------------------------------------------
# Environment Setup (Clean Python)
# ---------------------------------------------------------------------------
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
            except Exception:
                pass
        for file in files:
            if file.endswith('.pyc') or file.endswith('.pyo'):
                try:
                    os.remove(os.path.join(root, file))
                except Exception:
                    pass

cleanup_pycache()

from typing import Dict, Any, Optional, List
from PyQt5.QtCore import QObject, pyqtSignal

# ---------------------------------------------------------------------------
# Module Imports
# ---------------------------------------------------------------------------
from .config_data import VideoConfig
from .media_utils import MediaProber
from .filter_builder import FilterBuilder
from .encoders import EncoderManager
from .text_ops import TextWrapper, fix_hebrew_text, apply_bidi_formatting
from .system_utils import create_subprocess, monitor_ffmpeg_progress, kill_process_tree
from .step_intro import IntroProcessor
from .step_concat import ConcatProcessor
from .worker import ProcessThread

# Optional / Feature Flag Imports
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
    VALIDATION_SYSTEM_AVAILABLE = False


# ---------------------------------------------------------------------------
# Data Structures
# ---------------------------------------------------------------------------

class ProcessingJob:
    """Represents a video processing job with all configuration."""
    
    def __init__(self, 
                 input_path: str,
                 start_time: float,
                 end_time: float,
                 original_resolution: str,
                 is_mobile_format: bool = False,
                 speed_factor: float = 1.0,
                 quality_level: int = 2,
                 bg_music_path: Optional[str] = None,
                 bg_music_volume: Optional[float] = None,
                 bg_music_offset: float = 0.0,
                 portrait_text: Optional[str] = None,
                 is_boss_hp: bool = False,
                 show_teammates_overlay: bool = False,
                 disable_fades: bool = False,
                 intro_still_sec: float = 0.0,
                 intro_from_midpoint: bool = False,
                 intro_abs_time: Optional[float] = None,
                 original_total_duration: float = 0.0,
                 music_config: Optional[Dict[str, Any]] = None,
                 speed_segments: Optional[List[Dict[str, Any]]] = None): # [ADDED] Missing param
        
        self.input_path = input_path
        self.start_time = float(start_time)
        self.end_time = float(end_time)
        self.original_resolution = original_resolution
        self.is_mobile_format = is_mobile_format
        self.speed_factor = float(speed_factor)
        self.quality_level = quality_level
        self.bg_music_path = bg_music_path
        self.bg_music_volume = bg_music_volume
        self.bg_music_offset = float(bg_music_offset or 0.0)
        self.portrait_text = portrait_text
        self.is_boss_hp = is_boss_hp
        self.show_teammates_overlay = show_teammates_overlay
        self.disable_fades = disable_fades
        self.intro_still_sec = float(intro_still_sec or 0.0)
        self.intro_from_midpoint = intro_from_midpoint
        self.intro_abs_time = float(intro_abs_time) if intro_abs_time is not None else None
        self.original_total_duration = float(original_total_duration)
        self.music_config = music_config if music_config else {}
        self.speed_segments = speed_segments if speed_segments else [] # [ADDED]
        
        self.duration = self.end_time - self.start_time


class ProcessingResult:
    """Represents the result of a processing job."""
    
    def __init__(self, 
                 success: bool,
                 output_path: Optional[str] = None,
                 error_message: Optional[str] = None,
                 processing_time: float = 0.0,
                 output_size: int = 0):
        
        self.success = success
        self.output_path = output_path
        self.error_message = error_message
        self.processing_time = processing_time
        self.output_size = output_size
        
        self.metadata: Dict[str, Any] = {}


# ---------------------------------------------------------------------------
# Manager Class
# ---------------------------------------------------------------------------

class ProcessingManager(QObject):
    """
    High-level manager for video processing operations.
    Provides a clean interface for starting, monitoring, and controlling jobs.
    """
    
    job_started = pyqtSignal(str)
    job_progress = pyqtSignal(str, int, str)
    job_completed = pyqtSignal(str, ProcessingResult)
    job_error = pyqtSignal(str, str)
    job_cancelled = pyqtSignal(str)
    
    def __init__(self, base_dir: str, logger=None):
        super().__init__()
        self.base_dir = base_dir
        self.bin_dir = os.path.join(base_dir, 'binaries')
        self.logger = logger
        self._jobs: Dict[str, ProcessThread] = {}
        self._job_results: Dict[str, ProcessingResult] = {}
        
        self.config = VideoConfig(base_dir)
        self.encoder_mgr = EncoderManager(logger)
        self.filter_builder = FilterBuilder(logger)
        
    def create_job(self, job: ProcessingJob, job_id: Optional[str] = None) -> str:
        """
        Create and start a processing job.
        """
        import time
        import uuid
        
        if job_id is None:
            job_id = f"job_{int(time.time())}_{uuid.uuid4().hex[:8]}"
        
        script_dir = os.path.dirname(os.path.abspath(__file__))
        
        def progress_handler(progress: int):
            status = self._get_status_for_progress(progress)
            self.job_progress.emit(job_id, progress, status)
        
        def status_handler(message: str):
            self.job_progress.emit(job_id, -1, message)
        
        def finished_handler(success: bool, output_path: str):
            result = ProcessingResult(
                success=success,
                output_path=output_path if success else None,
                error_message=None if success else "Processing failed"
            )
            self._job_results[job_id] = result
            if success:
                self.job_completed.emit(job_id, result)
            else:
                self.job_error.emit(job_id, result.error_message or "Unknown error")
            
            if job_id in self._jobs:
                del self._jobs[job_id]
        
        # Prepare arguments for the Worker Thread
        thread_kwargs = {
            'input_path': job.input_path,
            'start_time_ms': int(job.start_time * 1000),
            'end_time_ms': int(job.end_time * 1000),
            'original_resolution': job.original_resolution,
            'is_mobile_format': job.is_mobile_format,
            'speed_factor': job.speed_factor,
            'script_dir': script_dir,
            'progress_update_signal': progress_handler,
            'status_update_signal': status_handler,
            'finished_signal': finished_handler,
            'logger': self.logger,
            'is_boss_hp': job.is_boss_hp,
            'show_teammates_overlay': job.show_teammates_overlay,
            'quality_level': job.quality_level,
            'bg_music_path': job.bg_music_path,
            'bg_music_volume': job.bg_music_volume,
            'bg_music_offset_ms': int(job.bg_music_offset * 1000),
            'original_total_duration_ms': int(job.original_total_duration * 1000),
            'disable_fades': job.disable_fades,
            'intro_still_sec': job.intro_still_sec,
            'intro_from_midpoint': job.intro_from_midpoint,
            'music_config': job.music_config,
            'speed_segments': job.speed_segments # [ADDED] Pass granular speed segments
        }
        
        if job.intro_abs_time is not None:
            thread_kwargs['intro_abs_time_ms'] = int(job.intro_abs_time * 1000)
        
        if job.portrait_text is not None:
            thread_kwargs['portrait_text'] = job.portrait_text
        
        thread = ProcessThread(**thread_kwargs)
        
        self._jobs[job_id] = thread
        self.job_started.emit(job_id)
        thread.start()
        
        return job_id
    
    def cancel_job(self, job_id: str) -> bool:
        """Cancel a running job."""
        if job_id in self._jobs:
            thread = self._jobs[job_id]
            thread.cancel()
            self.job_cancelled.emit(job_id)
            
            from PyQt5.QtCore import QTimer
            QTimer.singleShot(1000, lambda: self._cleanup_job(job_id))
            return True
        return False
    
    def get_job_status(self, job_id: str) -> Optional[Dict[str, Any]]:
        """Get current status of a job."""
        if job_id in self._jobs:
            thread = self._jobs[job_id]
            return {
                "running": thread.isRunning(),
                "cancelled": thread.is_canceled,
                "progress": getattr(thread, 'last_progress', 0)
            }
        elif job_id in self._job_results:
            result = self._job_results[job_id]
            return {
                "completed": True,
                "success": result.success,
                "output_path": result.output_path,
                "error": result.error_message
            }
        return None
    
    def get_active_jobs(self) -> Dict[str, Dict[str, Any]]:
        """Get all active jobs."""
        return {
            job_id: self.get_job_status(job_id)
            for job_id in self._jobs.keys()
        }
    
    def _cleanup_job(self, job_id: str):
        """Clean up job resources."""
        if job_id in self._jobs:
            thread = self._jobs[job_id]
            if thread.isFinished():
                thread.deleteLater()
                del self._jobs[job_id]
    
    def _get_status_for_progress(self, progress: int) -> str:
        """Map progress percentage to status message."""
        if progress < 30:
            return "Preparing video processing..."
        elif progress < 60:
            return "Processing core video segment..."
        elif progress < 90:
            return "Creating intro segment..."
        elif progress < 100:
            return "Concatenating final video..."
        else:
            return "Processing complete!"
    
    def validate_job(self, job: ProcessingJob) -> Dict[str, Any]:
        """
        Validate a job configuration before processing.
        """
        validation_results = {
            "valid": True,
            "errors": [],
            "warnings": []
        }
        
        if not os.path.exists(job.input_path):
            validation_results["valid"] = False
            validation_results["errors"].append(f"Input file not found: {job.input_path}")
        
        if job.duration <= 0:
            validation_results["valid"] = False
            validation_results["errors"].append("Duration must be positive")
        
        try:
            if 'x' in job.original_resolution:
                w, h = map(int, job.original_resolution.split('x'))
                if w <= 0 or h <= 0:
                    validation_results["errors"].append("Invalid resolution dimensions")
        except ValueError:
            validation_results["warnings"].append("Could not parse resolution, using default")
        
        if job.speed_factor < 0.25 or job.speed_factor > 4.0:
            validation_results["warnings"].append(f"Speed factor {job.speed_factor} is outside recommended range (0.25-4.0)")
        
        if job.quality_level < 0 or job.quality_level > 3:
            validation_results["warnings"].append(f"Quality level {job.quality_level} is outside valid range (0-3)")

        if not isinstance(job.speed_segments, list):
            validation_results["valid"] = False
            validation_results["errors"].append("speed_segments must be a list of segment dictionaries")
        else:
            for idx, segment in enumerate(job.speed_segments):
                if not isinstance(segment, dict):
                    validation_results["valid"] = False
                    validation_results["errors"].append(f"speed_segments[{idx}] must be a dictionary")
                    continue

                missing_keys = [k for k in ("start_ms", "end_ms", "speed") if k not in segment]
                if missing_keys:
                    validation_results["valid"] = False
                    validation_results["errors"].append(
                        f"speed_segments[{idx}] missing required keys: {', '.join(missing_keys)}"
                    )
                    continue

                try:
                    seg_start = float(segment["start_ms"])
                    seg_end = float(segment["end_ms"])
                    seg_speed = float(segment["speed"])
                except (TypeError, ValueError):
                    validation_results["valid"] = False
                    validation_results["errors"].append(
                        f"speed_segments[{idx}] values must be numeric (start_ms, end_ms, speed)"
                    )
                    continue

                if seg_end <= seg_start:
                    validation_results["valid"] = False
                    validation_results["errors"].append(
                        f"speed_segments[{idx}] must have end_ms > start_ms"
                    )

                if seg_speed < 0.25 or seg_speed > 4.0:
                    validation_results["warnings"].append(
                        f"speed_segments[{idx}] speed {seg_speed} is outside recommended range (0.25-4.0)"
                    )

                # Segment values are expected to be relative to the selected clip timeline.
                # Keep this as warning to avoid blocking existing pipelines.
                if seg_start < 0 or seg_end > (job.duration * 1000.0):
                    validation_results["warnings"].append(
                        f"speed_segments[{idx}] lies outside selected clip duration ({job.duration * 1000.0:.0f} ms)"
                    )
        
        return validation_results


def create_processing_manager(base_dir: Optional[str] = None, logger=None):
    """
    Create a ProcessingManager instance.
    """
    if base_dir is None:
        import sys
        script_dir = os.path.dirname(os.path.abspath(__file__))
        base_dir = os.path.abspath(os.path.join(script_dir, '..'))
    
    return ProcessingManager(base_dir, logger)


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