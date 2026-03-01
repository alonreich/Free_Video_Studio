import os
from typing import Dict, Any, Optional
try:
    from PyQt5.QtCore import QObject, pyqtSignal, QTimer, Qt, QRect
    from PyQt5.QtGui import QImage, QPainter, QFont, QColor
    HAS_GUI = True
except ImportError:
    HAS_GUI = False

from .processing_models import ProcessingJob, ProcessingResult, validate_job
from .processing_utils import ProgressScaler, generate_text_overlay_png
from .config_data import VideoConfig
from .encoders import EncoderManager
from .filter_builder import FilterBuilder
from .worker import ProcessThread

class ProcessingManager(QObject):
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
            res = ProcessingResult(
                success=success, 
                output_path=output_path if success else None, 
                error_message=None if success else "Processing failed",
                processing_time=0.0,
                output_size=0
            )
            self._job_results[job_id] = res
            if success:
                self.job_completed.emit(job_id, res)
            else:
                self.job_error.emit(job_id, res.error_message or "Unknown error")
            if job_id in self._jobs:
                del self._jobs[job_id]
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
            'speed_segments': job.speed_segments
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
        if job_id in self._jobs:
            thread = self._jobs[job_id]; thread.cancel()
            self.job_cancelled.emit(job_id)
            QTimer.singleShot(1000, lambda: self._cleanup_job(job_id))
            return True
        return False
    
    def get_job_status(self, job_id: str) -> Optional[Dict[str, Any]]:
        if job_id in self._jobs:
            thread = self._jobs[job_id]
            return {"running": thread.isRunning(), "cancelled": thread.is_canceled, "progress": getattr(thread, 'last_progress', 0)}
        elif job_id in self._job_results:
            result = self._job_results[job_id]
            return {
                "completed": True, "success": result.success,
                "output_path": result.output_path, "error": result.error_message
            }
        return None
    
    def get_active_jobs(self) -> Dict[str, Dict[str, Any]]:
        return {job_id: self.get_job_status(job_id) for job_id in self._jobs.keys()}
    
    def _cleanup_job(self, job_id: str):
        if job_id in self._jobs:
            thread = self._jobs[job_id]
            if thread.isFinished(): thread.deleteLater(); del self._jobs[job_id]
    
    def _get_status_for_progress(self, progress: int) -> str:
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
        return validate_job(job)

def create_processing_manager(base_dir: Optional[str] = None, logger=None):
    if base_dir is None:
        import sys
        script_dir = os.path.dirname(os.path.abspath(__file__))
        base_dir = os.path.abspath(os.path.join(script_dir, '..'))
    return ProcessingManager(base_dir, logger)
