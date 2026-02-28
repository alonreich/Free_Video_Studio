import os
from typing import Any, Optional

class EncoderManager:
    """
    Manages encoder selection, settings, and dynamic fallback strategies.
    """
    ENCODER_PREFERENCE = ["h264_nvenc", "h264_amf", "h264_qsv", "libx264"]

    def _fps_to_float(self, fps_expr: str, default: float = 60.0) -> float:
        try:
            if fps_expr and '/' in str(fps_expr):
                n, d = str(fps_expr).split('/', 1)
                d_f = float(d)
                if d_f <= 0.0:
                    return float(default)
                return float(n) / d_f
            return float(fps_expr)
        except Exception:
            return float(default)

    def __init__(self, logger: Any, hardware_strategy: Optional[str] = None):
        self.logger = logger
        self.primary_encoder = os.environ.get('VIDEO_HW_ENCODER', 'h264_nvenc')
        self.forced_cpu = (os.environ.get('VIDEO_FORCE_CPU') == '1')
        if hardware_strategy:
            if hardware_strategy == "NVIDIA":
                self.primary_encoder = "h264_nvenc"
                self.forced_cpu = False
            elif hardware_strategy == "AMD":
                self.primary_encoder = "h264_amf"
                self.forced_cpu = False
            elif hardware_strategy == "INTEL":
                self.primary_encoder = "h264_qsv"
                self.forced_cpu = False
            elif hardware_strategy == "CPU":
                self.primary_encoder = "libx264"
                self.forced_cpu = True
        self.attempted_encoders: set[str] = set()

    def get_initial_encoder(self):
        if self.forced_cpu:
            return "libx264"
        return self.primary_encoder

    def get_fallback_list(self, failed_encoder: str) -> list[str]:
        self.attempted_encoders.add(failed_encoder)
        try:
            start_index = self.ENCODER_PREFERENCE.index(failed_encoder) + 1
        except ValueError:
            start_index = 0
        fallback_options: list[str] = []
        for i in range(start_index, len(self.ENCODER_PREFERENCE)):
            encoder = self.ENCODER_PREFERENCE[i]
            if encoder not in self.attempted_encoders:
                fallback_options.append(encoder)
        self.logger.info(f"Fallback initiated. Failed: '{failed_encoder}'. Attempted so far: {self.attempted_encoders}. Next options: {fallback_options}")
        return fallback_options

    def get_codec_flags(self, encoder_name: str, video_bitrate_kbps: Optional[int], effective_duration_sec: float, fps_expr: str = "60000/1001") -> tuple[list[str], str]:
        fps_value = self._fps_to_float(fps_expr, default=60.0)
        if self.forced_cpu:
            self.logger.info("Encoder: CPU Force Enabled for this job.")
            return ['-c:v', 'libx264', '-preset', 'medium', '-crf', '17', '-pix_fmt', 'yuv420p'], "CPU (Forced HQ)"
        vcodec = ['-c:v', encoder_name]
        rc_label = "Unknown"
        if video_bitrate_kbps is not None:
            kbps = int(video_bitrate_kbps)
            bitrate_arg = f'{kbps}k'
            maxrate_arg = f'{int(kbps * 2.5)}k' 
            bufsize_arg = f'{int(kbps * 2.5)}k'
            vcodec.extend(['-b:v', bitrate_arg, '-maxrate', maxrate_arg, '-bufsize', bufsize_arg])
        gop = '60'
        try:
            if fps_expr and '/' in str(fps_expr):
                num, den = str(fps_expr).split('/', 1)
                gop = str(max(1, int(round(float(num) / float(den)) * 1.0)))
            elif fps_expr:
                gop = str(max(1, int(round(float(fps_expr)) * 1.0)))
        except Exception:
            gop = '60'
        vcodec.extend(['-g', gop, '-keyint_min', gop])
        if encoder_name == 'h264_nvenc':
            h264_level = '5.1' if fps_value >= 100.0 else '4.2'
            vcodec.extend([
                '-pix_fmt', 'nv12',
                '-preset', 'p7',
                '-tune', 'hq',
                '-rc', 'vbr',        
                '-multipass', 'fullres', 
                '-spatial-aq', '1',
                '-temporal-aq', '1',
                '-aq-strength', '8',
                '-bf', '3',
                '-b_ref_mode', 'each',
                '-rc-lookahead', '32',
                '-profile:v', 'high',   
                '-level:v', h264_level,
                '-sc_threshold', '0', 
                '-forced-idr', '1'
            ])
            rc_label = "NVENC (Titan-Motion VBR)"
        elif encoder_name == 'h264_amf':
            vcodec.extend([
                '-pix_fmt', 'nv12',
                '-usage', 'transcoding',
                '-quality', 'quality',
                '-rc', 'vbr_peak',
                '-enforce_hrd', '1',
                '-vbaq', '1',           
                '-bf', '2',             
                '-profile:v', 'high',
                '-level', '5.1'
            ])
            rc_label = "AMD AMF (Titan-Motion VBR)"
        elif encoder_name == 'h264_qsv':
            vcodec.extend([
                '-pix_fmt', 'nv12',
                '-preset', 'slow', 
                '-bf', '3', 
                '-look_ahead', '1', 
                '-look_ahead_depth', '32',
                '-profile:v', 'high',
                '-extbrc', '1'
            ])
            rc_label = "Intel QSV (Titan-Motion VBR)"
        elif encoder_name == 'libx264':
            vcodec.append('-pix_fmt')
            vcodec.append('yuv420p')
            if video_bitrate_kbps is None:
                vcodec.extend(['-preset', 'slower', '-crf', '17', '-bf', '3'])
                return vcodec, "CPU libx264 (CRF HQ)"
            else:
                vcodec.extend([
                    '-preset', 'slower', 
                    '-bf', '3',
                    '-profile:v', 'high',
                    '-level:v', ('5.1' if fps_value >= 100.0 else '4.2'),
                    '-x264-params', 'me=umh:subme=10:rc-lookahead=32:ref=4:aq-mode=2:mbtree=1'
                ])
                return vcodec, "CPU libx264 (Titan-Motion VBR)"
        else:
            vcodec.extend(['-pix_fmt', 'nv12'])
            rc_label = f"{encoder_name} (Generic)"
        return vcodec, rc_label

    def get_intro_codec_flags(self, video_bitrate_kbps: int) -> list[str]:
        encoder = self.primary_encoder if not self.forced_cpu else 'libx264'
        vcodec, _ = self.get_codec_flags(encoder, video_bitrate_kbps, effective_duration_sec=5.0)
        return vcodec