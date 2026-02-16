import os

class EncoderManager:
    """
    Manages encoder selection, settings, and dynamic fallback strategies.
    """
    ENCODER_PREFERENCE = ["h264_nvenc", "h264_amf", "h264_qsv", "libx264"]

    def __init__(self, logger):
        self.logger = logger
        self.primary_encoder = os.environ.get('VIDEO_HW_ENCODER', 'h264_nvenc')
        self.forced_cpu = (os.environ.get('VIDEO_FORCE_CPU') == '1')
        self.attempted_encoders = set()

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
        fallback_options = []
        for i in range(start_index, len(self.ENCODER_PREFERENCE)):
            encoder = self.ENCODER_PREFERENCE[i]
            if encoder not in self.attempted_encoders:
                fallback_options.append(encoder)
        self.logger.info(f"Fallback initiated. Failed: '{failed_encoder}'. Attempted so far: {self.attempted_encoders}. Next options: {fallback_options}")
        return fallback_options

    def get_codec_flags(self, encoder_name: str, video_bitrate_kbps: int, effective_duration_sec: float) -> tuple[list[str], str]:
        if self.forced_cpu:
            self.logger.info("Encoder: CPU Force Enabled for this job.")
            return ['-c:v', 'libx264', '-preset', 'veryfast', '-crf', '18'], "CPU (Forced)"
        vcodec = ['-c:v', encoder_name, '-pix_fmt', 'nv12']
        rc_label = "Unknown"
        if video_bitrate_kbps is not None:
            kbps = int(video_bitrate_kbps)
            bitrate_arg = f'{kbps}k'
            maxrate_arg = f'{int(kbps * 1.5)}k'
            bufsize_arg = f'{int(kbps * 2.0)}k'
            vcodec.extend(['-b:v', bitrate_arg, '-maxrate', maxrate_arg, '-bufsize', bufsize_arg])
        vcodec.extend(['-g', '60', '-keyint_min', '60'])
        if encoder_name == 'h264_nvenc':
            vcodec.extend([
                '-preset', 'p1',
                '-tune', 'hq',
                '-rc', 'vbr',
                '-forced-idr', '1',
                '-spatial-aq', '1',
                '-temporal-aq', '1',
                '-b_ref_mode', 'middle',
                '-multipass', 'fullres',
                '-bf', '3'
            ])
            rc_label = "NVENC (Fast Motion)"
        elif encoder_name == 'h264_amf':
            vcodec.extend(['-usage', 'transcoding', '-quality', 'speed', '-rc', 'vbr_peak'])
            rc_label = "AMD AMF (Stable)"
        elif encoder_name == 'h264_qsv':
            vcodec.extend(['-preset', 'medium'])
            rc_label = "Intel QSV (Stable)"
        elif encoder_name == 'libx264':
            if video_bitrate_kbps is None:
                return ['-c:v', 'libx264', '-preset', 'veryfast', '-crf', '18'], "CPU libx264 (CRF)"
            else:
                return ['-c:v', 'libx264', '-preset', 'veryfast', '-b:v', f'{video_bitrate_kbps}k', '-pix_fmt', 'yuv420p'], "CPU libx264 (VBR)"
        else:
            rc_label = f"{encoder_name} (Generic)"
        return vcodec, rc_label

    def get_intro_codec_flags(self, video_bitrate_kbps: int) -> list[str]:
        encoder = self.primary_encoder if not self.forced_cpu else 'libx264'
        vcodec, _ = self.get_codec_flags(encoder, video_bitrate_kbps, effective_duration_sec=5.0)
        return vcodec