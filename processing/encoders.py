import os
from typing import Any, Optional

class EncoderManager:
    ENCODER_PREFERENCE = ["h264_nvenc", "h264_amf", "h264_qsv", "libx264"]

    def _fps_to_float(self, fps_expr: str, default: float = 60.0) -> float:
        from fractions import Fraction
        try:
            if not fps_expr:
                return float(default)
            return float(Fraction(str(fps_expr)))
        except Exception:
            return float(default)

    def __init__(self, logger: Any, hardware_strategy: Optional[str] = None):
        self.logger = logger
        self.available_encoders = self._detect_available_encoders()
        self.primary_encoder = os.environ.get('VIDEO_HW_ENCODER', 'h264_nvenc')
        self.forced_cpu = (os.environ.get('VIDEO_FORCE_CPU') == '1')
        if hardware_strategy:
            if hardware_strategy == "NVIDIA" and "h264_nvenc" in self.available_encoders:
                self.primary_encoder = "h264_nvenc"
                self.forced_cpu = False
            elif hardware_strategy == "AMD" and "h264_amf" in self.available_encoders:
                self.primary_encoder = "h264_amf"
                self.forced_cpu = False
            elif hardware_strategy == "INTEL" and "h264_qsv" in self.available_encoders:
                self.primary_encoder = "h264_qsv"
                self.forced_cpu = False
            elif hardware_strategy == "CPU":
                self.primary_encoder = "libx264"
                self.forced_cpu = True
            else:
                if hardware_strategy != "CPU":
                    self.logger.warning(f"Hardware strategy '{hardware_strategy}' requested but encoder not found. Falling back to libx264.")
                self.primary_encoder = "libx264"
                self.forced_cpu = True
        self.attempted_encoders: set[str] = set()

    def _detect_available_encoders(self) -> set[str]:
        import subprocess
        try:
            startupinfo = None
            if os.name == 'nt':
                startupinfo = subprocess.STARTUPINFO()
                startupinfo.dwFlags |= subprocess.STARTF_USESHOWWINDOW
            res = subprocess.run(['ffmpeg', '-hide_banner', '-encoders'], 
                                capture_output=True, text=True, 
                                startupinfo=startupinfo,
                                creationflags=0x08000000 if os.name == 'nt' else 0,
                                timeout=5)
            found = set()
            for line in res.stdout.splitlines():
                if 'h264_nvenc' in line: found.add('h264_nvenc')
                if 'h264_amf' in line: found.add('h264_amf')
                if 'h264_qsv' in line: found.add('h264_qsv')
            self.logger.info(f"Detected hardware encoders: {found}")
            return found
        except Exception as e:
            self.logger.warning(f"Failed to detect encoders: {e}")
            return set()

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

    def get_codec_flags(self, encoder_name: str, video_bitrate_kbps: Optional[int], effective_duration_sec: float, fps_expr: str = "60000/1001", quality_level: int = 2) -> tuple[list[str], str]:
        fps_value = self._fps_to_float(fps_expr, default=60.0)
        if self.forced_cpu:
            cpu_preset = 'fast' if quality_level <= 1 else 'medium'
            crf = '20' if quality_level <= 1 else '17'
            self.logger.info(f"Encoder: CPU Force Enabled. Preset={cpu_preset} CRF={crf}")
            return ['-c:v', 'libx264', '-preset', cpu_preset, '-crf', crf, '-pix_fmt', 'yuv420p'], f"CPU ({cpu_preset}/{crf})"
        vcodec = ['-c:v', encoder_name]
        rc_label = "Unknown"
        gop = str(int(round(fps_value * 2.0)))
        vcodec.extend(['-g', gop, '-keyint_min', str(int(round(fps_value)))])
        if encoder_name == 'h264_nvenc':
            if quality_level <= 0: 
                nv_preset, multipass, lookahead = 'p2', 'disabled', '20'
                aq_flags = []
            elif quality_level <= 1:
                nv_preset, multipass, lookahead = 'p4', 'disabled', '40'
                aq_flags = ['-spatial-aq', '1', '-temporal-aq', '1']
            else: 
                nv_preset, multipass, lookahead = 'p6', 'fullres', '40'
                aq_flags = ['-spatial-aq', '1', '-temporal-aq', '1']
            h264_level = 'auto'
            vcodec.extend([
                '-pix_fmt', 'yuv420p',
                '-preset', nv_preset,
                '-tune', 'hq',
                '-rc', 'vbr',
                '-multipass', multipass
            ])
            vcodec.extend(aq_flags)
            if quality_level > 1:
                vcodec.extend(['-aq-strength', '15', '-b_ref_mode', 'middle', '-nonref_p', '1', '-spatial-aq', '1', '-temporal-aq', '1'])
            vcodec.extend([
                '-bf', '3',
                '-rc-lookahead', lookahead,
                '-profile:v', 'high',
                '-level:v', h264_level
            ])
            if video_bitrate_kbps:
                kbps = min(40000, int(video_bitrate_kbps))
                max_rate = min(50000, int(kbps * 1.5))
                vcodec.extend([
                    '-b:v', f'{kbps}k',
                    '-maxrate', f'{max_rate}k',
                    '-bufsize', f'{int(kbps * 3.0)}k'
                ])
                rc_label = f"NVENC {nv_preset}/{multipass} (VBR)"
            else:
                cq_val = '22' if quality_level <= 1 else '19'
                vcodec.extend(['-cq', cq_val])
                rc_label = f"NVENC {nv_preset}/{multipass} (CQ {cq_val})"
        elif encoder_name == 'h264_amf':
            amf_quality = 'balanced' if quality_level <= 1 else 'quality'
            vcodec.extend([
                '-pix_fmt', 'yuv420p',
                '-usage', 'transcoding',
                '-quality', amf_quality,
                '-rc', 'vbr_peak',
                '-enforce_hrd', '1',
                '-vbaq', '1',
                '-bf', '3',
                '-profile:v', 'high',
                '-level', '4.2'
            ])
            if video_bitrate_kbps:
                kbps = int(video_bitrate_kbps)
                vcodec.extend(['-b:v', f'{kbps}k', '-maxrate', f'{int(kbps * 1.5)}k', '-bufsize', f'{int(kbps * 2.0)}k'])
            rc_label = f"AMD AMF {amf_quality}"
        elif encoder_name == 'h264_qsv':
            qsv_preset = 'balanced' if quality_level <= 1 else 'slow'
            vcodec.extend([
                '-pix_fmt', 'yuv420p',
                '-preset', qsv_preset, 
                '-bf', '3', 
                '-look_ahead', '1', 
                '-look_ahead_depth', '60' if quality_level <= 1 else '100',
                '-profile:v', 'high',
                '-level', '4.2'
            ])
            if video_bitrate_kbps:
                kbps = int(video_bitrate_kbps)
                vcodec.extend(['-b:v', f'{kbps}k', '-maxrate', f'{int(kbps * 1.5)}k', '-bufsize', f'{int(kbps * 2.0)}k'])
            rc_label = f"Intel QSV {qsv_preset}"
        elif encoder_name == 'libx264':
            cpu_preset = 'veryfast' if quality_level <= 0 else ('fast' if quality_level <= 1 else 'medium')
            vcodec.append('-pix_fmt')
            vcodec.append('yuv420p')
            if video_bitrate_kbps is None:
                crf = '23' if quality_level <= 0 else ('20' if quality_level <= 1 else '17')
                vcodec.extend(['-preset', cpu_preset, '-crf', crf, '-bf', '3'])
                return vcodec, f"CPU libx264 ({cpu_preset}/CRF{crf})"
            else:
                kbps = int(video_bitrate_kbps)
                vcodec.extend([
                    '-preset', cpu_preset, 
                    '-bf', '3',
                    '-b:v', f'{kbps}k',
                    '-maxrate', f'{int(kbps * 1.5)}k',
                    '-bufsize', f'{int(kbps * 3.0)}k',
                    '-profile:v', 'high'
                ])
                return vcodec, f"CPU libx264 ({cpu_preset})"
        else:
            vcodec.extend(['-pix_fmt', 'yuv420p'])
            rc_label = f"{encoder_name} (Generic)"
        return vcodec, rc_label

    def get_intro_codec_flags(self, video_bitrate_kbps: int) -> list[str]:
        encoder = self.primary_encoder if not self.forced_cpu else 'libx264'
        vcodec, _ = self.get_codec_flags(encoder, video_bitrate_kbps, effective_duration_sec=5.0)
        return vcodec
