import os

class EncoderManager:
    """
    Manages encoder selection, settings, and dynamic fallback strategies.
    This class centralizes encoder logic, making it easier to manage priorities
    and adapt to failures during a render job.
    """
    ENCODER_PREFERENCE = ["h264_nvenc", "h264_amf", "h264_qsv", "libx264"]

    def __init__(self, logger):
        self.logger = logger
        self.primary_encoder = os.environ.get('VIDEO_HW_ENCODER', 'libx264')
        self.forced_cpu = (os.environ.get('VIDEO_FORCE_CPU') == '1')
        self.attempted_encoders = set()

    def get_initial_encoder(self):
        """Returns the best-case encoder determined at startup."""
        if self.forced_cpu:
            return "libx264"
        return self.primary_encoder

    def get_fallback_list(self, failed_encoder: str) -> list[str]:
        """
        Generates a dynamic list of fallback encoders to try, excluding any
        that have already failed in the current job.
        Args:
            failed_encoder: The name of the encoder that just failed (e.g., 'h264_nvenc').
        Returns:
            A list of new encoder names to attempt.
        """
        self.attempted_encoders.add(failed_encoder)
        try:
            start_index = self.ENCODER_PREFERENCE.index(failed_encoder) + 1
        except ValueError:
            start_index = 0
        fallback_options = []
        for i in range(start_index, len(self.ENCODER_PREFERENCE)):
            encoder = self.ENCODER_PREFERENCE[i]
            if encoder not in self.attempted_encoders:
                fallback_options.append(encoder)
        self.logger.info(f"Fallback initiated. Failed: '{failed_encoder}'. Attempted so far: {self.attempted_encoders}. Next options: {fallback_options}")
        return fallback_options

    def get_codec_flags(self, encoder_name: str, video_bitrate_kbps: int, effective_duration_sec: float) -> tuple[list[str], str]:
        """
        Returns the specific FFmpeg command flags and a UI label for a given encoder.
        """
        if self.forced_cpu:
            self.logger.info("Encoder: CPU Force Enabled for this job.")
            return ['-c:v', 'libx264', '-preset', 'veryfast', '-crf', '18'], "CPU (Forced)"
        vcodec = ['-c:v', encoder_name]
        rc_label = "Unknown"
        if video_bitrate_kbps is not None:
            kbps = int(video_bitrate_kbps)
            bitrate_arg = f'{kbps}k'
            maxrate_arg = f'{int(kbps * 2.0)}k'
            bufsize_arg = f'{int(kbps * 2.0)}k'
            vcodec.extend(['-b:v', bitrate_arg, '-maxrate', maxrate_arg, '-bufsize', bufsize_arg])
        vcodec.extend(['-g', '60', '-keyint_min', '60'])
        if encoder_name == 'h264_nvenc':
            vcodec.extend([
                '-rc', 'vbr',
                '-tune', 'hq', 
                '-preset', 'p5',
                '-rc-lookahead', '20',
                '-spatial-aq', '1',
                '-temporal-aq', '1',
                '-bf', '3',
                '-b_ref_mode', 'middle',
                '-forced-idr', '1'
            ])
            rc_label = "NVENC VBR (HQ)"
        elif encoder_name == 'h264_amf':
            vcodec.extend(['-usage', 'transcoding', '-quality', 'quality', '-rc', 'vbr_peak'])
            rc_label = "AMD AMF"
        elif encoder_name == 'h264_qsv':
            vcodec.extend(['-preset', 'medium', '-look_ahead', '0'])
            rc_label = "Intel QSV"
        elif encoder_name == 'libx264':
            if video_bitrate_kbps is None:
                return ['-c:v', 'libx264', '-preset', 'veryfast', '-crf', '18'], "CPU libx264 (CRF)"
            else:
                return ['-c:v', 'libx264', '-preset', 'veryfast', '-b:v', f'{video_bitrate_kbps}k'], "CPU libx264 (VBR)"
        else:
            rc_label = f"{encoder_name} (Generic)"
        return vcodec, rc_label

    def get_intro_codec_flags(self, video_bitrate_kbps: int) -> list[str]:
        """
        Returns FFmpeg command flags for intro encoding.
        Uses the primary encoder (or CPU fallback) with the given bitrate.
        """
        encoder = self.primary_encoder if not self.forced_cpu else 'libx264'
        vcodec, _ = self.get_codec_flags(encoder, video_bitrate_kbps, effective_duration_sec=5.0)
        return vcodec
