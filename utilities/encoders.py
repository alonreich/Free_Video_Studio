import os

class EncoderManager:
    def __init__(self, logger):
        self.logger = logger
        env_enc = os.environ.get('VIDEO_HW_ENCODER')
        if env_enc:
            self.hw_encoder = env_enc
        else:
            import shutil
            self.hw_encoder = 'libx264'
            if shutil.which('nvidia-smi'):
                self.hw_encoder = 'h264_nvenc'
            self.logger.info(f"EncoderManager: Selected '{self.hw_encoder}' based on hardware detection.")
        self.forced_cpu = (os.environ.get('VIDEO_FORCE_CPU') == '1')

    def get_core_codec_flags(self, video_bitrate_kbps, effective_duration):
        if self.forced_cpu:
            self.logger.info("Encoder: CPU Force Enabled.")
            if video_bitrate_kbps is None:
                return ['-c:v', 'libx264', '-preset', 'veryfast', '-crf', '18'], "CPU libx264"
            else:
                return ['-c:v', 'libx264', '-preset', 'veryfast', '-b:v', f'{video_bitrate_kbps}k'], "CPU libx264"
        vcodec = ['-c:v', self.hw_encoder]
        rc_label = "Unknown"
        if self.hw_encoder == 'h264_nvenc' and video_bitrate_kbps is None:
            video_bitrate_kbps = 6000
        if video_bitrate_kbps is not None:
            kbps = int(video_bitrate_kbps)
            bitrate_arg = f'{kbps}k'
            maxrate_arg = f'{int(kbps*1.2)}k'
            bufsize_arg = f'{int(kbps*2.0)}k'
            vcodec += ['-b:v', bitrate_arg, '-maxrate', maxrate_arg, '-bufsize', bufsize_arg]
        elif self.hw_encoder == 'h264_nvenc':
            vcodec += ['-rc', 'vbr', '-cq', '28']
        elif self.hw_encoder == 'h264_amf':
            vcodec += ['-quality', 'quality', '-rc', 'vbr_peak']
        elif self.hw_encoder == 'h264_qsv':
            vcodec += ['-global_quality', '23']
        vcodec += ['-g', '60', '-keyint_min', '60']
        if self.hw_encoder == 'h264_nvenc':
            strict_size = (effective_duration <= 20.0)
            vcodec += ['-forced-idr', '1', '-b_ref_mode', 'disabled']
            if strict_size:
                vcodec += ['-rc', 'cbr', '-tune', 'hq', '-rc-lookahead', '0', '-bf', '0']
                rc_label = "NVENC CBR (Strict)"
            else:
                vcodec += ['-rc', 'vbr', '-preset', 'p6', '-tune', 'hq', '-multipass', '2', '-rc-lookahead', '8', '-bf', '1']
                rc_label = "NVENC VBR (Capped 6Mbps)"
        elif self.hw_encoder == 'h264_amf':
            vcodec += ['-usage', 'transcoding', '-quality', 'quality', '-rc', 'vbr_peak']
            rc_label = "AMD AMF"
        elif self.hw_encoder == 'h264_qsv':
            vcodec += ['-preset', 'medium', '-look_ahead', '0']
            rc_label = "Intel QSV"
        else:
            rc_label = f"{self.hw_encoder} (Generic)"
        return vcodec, rc_label

    def get_intro_codec_flags(self, video_bitrate_kbps):
        if self.forced_cpu:
            return ['-c:v', 'libx264', '-preset', 'veryfast', '-crf', '23']
        vcodec = ['-c:v', self.hw_encoder]
        if video_bitrate_kbps:
            kbps = int(video_bitrate_kbps)
            vcodec += ['-b:v', f'{kbps}k', '-maxrate', f'{kbps}k', '-bufsize', f'{kbps}k']
        vcodec += ['-g', '60', '-keyint_min', '60']
        if self.hw_encoder == 'h264_nvenc':
            vcodec += ['-rc', 'cbr', '-tune', 'hq', '-rc-lookahead', '0', '-bf', '0', '-forced-idr', '1', '-b_ref_mode', 'disabled']
        elif self.hw_encoder == 'h264_amf':
            vcodec += ['-usage', 'transcoding', '-quality', 'quality', '-rc', 'cbr']
        elif self.hw_encoder == 'h264_qsv':
            vcodec += ['-preset', 'medium', '-look_ahead', '0']
        return vcodec

    def get_fallback_list(self):
        all_encoders = ["h264_nvenc", "h264_amf", "h264_qsv", "libx264"]
        try:
            start_idx = all_encoders.index(self.hw_encoder) + 1
        except ValueError:
            start_idx = 0
        return all_encoders[start_idx:]