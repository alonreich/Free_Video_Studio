import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import psutil
import time
from PyQt5.QtCore import QThread
from PyQt5.QtGui import QFont, QFontMetrics

class ProcessThread(QThread):

    def __init__(self, input_path, start_time, end_time, original_resolution, is_mobile_format, speed_factor,
                script_dir, progress_update_signal, status_update_signal, finished_signal, logger,
                is_boss_hp=False, show_teammates_overlay=False, quality_level: int = 2,
                bg_music_path=None, bg_music_volume=None, bg_music_offset=0.0, original_total_duration=0.0,
                disable_fades=False, intro_still_sec: float = 0.0, intro_from_midpoint: bool = False, intro_abs_time: float = None,
                portrait_text: str = None):
        super().__init__()
        self.portrait_text = portrait_text
        self.is_boss_hp = is_boss_hp
        self.input_path = input_path
        self.start_time = start_time
        self.end_time = end_time
        self.duration = end_time - start_time
        self.original_resolution = original_resolution
        self.is_mobile_format = is_mobile_format
        self.speed_factor = speed_factor
        self.show_teammates_overlay = bool(show_teammates_overlay)
        try:
            self.quality_level = int(quality_level)
        except Exception:
            self.quality_level = 2
        try:
            q = self.quality_level
        except Exception:
            q = 2
        self.keep_highest_res = (q >= 4)
        self.lower_quality = (q <= 0)
        if self.keep_highest_res:
            self.target_mb = None
        elif q == 3:
            self.target_mb = 90.0
        elif q == 2:
            self.target_mb = 45.0
        elif q == 1:
            self.target_mb = 25.0
        else:
            self.target_mb = 15.0
        self.script_dir = script_dir
        self.base_dir = os.path.abspath(os.path.join(self.script_dir, os.pardir))
        self.bin_dir = os.path.join(self.base_dir, 'binaries')
        self.progress_update_signal = progress_update_signal
        self.status_update_signal = status_update_signal
        self.finished_signal = finished_signal
        self.logger = logger
        self.bg_music_path = bg_music_path if (bg_music_path and os.path.isfile(bg_music_path)) else None
        try:
            self.bg_music_volume = float(bg_music_volume) if bg_music_volume is not None else None
        except Exception:
            self.bg_music_volume = None
        try:
            self.bg_music_offset = float(bg_music_offset)
        except Exception:
            self.bg_music_offset = 0.0
        try:
            self.original_total_duration = float(original_total_duration)
        except Exception:
            self.original_total_duration = 0.0
        self.disable_fades = bool(disable_fades)
        self.intro_from_midpoint = bool(intro_from_midpoint)
        try:
            self.intro_still_sec = float(intro_still_sec or 0.0)
            self.intro_abs_time = float(intro_abs_time) if intro_abs_time is not None else None
        except Exception:
            self.intro_still_sec = 0.0
        if self.intro_still_sec <= 0.0:
            self.intro_still_sec = 0.0
        self.start_time_corrected = self.start_time / self.speed_factor if self.speed_factor != 1.0 else self.start_time
        user_duration = self.duration / self.speed_factor if self.speed_factor != 1.0 else self.duration
        self.duration_corrected = max(0.0, user_duration)
        self._estimated_total_duration = max(1.0, self.duration_corrected + self.intro_still_sec)
        self.current_process = None
        self.is_canceled = False

    def _parse_time_to_seconds(self, time_str: str) -> float:
        """Converts HH:MM:SS.ss or MM:SS.ss time string to seconds."""
        try:
            parts = time_str.split(':')
            if len(parts) == 3:
                h = int(parts[0])
                m = int(parts[1])
                s = float(parts[2])
                return (h * 3600) + (m * 60) + s
        except Exception:
            try:
                if len(parts) == 2:
                    m = int(parts[0])
                    s = float(parts[1])
                    return (m * 60) + s
                elif len(parts) == 1:
                    return float(parts[0])
            except Exception:
                return 0.0
        return 0.0

    def cancel(self):
        self.logger.info("Cancellation requested for processing thread.")
        self.is_canceled = True
        if self.current_process and self.current_process.poll() is None:
            self.logger.info(f"Terminating process with PID: {self.current_process.pid}")
            try:
                parent = psutil.Process(self.current_process.pid)
                for child in parent.children(recursive=True):
                    child.kill()
                parent.kill()
                self.logger.info("Process terminated.")
            except psutil.NoSuchProcess:
                self.logger.warning("Process not found, might have already finished.")
            except Exception as e:
                self.logger.error(f"Error terminating process: {e}")

    def get_total_frames(self):
        return None

    def _get_audio_bitrate(self):
        """Robustly probes audio bitrate. Tries stream first, falls back to format."""
        ffprobe_path = os.path.join(self.bin_dir, 'ffprobe.exe')

        def _run_probe(args):
            try:
                cmd = [ffprobe_path, "-v", "error", "-of", "default=nw=1:nk=1"] + args + [self.input_path]
                r = subprocess.run(
                    cmd, capture_output=True, text=True, check=True,
                    creationflags=(subprocess.CREATE_NO_WINDOW if sys.platform == "win32" else 0)
                )
                val = float((r.stdout or "0").strip() or 0)
                return max(8, int(round(val / 1000.0))) if val > 0 else None
            except Exception:
                return None
        kbps = _run_probe(["-select_streams", "a:0", "-show_entries", "stream=bit_rate"])
        if kbps: return kbps
        kbps = _run_probe(["-show_entries", "format=bit_rate"])
        return kbps

    def _calculate_video_bitrate(self, effective_duration, audio_kbps):
        """Calculates target video bitrate (kbps) based on quality settings and duration."""
        target_size_bits = 0
        is_max_quality = False
        if self.keep_highest_res:
            try:
                src_bytes = os.path.getsize(self.input_path)
                target_size_bits = max(1, src_bytes) * 8 
                is_max_quality = True
            except Exception:
                self.target_mb = 52.0
        if not is_max_quality:
            t_mb = self.target_mb if self.target_mb is not None else 52.0
            target_size_bits = t_mb * 8 * 1024 * 1024
        audio_bits = audio_kbps * 1024 * effective_duration
        video_bits = target_size_bits - audio_bits
        if video_bits <= 0:
            if is_max_quality:
                return 300
            return None
        calculated_kbps = int(video_bits / (1024 * effective_duration))
        if is_max_quality:
            return max(300, calculated_kbps)
        return calculated_kbps

    def run(self):
        temp_dir = tempfile.gettempdir()
        self.temp_dir = temp_dir
        temp_log_path = os.path.join(temp_dir, f"ffmpeg2pass-{os.getpid()}-{int(time.time())}.log")
        core_path, intro_path, concat_path, output_path = None, None, None, None
        try:
            if self.is_canceled: return
            user_start = float(self.start_time)
            user_end   = float(self.end_time)
            total_orig = float(self.original_total_duration or 0.0)
            FADE_DUR = 1.5
            EPS = 0.01
            if self.disable_fades:
                self.logger.info("Fades disabled. Using exact trim.")
                in_ss = user_start
                in_t = user_end - user_start
                vfade_in_d = 0.0
                vfade_out_d = 0.0
                vfade_out_st = 0.0
                output_clip_duration = in_t
            else:
                self.logger.info("Fades enabled. Calculating padding.")
                if user_start < FADE_DUR - EPS:
                    self.logger.info(f"Start time {user_start}s is too close to 0. Disabling fade-in and start padding.")
                    in_ss = user_start
                    vfade_in_d = 0.0
                else:
                    in_ss = user_start - FADE_DUR
                    vfade_in_d = FADE_DUR
                if total_orig > 0.0 and user_end > (total_orig - FADE_DUR + EPS):
                    self.logger.info(f"End time {user_end}s is too close to total duration {total_orig}s. Disabling fade-out and end padding.")
                    adj_end = total_orig
                    vfade_out_d = 0.0
                else:
                    adj_end = user_end + FADE_DUR
                    vfade_out_d = FADE_DUR
                in_t = max(0.0, adj_end - in_ss)
                output_clip_duration = in_t
                if vfade_out_d > 0.0:
                    vfade_out_st = max(0.0, output_clip_duration - vfade_out_d)
                else:
                    vfade_out_st = output_clip_duration
            output_clip_duration = output_clip_duration / self.speed_factor if self.speed_factor != 1.0 else output_clip_duration
            vfade_in_d   = vfade_in_d / self.speed_factor if self.speed_factor != 1.0 else vfade_in_d
            vfade_out_d  = vfade_out_d / self.speed_factor if self.speed_factor != 1.0 else vfade_out_d
            vfade_out_st = vfade_out_st / self.speed_factor if self.speed_factor != 1.0 else vfade_out_st
            self.duration_corrected = max(0.0, output_clip_duration)
            if self.is_mobile_format and self.portrait_text:
                txt = self.portrait_text
                if any("\u0590" <= c <= "\u05ff" for c in txt):
                    rev_full = txt[::-1]



                    def _flip_back_match(match):
                        return match.group(0)[::-1]
                    final_text = re.sub(r'[^\u0590-\u05ff]+', _flip_back_match, rev_full)
                else:
                    final_text = txt
                final_text = final_text.replace(":", "\\:").replace("'", "'\\\\''")
            if self.speed_factor != 1.0:
                self.status_update_signal.emit(f"Adjusting trim times for speed factor {self.speed_factor}x.")
            thumbnail_hold_sec = 0.0
            start_time_corrected = in_ss
            AUDIO_KBPS = 256
            _src_kbps = self._get_audio_bitrate()
            if _src_kbps:
                AUDIO_KBPS = _src_kbps
                self.status_update_signal.emit(f"Audio bitrate: preserving source ~{AUDIO_KBPS} kbps.")
                self.logger.info(f"Audio bitrate probe: {AUDIO_KBPS} kbps")
            else:
                self.status_update_signal.emit(f"Audio bitrate: source unknown, defaulting to {AUDIO_KBPS} kbps.")
            intro_len_for_size = max(0.0, float(self.intro_still_sec)) if self.intro_still_sec > 0.0 else 0.0
            effective_duration = self.duration_corrected + intro_len_for_size
            if self.keep_highest_res:
                probed = self._get_audio_bitrate()
                if probed:
                    AUDIO_KBPS = probed
            video_bitrate_kbps = self._calculate_video_bitrate(effective_duration, AUDIO_KBPS)
            if video_bitrate_kbps is None:
                if not self.keep_highest_res:
                    self.finished_signal.emit(False, "Video duration is too short for the target file size (Audio takes up all space).")
                    return
                else:
                    video_bitrate_kbps = 2500
            if self.keep_highest_res:
                self.status_update_signal.emit(f"Maximum quality: source-matched size; video ~{video_bitrate_kbps} kbps.")
            else:
                t_mb = self.target_mb if self.target_mb is not None else 52.0
                q_desc = {0: "Bad", 1: "Okay", 2: "Standard", 3: "Good"}.get(self.quality_level, "Standard")
                self.status_update_signal.emit(f"{q_desc} quality: target ~{t_mb:.0f} MB; video ~{video_bitrate_kbps} kbps.")
            if self.is_canceled: return
            total_frames = self.get_total_frames()
            if total_frames is None:
                self.status_update_signal.emit("Could not determine total frames. Progress bar might be inaccurate.")
            video_filter_cmd = ""
            main_width  = 1280
            main_height = 1920
            if self.is_mobile_format:
                coords = {
                    "crops_1080p": {
                        "loot": [339, 68, 1548, 976], "stats": [215, 153, 1682, 20],
                        "normal_hp": [324, 50, 33, 979], "boss_hp": [325, 46, 138, 981],
                        "team": [174, 161, 14, 801]
                    },
                    "scales": {
                        "loot": 1.953, "stats": 2.112, "team": 1.61,
                        "normal_hp": 1.849, "boss_hp": 1.615
                    },
                    "overlays": {
                        "loot": {"x": 613, "y": 1659}, "stats": {"x": 829, "y": 0},
                        "team": {"x": 32, "y": 1434},
                        "normal_hp": {"x": 5, "y": 1681}, "boss_hp": {"x": 20, "y": 1701}
                    }
                }
                conf_path = os.path.join(self.base_dir, 'processing', 'crops_coordinations.conf')
                if os.path.exists(conf_path):
                    try:
                        with open(conf_path, 'r') as f:
                            coords.update(json.load(f))
                        self.logger.info(f"Loaded crop config from {conf_path}")
                    except Exception as e:
                        self.logger.error(f"Failed to load crop config, using defaults: {e}")
                loot_1080 = tuple(coords['crops_1080p']['loot'])
                stats_1080 = tuple(coords['crops_1080p']['stats'])
                normal_hp_1080 = tuple(coords['crops_1080p']['normal_hp'])
                boss_hp_1080 = tuple(coords['crops_1080p']['boss_hp'])
                team_1080 = tuple(coords['crops_1080p']['team'])
                if self.is_boss_hp:
                    hp_1080 = boss_hp_1080
                    healthbar_scale = float(coords['scales']['boss_hp'])
                    hp_ov = coords['overlays']['boss_hp']
                    self.logger.info("Using Boss HP coordinates.")
                else:
                    hp_1080 = normal_hp_1080
                    healthbar_scale = float(coords['scales']['normal_hp'])
                    hp_ov = coords['overlays']['normal_hp']
                    self.logger.info("Using Normal HP coordinates.")
                healthbar_overlay_x = hp_ov['x']
                healthbar_overlay_y = hp_ov['y']
                loot_scale = float(coords['scales']['loot'])
                stats_scale = float(coords['scales']['stats'])
                team_scale = float(coords['scales']['team'])

                def scale_box(box, s):
                    return tuple(int(round(v * s)) for v in box)
                in_w, in_h = map(int, self.original_resolution.split('x'))
                scale_factor = in_h / 1080.0
                self.logger.info(f"Mobile Crop: Resolution {in_w}x{in_h} detected. Scale factor: {scale_factor:.4f}")
                hp = scale_box(hp_1080, scale_factor)
                loot = scale_box(loot_1080, scale_factor)
                stats = scale_box(stats_1080, scale_factor)
                team = scale_box(team_1080, scale_factor)
                healthbar_crop_string  = f"{hp[0]}:{hp[1]}:{hp[2]}:{hp[3]}"
                loot_area_crop_string  = f"{loot[0]}:{loot[1]}:{loot[2]}:{loot[3]}"
                stats_area_crop_string = f"{stats[0]}:{stats[1]}:{stats[2]}:{stats[3]}"
                team_crop_string       = f"{team[0]}:{team[1]}:{team[2]}:{team[3]}"
                lootbar_scale_str = f"scale={int(round(loot_1080[0] * loot_scale))}:{int(round(loot_1080[1] * loot_scale))}"
                healthbar_scale_str = f"scale={int(round(hp_1080[0] * healthbar_scale))}:{int(round(hp_1080[1] * healthbar_scale))}"
                stats_scale_str = f"scale={int(round(stats_1080[0] * stats_scale))}:{int(round(stats_1080[1] * stats_scale))}"
                team_scale_str = f"scale={int(round(team_1080[0] * team_scale))}:{int(round(team_1080[1] * team_scale))}"
                loot_overlay_x = coords['overlays']['loot']['x']
                loot_overlay_y = coords['overlays']['loot']['y']
                stats_overlay_x = coords['overlays']['stats']['x']
                stats_overlay_y = coords['overlays']['stats']['y']
                team_overlay_x = coords['overlays']['team']['x']
                team_overlay_y = coords['overlays']['team']['y']
                common_filters = (
                    f"[main]scale=1280:1920:force_original_aspect_ratio=increase,crop=1280:1920[main_cropped];"
                    f"[lootbar]crop={loot_area_crop_string},drawbox=t=2:c=black,{lootbar_scale_str},format=yuva444p[lootbar_scaled];"
                    f"[healthbar]crop={healthbar_crop_string},drawbox=t=2:c=black,{healthbar_scale_str},format=yuva444p[healthbar_scaled];"
                    f"[stats]crop={stats_area_crop_string},drawbox=t=2:c=black,{stats_scale_str},format=yuva444p[stats_scaled];"
                )
                common_overlays = (
                    f"[main_cropped][lootbar_scaled]overlay={loot_overlay_x}:{loot_overlay_y}[t1];"
                    f"[t1][healthbar_scaled]overlay={healthbar_overlay_x}:{healthbar_overlay_y}[t2];"
                    f"[t2][stats_scaled]overlay={stats_overlay_x}:{stats_overlay_y}"
                )
                if self.show_teammates_overlay:
                    video_filter_cmd = (
                        f"split=5[main][lootbar][healthbar][stats][team];"
                        f"{common_filters}"
                        f"[team]crop={team_crop_string},drawbox=t=2:c=black,{team_scale_str},format=yuva444p[team_scaled];"
                        f"{common_overlays}[t3];"
                        f"[t3][team_scaled]overlay={team_overlay_x}:{team_overlay_y}"
                    )
                else:
                    video_filter_cmd = f"split=4[main][lootbar][healthbar][stats];{common_filters}{common_overlays}"
                video_filter_cmd += ",scale=1080:-2,pad=1080:1920:(ow-iw)/2:(oh-ih)/2:black"
                if self.portrait_text:
                    RAW = self.portrait_text
                    WRAP_AT_PX = 950
                    SAFE_MAX_PX = 900
                    BASE_SIZE = 80
                    MIN_SIZE = 36
                    LINE_SPACING = -45
                    SHADOW_PAD_PX = 14
                    MEASURE_FUDGE = 1.12

                    def _measure_px(s: str, px_size: int) -> int:
                        f = QFont("Arial")
                        f.setPixelSize(int(px_size))
                        fm = QFontMetrics(f)
                        try:
                            w = int(fm.horizontalAdvance(s))
                        except Exception:
                            w = int(fm.width(s))
                        return int(w * MEASURE_FUDGE) + SHADOW_PAD_PX
                    MAX_LINE_W = WRAP_AT_PX
                    HARD_MAX_W = SAFE_MAX_PX

                    def _split_long_token(tok: str, px_size: int):
                        chunks = []
                        cur = ""
                        for ch in tok:
                            cand = cur + ch
                            if cur and _measure_px(cand, px_size) > MAX_LINE_W:
                                chunks.append(cur)
                                cur = ch
                            else:
                                cur = cand
                        if cur:
                            chunks.append(cur)
                        return chunks

                    def _wrap_text(s: str, px_size: int):
                        tokens = []
                        for t in (s or "").split():
                            if _measure_px(t, px_size) > MAX_LINE_W:
                                tokens.extend(_split_long_token(t, px_size))
                            else:
                                tokens.append(t)
                        lines = []
                        cur = ""
                        for t in tokens:
                            cand = t if not cur else (cur + " " + t)
                            if not cur or _measure_px(cand, px_size) <= MAX_LINE_W:
                                cur = cand
                            else:
                                lines.append(cur)
                                cur = t
                        if cur:
                            lines.append(cur)
                        return lines if lines else [""]

                    def _fit_and_wrap(s: str):
                        size = BASE_SIZE
                        for _ in range(25):
                            lines = _wrap_text(s, size)
                            widest = max(_measure_px(ln, size) for ln in lines) if lines else 0
                            if widest <= MAX_LINE_W and len(lines) <= 2:
                                return size, lines
                            ratio = (widest / float(MAX_LINE_W)) if MAX_LINE_W else 1.25
                            ratio = max(1.08, ratio)
                            penalty = max(0, len(lines) - 2) * 5
                            new_size = int(max(MIN_SIZE, (size / ratio) - penalty))
                            if new_size >= size:
                                new_size = size - 2
                            if new_size <= MIN_SIZE:
                                break
                            size = new_size
                        size = max(MIN_SIZE, int(size))
                        lines = _wrap_text(s, size)
                        return size, lines
                    font_px, lines = _fit_and_wrap(RAW)
                    txt_wrapped = "\n".join(lines)
                    txt_with_ltr_numbers = re.sub(
                        r'([0-9]+(?:[.,:/\-][0-9]+)*)',
                        lambda m: "\u2066" + m.group(1) + "\u2069",
                        txt_wrapped
                    )
                    if any("\u0590" <= c <= "\u05ff" for c in self.portrait_text):
                        final_text = "\u2067" + txt_with_ltr_numbers + "\u200F" + "\u2069"
                    else:
                        final_text = "\u2066" + txt_with_ltr_numbers + "\u2069"
                    textfile_path = os.path.join(temp_dir, f"drawtext-{os.getpid()}-{int(time.time())}.txt")
                    with open(textfile_path, "w", encoding="utf-8") as tf:
                        tf.write(final_text)
                    ff_textfile = textfile_path.replace("\\", "/").replace(":", "\\:")
                    video_filter_cmd += (
                        f",drawtext=fontfile='C\\:/Windows/Fonts/arial.ttf':"
                        f"textfile='{ff_textfile}':reload=0:text_shaping=1:"
                        f"fontcolor=white:fontsize={int(font_px)}:"
                        f"x=(w-text_w)/2:y=40:line_spacing={LINE_SPACING}:"
                        f"shadowcolor=black:shadowx=3:shadowy=3"
                    )
                self.logger.info(f"Mobile portrait mode (Canvas Trick 1080x1920): Title='{self.portrait_text}'")
                self.status_update_signal.emit("Optimizing: Applying Canvas Trick (Shrink & Pad) with Text.")
            else:
                original_width, original_height = map(int, self.original_resolution.split('x'))
                if self.keep_highest_res:
                    target_resolution = "scale=iw:ih"
                    self.status_update_signal.emit("Highest Resolution: keeping source resolution.")
                else:
                    if self.quality_level >= 2:
                        target_resolution = "scale='min(1920,iw)':-2"
                        if video_bitrate_kbps < 800 and original_height > 720:
                            target_resolution = "scale='min(1280,iw)':-2"
                            self.status_update_signal.emit("Low bitrate detected. Scaling to 720p.")
                    elif self.quality_level == 1:
                        target_resolution = "scale='min(1280,iw)':-2"
                        self.status_update_signal.emit("Okay Quality: scaling to 720p.")
                    else:
                        target_resolution = "scale='min(960,iw)':-2"
                        self.status_update_signal.emit("Bad Quality: targeting ~15–20MB and smaller resolution.")
                video_filter_cmd = f"fps=60,{target_resolution}"
            if self.speed_factor != 1.0:
                speed_filter = f"setpts=PTS/{self.speed_factor}"
                if video_filter_cmd:
                    video_filter_cmd = f"{video_filter_cmd},{speed_filter}"
                else:
                    video_filter_cmd = speed_filter
                self.status_update_signal.emit(f"Applying speed factor: {self.speed_factor}x to video.")
            audio_filter_cmd = ""
            if self.speed_factor != 1.0:
                s = float(self.speed_factor)
                if 0.5 <= s <= 2.0:
                    audio_filter_cmd = f"atempo={s:.3f}"
                    self.status_update_signal.emit(f"Applying speed factor {self.speed_factor}x to audio (atempo).")
                    self.logger.info(f"Audio atempo: {audio_filter_cmd}")
                else:
                    chain = []
                    s_work = s
                    if s_work >= 1.0:
                        while s_work > 2.0:
                            chain.append(2.0); s_work /= 2.0
                        chain.append(s_work)
                    else:
                        while s_work < 0.5:
                            chain.append(0.5); s_work /= 0.5
                        chain.append(s_work)
                    chain = [min(2.0, max(0.5, round(f, 3))) for f in chain if abs(f - 1.0) > 1e-3]
                    if chain:
                        audio_filter_cmd = ",".join(f"atempo={f}" for f in chain)
                        self.status_update_signal.emit(f"Applying speed factor {self.speed_factor}x to audio (atempo chain).")
                        self.logger.info(f"Audio atempo chain: {audio_filter_cmd}")
                    else:
                        audio_filter_cmd = f"rubberband=tempo={s:.3f}:pitch=1:formant=1:transients=smooth"
                        self.status_update_signal.emit(f"Applying speed factor {self.speed_factor}x to audio (rubberband fallback).")
                        self.logger.info(f"Audio rubberband fallback: tempo={s:.3f} pitch=1 formant=1")
            if self.is_canceled: return
            output_dir = os.path.join(self.base_dir, '!!!_Ouput_Video_Files_!!!')
            os.makedirs(output_dir, exist_ok=True)
            i = 1
            while True:
                output_file_name = f"Fortnite-Video-{i}.mp4"
                output_path = os.path.join(output_dir, output_file_name)
                if not os.path.exists(output_path):
                    break
                i += 1
            ffmpeg_path = os.path.join(self.bin_dir, 'ffmpeg.exe')
            time_regex = re.compile(r'time=(\S+)')
            self.progress_update_signal.emit(0)
            cmd = [
                ffmpeg_path, '-y',
                '-hwaccel', 'auto',
                '-ss', f"{in_ss:.3f}", '-t', f"{in_t:.3f}",
                '-i', self.input_path,
            ]
            have_bg = bool(self.bg_music_path)
            if have_bg:
                cmd += ['-i', self.bg_music_path]
                self.status_update_signal.emit("Background music: mixing enabled.")
            else:
                self.status_update_signal.emit("Background music: disabled or not found.")
            hw_encoder = os.environ.get('VIDEO_HW_ENCODER', 'h264_nvenc')
            forced_cpu = (os.environ.get('VIDEO_FORCE_CPU') == '1')
            if forced_cpu:
                if video_bitrate_kbps is None:
                    vcodec = ['-c:v', 'libx264', '-preset', 'veryfast', '-crf', '18']
                else:
                    vcodec = ['-c:v', 'libx264', '-preset', 'veryfast', '-b:v', f'{video_bitrate_kbps}k']
            else:
                vcodec = ['-c:v', hw_encoder]
                if video_bitrate_kbps is not None:
                    kbps = int(video_bitrate_kbps)
                    vcodec += ['-b:v', f'{kbps}k', '-maxrate', f'{int(kbps*1.05)}k', '-bufsize', f'{int(kbps*1.2)}k']
                vcodec += ['-g', '60', '-keyint_min', '60']
                if hw_encoder == 'h264_nvenc':
                    strict_size = (effective_duration <= 20.0)
                    vcodec += ['-forced-idr', '1', '-b_ref_mode', 'disabled']
                    if strict_size:
                        vcodec += ['-rc', 'cbr', '-tune', 'hq', '-rc-lookahead', '0', '-bf', '0']
                        rc_label = "NVENC CBR (Strict)"
                    else:
                        vcodec += ['-rc', 'vbr', '-tune', 'hq', '-multipass', '2', '-rc-lookahead', '8', '-bf', '1']
                        rc_label = "NVENC VBR (HQ)"
                elif hw_encoder == 'h264_amf':
                    vcodec += ['-usage', 'transcoding', '-quality', 'quality', '-rc', 'vbr_peak']
                    rc_label = "AMD AMF"
                elif hw_encoder == 'h264_qsv':
                    vcodec += ['-preset', 'medium', '-look_ahead', '0']
                    rc_label = "Intel QSV"
                else:
                    rc_label = f"{hw_encoder} (Generic)"
            if forced_cpu:
                self.status_update_signal.emit("Processing video (CPU libx264).")
            else:
                self.status_update_signal.emit(f"Processing video ({rc_label}).")
            cmd += vcodec
            cmd += [
                '-pix_fmt', 'yuv420p',
                '-movflags', '+faststart',
            ]
            cmd += ['-loglevel', 'info']
            cmd += ['-fflags', '+genpts', '-avoid_negative_ts', 'make_zero', '-muxpreload', '0', '-muxdelay', '0']
            filter_complex_parts = []
            map_args = []
            core_filters = []
            if self.is_mobile_format:
                vcore = f"[0:v]{video_filter_cmd}," if video_filter_cmd else "[0:v]"
                if not getattr(self, "disable_fades", False):
                    vcore += f"fade=t=in:st=0:d={vfade_in_d:.3f},fade=t=out:st={vfade_out_st:.3f}:d={vfade_out_d:.3f},"
                core_filters.append(
                    f"{vcore}format=yuv420p,trim=duration={self.duration_corrected:.6f},setpts=PTS-STARTPTS,setsar=1,"
                    f"fps=60[vcore]"
                )
            else:
                vcore = f"[0:v]{video_filter_cmd}," if video_filter_cmd else "[0:v],"
                if not getattr(self, "disable_fades", False):
                    vcore += f"fade=t=in:st=0:d={vfade_in_d:.3f},fade=t=out:st={vfade_out_st:.3f}:d={vfade_out_d:.3f},"
                core_filters.append(
                    f"{vcore}format=yuv420p,trim=duration={self.duration_corrected:.6f},setpts=PTS-STARTPTS,setsar=1,"
                    f"fps=60[vcore]"
                )
            has_audio = False
            if _src_kbps:
                has_audio = True
            else:
                try:
                    chk_cmd = [os.path.join(self.bin_dir, 'ffprobe.exe'), "-v", "error", "-select_streams", "a:0", "-show_entries", "stream=index", "-of", "csv=p=0", self.input_path]
                    si = subprocess.STARTUPINFO()
                    si.dwFlags |= subprocess.STARTF_USESHOWWINDOW
                    rr = subprocess.run(chk_cmd, capture_output=True, text=True, startupinfo=si, creationflags=subprocess.CREATE_NO_WINDOW)
                    if rr.stdout.strip(): has_audio = True
                except: pass
            audio_src_node = "[0:a]"
            audio_pre_chain = ""
            if not has_audio:
                self.logger.warning("No audio track found in input. Generating silence to prevent crash.")
                audio_src_node = "[a_silence]"
                audio_pre_chain = f"anullsrc=r=48000:cl=stereo,atrim=duration={effective_duration:.3f},asetpts=PTS-STARTPTS[a_silence];"
            if have_bg:
                if audio_filter_cmd:
                    core_filters.append(f"{audio_pre_chain}{audio_src_node}{audio_filter_cmd},aresample=48000,asetpts=PTS-STARTPTS[a_main_speed_corrected]")
                else:
                    core_filters.append(f"{audio_pre_chain}{audio_src_node}anull,aresample=48000,asetpts=PTS-STARTPTS[a_main_speed_corrected]")
                vol = self.bg_music_volume
                try:
                    vol = float(vol) if vol is not None else 0.35
                except Exception:
                    vol = 0.35
                vol = max(0.0, min(1.0, vol))
                mo = max(0.0, float(self.bg_music_offset or 0.0))
                music_needed_duration = self.duration_corrected
                a1_chain = (
                    f"atrim=start={mo:.3f}:duration={music_needed_duration:.3f},"
                    f"asetpts=PTS-STARTPTS,volume={vol:.4f},aresample=48000"
                )
                if not self.disable_fades:
                    music_fade_out_start = max(0.0, music_needed_duration - 1.5)
                    a1_chain += (
                        f",afade=t=in:st=0:d=1.5"
                        f",afade=t=out:st={music_fade_out_start:.3f}:d=1.5"
                    )
                core_filters.append(f"[1:a]{a1_chain}[a_music_prepared]")
                core_filters.append("[a_main_speed_corrected]asplit=3[a_main_final][a_trig_spectral][a_trig_vol]")
                core_filters.append("[a_trig_spectral]asplit=3[t_raw_low][t_raw_mid][t_raw_high]")
                core_filters.append("[t_raw_low]lowpass=f=250[t_low]")
                core_filters.append("[t_raw_mid]highpass=f=250,lowpass=f=4000[t_mid]")
                core_filters.append("[t_raw_high]highpass=f=4000[t_high]")
                core_filters.append("[a_music_prepared]asplit=3[m_raw_low][m_raw_mid][m_raw_high]")
                core_filters.append("[m_raw_low]lowpass=f=250[m_low]")
                core_filters.append("[m_raw_mid]highpass=f=250,lowpass=f=4000[m_mid]")
                core_filters.append("[m_raw_high]highpass=f=4000[m_high]")
                duck_params = "threshold=0.1:ratio=3:attack=5:release=200"
                core_filters.append(f"[m_low][t_low]sidechaincompress={duck_params}[m_low_ducked]")
                core_filters.append(f"[m_mid][t_mid]sidechaincompress={duck_params}[m_mid_ducked]")
                core_filters.append(f"[m_high][t_high]sidechaincompress={duck_params}[m_high_ducked]")
                core_filters.append("[m_low_ducked][m_mid_ducked][m_high_ducked]amix=inputs=3:weights=1 1 1:normalize=0[a_music_carved]")
                core_filters.append(
                    f"[a_music_carved][a_trig_vol]sidechaincompress=threshold=0.2:ratio=1.5:attack=30:release=500[a_music_final_ready]"
                )
                core_filters.append(
                    "[a_main_final][a_music_final_ready]"
                    "amix=inputs=2:duration=first:dropout_transition=3,aresample=48000[acore]"
                )
            else:
                if audio_filter_cmd:
                    core_filters.append(f"{audio_pre_chain}{audio_src_node}{audio_filter_cmd},aresample=48000,asetpts=PTS-STARTPTS[acore]")
                else:
                    core_filters.append(f"{audio_pre_chain}{audio_src_node}anull,aresample=48000,asetpts=PTS-STARTPTS[acore]")
            core_path = os.path.join(temp_dir, f"core-{os.getpid()}-{int(time.time())}.mp4")
            final_output_target_path = output_path
            core_cmd = [
                ffmpeg_path, '-y', '-hwaccel', 'auto',
                '-ss', f"{in_ss:.3f}", '-t', f"{in_t:.3f}",
                '-i', self.input_path,
            ]
            if have_bg:
                core_cmd += ['-i', self.bg_music_path]
            core_cmd += vcodec + [
                '-pix_fmt', 'yuv420p', '-movflags', '+faststart',
                '-c:a', 'aac', '-b:a', f'{AUDIO_KBPS}k', '-ar', '48000',
                '-filter_complex', ';'.join(core_filters),
                '-map', '[vcore]', '-map', '[acore]', '-shortest',
                core_path
            ]
            if self.is_canceled: return
            self.logger.info(f"STEP 1/3 CORE: {' '.join(map(str, core_cmd))}")
            core_progress_weight = 0.8 if self.intro_still_sec > 0 else 1.0
            startupinfo = None
            if sys.platform == "win32":
                startupinfo = subprocess.STARTUPINFO()
                startupinfo.dwFlags |= subprocess.STARTF_USESHOWWINDOW
            safe_flags = subprocess.CREATE_NO_WINDOW if sys.platform == "win32" else 0
            self.current_process = subprocess.Popen(
                core_cmd,
                stdout=subprocess.PIPE,
                stderr=subprocess.STDOUT,
                stdin=subprocess.DEVNULL,
                text=True,
                creationflags=safe_flags,
                startupinfo=startupinfo,
                encoding="utf-8",
                errors="replace"
            )
            while True:
                if self.is_canceled: break
                line = self.current_process.stdout.readline()
                if not line and self.current_process.poll() is not None:
                    break
                if line:
                    s = line.strip()
                    if s:
                        self.logger.info(s)
                        match = time_regex.search(s)
                        if match:
                            current_time_str = match.group(1).split('.')[0]
                            current_seconds = self._parse_time_to_seconds(current_time_str)
                            if self.duration_corrected > 0:
                                percent = (current_seconds / self.duration_corrected)
                                progress = int(max(0, min(100 * core_progress_weight, percent * 100 * core_progress_weight)))
                                self.progress_update_signal.emit(progress)
            self.current_process.wait()
            if self.is_canceled: 
                self.logger.info("Thread stopping: Cancelled after Step 1.")
                return
            if self.current_process.returncode != 0:
                self.logger.warning(f"Primary encoder '{hw_encoder}' failed. Starting hardware cascade...")
                fallback_queue = ["h264_nvenc", "h264_amf", "h264_qsv", "libx264"]
                try:
                    start_idx = fallback_queue.index(hw_encoder) + 1
                except ValueError:
                    start_idx = 0
                success = False
                for encoder in fallback_queue[start_idx:]:
                    self.logger.info(f"Attempting fallback to: {encoder}")
                    self.status_update_signal.emit(f"Hardware failure. Trying {encoder}...")
                    cmd_retry = [ffmpeg_path, '-y', '-ss', f"{in_ss:.3f}", '-t', f"{in_t:.3f}", '-i', self.input_file_path]
                    if have_bg: cmd_retry += ['-i', self.bg_music_path]
                    if encoder == "libx264":
                        cmd_retry += ['-c:v', 'libx264', '-preset', 'medium', '-crf', '23']
                    else:
                        cmd_retry += ['-c:v', encoder, '-b:v', f'{video_bitrate_kbps}k']
                    cmd_retry += [
                        '-pix_fmt', 'yuv420p', '-movflags', '+faststart',
                        '-c:a', 'aac', '-b:a', f'{AUDIO_KBPS}k', '-ar', '48000',
                        '-filter_complex', ';'.join(core_filters),
                        '-map', '[vcore]', '-map', '[acore]', '-shortest', core_path
                    ]
                    retry_proc = subprocess.Popen(cmd_retry, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, 
                                                text=True, creationflags=safe_flags, startupinfo=startupinfo)
                    for line in retry_proc.stdout:
                        if self.is_canceled: break
                        self.logger.info(line.strip())
                    retry_proc.wait()
                    if retry_proc.returncode == 0:
                        self.logger.info(f"Fallback to {encoder} succeeded.")
                        success = True
                        break
                if not success:
                    self.finished_signal.emit(False, "All hardware and software encoders failed.")
                    return
                cmd_cpu += [
                    '-pix_fmt', 'yuv420p', '-movflags', '+faststart',
                    '-c:a', 'aac', '-b:a', f'{AUDIO_KBPS}k', '-ar', '48000',
                    '-filter_complex', ';'.join(core_filters),
                    '-map', '[vcore]', '-map', '[acore]', '-shortest',
                    core_path
                ]
                self.logger.info(f"Retry STEP 1/3 with CPU (libx264): {' '.join(cmd_cpu)}")
                startupinfo = None
                if sys.platform == "win32":
                    startupinfo = subprocess.STARTUPINFO()
                    startupinfo.dwFlags |= subprocess.STARTF_USESHOWWINDOW
                self.current_process = subprocess.Popen(
                    cmd_cpu,
                    stdout=subprocess.PIPE,
                    stderr=subprocess.STDOUT,
                    stdin=subprocess.DEVNULL,
                    text=True,
                    creationflags=(subprocess.CREATE_NO_WINDOW if sys.platform == 'win32' else 0),
                    startupinfo=startupinfo,
                    encoding='utf-8',
                    errors='replace'
                )
                while True:
                    if self.is_canceled: break
                    line = self.current_process.stdout.readline()
                    if not line and self.current_process.poll() is not None:
                        break
                    if line:
                        s = line.strip()
                        if s:
                            self.logger.info(s)
                            match = time_regex.search(s)
                            if match:
                                current_time_str = match.group(1).split('.')[0]
                                current_seconds = self._parse_time_to_seconds(current_time_str)
                                if self.duration_corrected > 0:
                                    percent = (current_seconds / self.duration_corrected)
                                    progress = int(max(0, min(100 * core_progress_weight, percent * 100 * core_progress_weight)))
                                    self.progress_update_signal.emit(progress)
                self.current_process.wait()
                if self.is_canceled: return
                if self.current_process.returncode != 0:
                    self.finished_signal.emit(False, "Core encode failed (STEP 1/3) after GPU and CPU retries.")
                    return
                else:
                    self.logger.info("CPU fallback succeeded after GPU failure.")
            if self.is_canceled:
                self.logger.info("Thread stopping: Cancelled before Intro step.")
                return
            if self.intro_still_sec > 0:
                if self.intro_abs_time is None and self.intro_from_midpoint:
                    mid = (user_start + user_end) / 2.0
                    if total_orig > 0.0:
                        mid = min(max(0.0, mid), max(0.0, total_orig - 0.05))
                    self.intro_abs_time = float(mid)
                    self.logger.info("INTRO: no user pick; using midpoint %.3fs as thumbnail.", self.intro_abs_time)
                if self.intro_abs_time is not None:
                    self.logger.info(
                        "DEBUG: Intro step enabled. still=%.3fs  abs=%.3fs",
                        float(self.intro_still_sec or 0.1), self.intro_abs_time
                    )
                    intro_path = os.path.join(temp_dir, f"intro-{os.getpid()}-{int(time.time())}.mp4")
                    still_len = max(0.01, float(self.intro_still_sec or 0.1))
                    loop_frames = max(1, int(round(still_len * 60)))
                    base_intro_filter = (
                        f"select='eq(n\\,0)',format=yuv420p,setsar=1,"
                        f"loop=loop={loop_frames}:size=1:start=0,setpts=N/60/TB,fps=60[vintro];"
                        f"anullsrc=r=48000:cl=stereo,atrim=duration={still_len:.3f},asetpts=PTS-STARTPTS[aintro]"
                    )
                    if self.is_mobile_format:
                        main_width = 1080
                        main_height = 1920
                        intro_filter = (
                            f"[0:v]scale={main_width}:{main_height}:force_original_aspect_ratio=increase,"
                            f"crop={main_width}:{main_height},{base_intro_filter}"
                        )
                        self.logger.info(
                            "INTRO: abs=%.3fs len=%.3fs (portrait crop 1080)", self.intro_abs_time, still_len
                        )
                    else:
                        intro_filter = f"[0:v]{base_intro_filter}"
                        self.logger.info(
                            "INTRO: abs=%.3fs len=%.3fs (keep aspect)", self.intro_abs_time, still_len
                        )
                    if os.environ.get('VIDEO_FORCE_CPU') == '1':
                        vcodec_intro = ['-c:v', 'libx264', '-preset', 'veryfast', '-crf', '23']
                    else:
                        vcodec_intro = ['-c:v', hw_encoder]
                        if video_bitrate_kbps:
                            vcodec_intro += ['-b:v', f'{video_bitrate_kbps}k', '-maxrate', f'{video_bitrate_kbps}k', '-bufsize', f'{int(video_bitrate_kbps*1.0)}k']
                        vcodec_intro += ['-g', '60', '-keyint_min', '60']
                        if hw_encoder == 'h264_nvenc':
                            vcodec_intro += ['-rc', 'cbr', '-tune', 'hq', '-rc-lookahead', '0', '-bf', '0', '-forced-idr', '1', '-b_ref_mode', 'disabled']
                        elif hw_encoder == 'h264_amf':
                            vcodec_intro += ['-usage', 'transcoding', '-quality', 'quality', '-rc', 'cbr']
                        elif hw_encoder == 'h264_qsv':
                            vcodec_intro += ['-preset', 'medium', '-look_ahead', '0']
                    intro_cmd_base = [
                        ffmpeg_path, "-y", "-hwaccel", "auto",
                        "-ss", f"{self.intro_abs_time:.6f}",
                        "-i", self.input_path,
                        "-t", "0.2",
                    ]
                    intro_cmd = intro_cmd_base + vcodec_intro + [
                        "-pix_fmt", "yuv420p", "-movflags", "+faststart",
                        "-c:a", "aac", "-b:a", f'{AUDIO_KBPS}k', '-ar', '48000',
                        "-filter_complex", intro_filter,
                        "-map", "[vintro]", "-map", "[aintro]", "-shortest", intro_path
                    ]
                    self.logger.info("STEP 2/3 INTRO (GPU/Attempt 1): %s", " ".join(intro_cmd))
                    startupinfo = None
                    if sys.platform == "win32":
                        startupinfo = subprocess.STARTUPINFO()
                        startupinfo.dwFlags |= subprocess.STARTF_USESHOWWINDOW
                    if self.is_canceled: return
                    self.current_process = subprocess.Popen(
                        intro_cmd,
                        stdout=subprocess.PIPE,
                        stderr=subprocess.STDOUT,
                        stdin=subprocess.DEVNULL,
                        universal_newlines=True,
                        creationflags=(subprocess.CREATE_NO_WINDOW if sys.platform == "win32" else 0),
                        startupinfo=startupinfo,
                        encoding="utf-8",
                        errors="replace"
                    )
                    for line in self.current_process.stdout:
                        self.logger.info(line.rstrip())
                        self.progress_update_signal.emit(95)
                    self.current_process.wait()
                    if self.is_canceled: return
                    if self.current_process.returncode != 0:
                        self.logger.warning(f"Intro encoder '{hw_encoder}' failed. Cascading...")
                        fallback_queue = ["h264_nvenc", "h264_amf", "h264_qsv", "libx264"]
                        try:
                            start_idx = fallback_queue.index(hw_encoder) + 1
                        except ValueError:
                            start_idx = 0
                        intro_success = False
                        for encoder in fallback_queue[start_idx:]:
                            self.logger.info(f"Intro fallback attempt: {encoder}")
                            vcodec_fallback = ['-c:v', encoder]
                            if encoder == 'libx264':
                                vcodec_fallback += ['-preset', 'medium', '-crf', '23']
                            else:
                                vcodec_fallback += ['-b:v', f'{video_bitrate_kbps}k', '-g', '60']
                            cmd_intro_retry = intro_cmd_base + vcodec_fallback + [
                                "-pix_fmt", "yuv420p", "-movflags", "+faststart",
                                "-c:a", "aac", "-b:a", f'{AUDIO_KBPS}k', '-ar', '48000',
                                "-filter_complex", intro_filter,
                                "-map", "[vintro]", "-map", "[aintro]", "-shortest", intro_path
                            ]
                            retry_proc = subprocess.Popen(cmd_intro_retry, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, 
                                                        text=True, creationflags=safe_flags, startupinfo=startupinfo)
                            retry_proc.wait()
                            if retry_proc.returncode == 0:
                                self.logger.info(f"Intro fallback to {encoder} succeeded.")
                                intro_success = True
                                break
                        if not intro_success:
                            self.finished_signal.emit(False, "Intro encoding failed across all available hardware.")
                            return
                        self.logger.info(f"Retry STEP 2/3 with CPU (libx264): {' '.join(intro_cmd_cpu)}")
                        startupinfo = None
                        if sys.platform == "win32":
                            startupinfo = subprocess.STARTUPINFO()
                            startupinfo.dwFlags |= subprocess.STARTF_USESHOWWINDOW
                        if self.is_canceled: return
                        self.current_process = subprocess.Popen(
                            intro_cmd_cpu,
                            stdout=subprocess.PIPE,
                            stderr=subprocess.STDOUT,
                            stdin=subprocess.DEVNULL,
                            universal_newlines=True,
                            creationflags=(subprocess.CREATE_NO_WINDOW if sys.platform == "win32" else 0),
                            startupinfo=startupinfo,
                            encoding="utf-8",
                            errors="replace"
                        )
                        for line in self.current_process.stdout:
                            self.logger.info(line.rstrip())
                            self.progress_update_signal.emit(95)
                        self.current_process.wait()
                        if self.is_canceled: return
                        if self.current_process.returncode != 0:
                            self.finished_signal.emit(False, "Intro encode failed (STEP 2/3) after GPU and CPU retries.")
                            return
                        else:
                            self.logger.info("Intro CPU fallback succeeded after GPU failure.")
                else:
                    self.logger.info("Skipping Intro: no absolute time resolved (user pick or midpoint).")
                    intro_path = None
            else:
                self.logger.info("Skipping Intro: disabled (intro_still_sec<=0).")
                intro_path = None
            if self.is_canceled:
                self.logger.info("Thread stopping: Cancelled before Concat step.")
                return
            concat_list_path = os.path.join(temp_dir, f"concat-{os.getpid()}-{int(time.time())}.txt")
            files_to_concat = []
            if intro_path and os.path.exists(intro_path):
                files_to_concat.append(intro_path)
            if core_path and os.path.exists(core_path):
                files_to_concat.append(core_path)
            else:
                self.finished_signal.emit(False, "Core video file is missing for concatenation.")
                return
            if len(files_to_concat) == 1:
                self.logger.info("STEP 3/3 CONCAT: Skipping concat, renaming core file.")
                try:
                    shutil.move(files_to_concat[0], output_path)
                    core_path = None
                    self.progress_update_signal.emit(100)
                    self.logger.info(
                        f"Job SUCCESS | start={self.start_time}s end={self.end_time}s | out='{output_path}'"
                    )
                    if not self.is_canceled:
                        self.finished_signal.emit(True, output_path)
                    return
                except Exception as move_err:
                    if not self.is_canceled:
                        self.finished_signal.emit(False, f"Failed to move final video: {move_err}")
                    return
            elif len(files_to_concat) > 1:
                with open(concat_list_path, "w", encoding="utf-8") as fcat:
                    for f in files_to_concat:
                        fcat.write(f"file '{f.replace('\\', '/')}'\n")
                concat_cmd = [
                    ffmpeg_path, "-y",
                    "-f", "concat", "-safe", "0",
                    "-i", concat_list_path,
                    "-c", "copy", "-movflags", "+faststart",
                    output_path
                ]
            else:
                if not self.is_canceled:
                    self.finished_signal.emit(False, "No video files found for final step.")
                return
            if self.is_canceled: return
            self.logger.info(f"STEP 3/3 CONCAT: {' '.join(concat_cmd)}")
            startupinfo = None
            if sys.platform == "win32":
                startupinfo = subprocess.STARTUPINFO()
                startupinfo.dwFlags |= subprocess.STARTF_USESHOWWINDOW
            self.current_process = subprocess.Popen(
                concat_cmd,
                stdout=subprocess.PIPE,
                stderr=subprocess.STDOUT,
                stdin=subprocess.DEVNULL,
                universal_newlines=True,
                creationflags=(subprocess.CREATE_NO_WINDOW if sys.platform == "win32" else 0),
                startupinfo=startupinfo,
                encoding="utf-8",
                errors="replace"
            )
            for line in self.current_process.stdout:
                self.logger.info(line.rstrip())
                self.progress_update_signal.emit(99)
            self.current_process.wait()
            if self.is_canceled: return
            if self.current_process.returncode != 0:
                if not self.is_canceled:
                    self.finished_signal.emit(False, "Concat failed (STEP 3/3).")
                return
            self.progress_update_signal.emit(100)
            self.logger.info(
                f"Job SUCCESS | start={self.start_time}s end={self.end_time}s | out='{output_path}'"
            )
            if not self.is_canceled:
                self.finished_signal.emit(True, output_path)
            return
        except Exception as e:
            if not self.is_canceled:
                self.logger.exception(f"Job FAILURE with exception: {e}")
                self.finished_signal.emit(False, f"An unexpected error occurred: {e}.")
        finally:
            if self.is_canceled:
                self.logger.info("Process was canceled. Cleaning up temporary files.")
                self.finished_signal.emit(False, "Processing was canceled by user.")
                if output_path and os.path.exists(output_path):
                    try:
                        os.remove(output_path)
                        self.logger.info(f"Removed incomplete output file: {output_path}")
                    except Exception as e:
                        self.logger.error(f"Failed to remove incomplete output file: {e}")
            if getattr(self, "_progress_timer", None):
                try:
                    self._progress_timer.stop()
                except Exception:
                    pass
            for p in [core_path, intro_path, concat_path]:
                if p and os.path.exists(p):
                    try: os.remove(p)
                    except Exception: pass
            for ext in ["", "-0.log", "-1.log", ".log", ".log-0.log", ".log-1.log"]:
                try:
                    os.remove(temp_log_path.replace(".log", ext))
                except Exception:
                    pass