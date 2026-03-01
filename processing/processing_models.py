import os
from typing import Dict, Any, Optional, List

class ProcessingJob:
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
                 speed_segments: Optional[List[Dict[str, Any]]] = None):
        self.input_path = input_path
        self.start_time = float(start_time); self.end_time = float(end_time); self.original_resolution = original_resolution
        self.is_mobile_format = is_mobile_format; self.speed_factor = float(speed_factor); self.quality_level = quality_level
        self.bg_music_path = bg_music_path; self.bg_music_volume = bg_music_volume; self.bg_music_offset = float(bg_music_offset or 0.0)
        self.portrait_text = portrait_text; self.is_boss_hp = is_boss_hp; self.show_teammates_overlay = show_teammates_overlay
        self.disable_fades = disable_fades; self.intro_still_sec = float(intro_still_sec or 0.0); self.intro_from_midpoint = intro_from_midpoint
        self.intro_abs_time = float(intro_abs_time) if intro_abs_time is not None else None
        self.original_total_duration = float(original_total_duration); self.music_config = music_config if music_config else {}
        self.speed_segments = speed_segments if speed_segments else []; self.duration = self.end_time - self.start_time

class ProcessingResult:
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

def validate_job(job: ProcessingJob) -> Dict[str, Any]:
    validation_results = {"valid": True, "errors": [], "warnings": []}
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
        validation_results["warnings"].append(f"Speed factor {job.speed_factor} is outside recommended range")
    if job.quality_level < 0 or job.quality_level > 3:
        validation_results["warnings"].append(f"Quality level {job.quality_level} is outside valid range")
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
                validation_results["errors"].append(f"speed_segments[{idx}] missing keys: {', '.join(missing_keys)}")
                continue
            try:
                seg_start = float(segment["start_ms"])
                seg_end = float(segment["end_ms"])
                seg_speed = float(segment["speed"])
            except (TypeError, ValueError):
                validation_results["valid"] = False
                validation_results["errors"].append(f"speed_segments[{idx}] values must be numeric")
                continue
            if seg_end <= seg_start:
                validation_results["valid"] = False
                validation_results["errors"].append(f"speed_segments[{idx}] must have end_ms > start_ms")
            if seg_speed < 0.25 or seg_speed > 4.0:
                validation_results["warnings"].append(f"speed_segments[{idx}] speed {seg_speed} is outside recommended range")
            if seg_start < 0 or seg_end > (job.duration * 1000.0):
                validation_results["warnings"].append(f"speed_segments[{idx}] lies outside selected clip duration")
    return validation_results
