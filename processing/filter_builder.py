import os
from typing import Tuple
from .text_ops import safe_text
TARGET_W = 1280
TARGET_H = 1920
CONTENT_W = 1080
CONTENT_H = 1620

def inverse_transform_from_content_area(rect: Tuple[float, float, float, float],
                                        original_resolution: str) -> Tuple[float, float, float, float]:
    if not original_resolution:
        return rect
    try:
        in_w, in_h = map(int, original_resolution.split('x'))
        if in_w <= 0 or in_h <= 0:
            return rect
    except (ValueError, AttributeError):
        return rect
    x_content, y_content, w_content, h_content = rect
    x_canvas = x_content * 1280.0 / 1080.0
    y_canvas = y_content * 1280.0 / 1080.0
    w_canvas = w_content * 1280.0 / 1080.0
    h_canvas = h_content * 1280.0 / 1080.0
    scale_w = 1280.0 / in_w
    scale_h = 1920.0 / in_h
    scale = max(scale_w, scale_h)
    crop_x = (in_w * scale - 1280.0) / 2.0
    crop_y = (in_h * scale - 1920.0) / 2.0
    x_scaled = x_canvas + crop_x
    y_scaled = y_canvas + crop_y
    x_original = x_scaled / scale
    y_original = y_scaled / scale
    w_original = w_canvas / scale
    h_original = h_canvas / scale
    w_original = max(1.0, min(w_original, in_w))
    h_original = max(1.0, min(h_original, in_h))
    x_original = max(0.0, min(x_original, in_w - w_original))
    y_original = max(0.0, min(y_original, in_h - h_original))
    return (x_original, y_original, w_original, h_original)

def inverse_transform_from_content_area_int(rect: Tuple[int, int, int, int],
                                            original_resolution: str) -> Tuple[int, int, int, int]:
    x, y, w, h = rect
    fx, fy, fw, fh = inverse_transform_from_content_area(
        (float(x), float(y), float(w), float(h)), original_resolution
    )
    EPSILON = 0.001
    return (
        int(round(fx + EPSILON)),
        int(round(fy + EPSILON)),
        int(round(fw + EPSILON)),
        int(round(fh + EPSILON))
    )
COORDINATE_MATH_AVAILABLE = True

class FilterBuilder:
    def __init__(self, logger):
        self.logger = logger

    def _drawtext(self, text, x, y, size, color, font_path, alpha=1.0):
        safe_t = safe_text(text)
        alpha_hex = hex(int(alpha * 255))[2:].zfill(2)
        return (
            f"drawtext=fontfile='{font_path}':text='{safe_t}':"
            f"text_shaping=1:"
            f"fontsize={size}:fontcolor={color}{alpha_hex}:"
            f"x={x}:y={y}:shadowcolor=black@0.6:shadowx=2:shadowy=2"
        )

    def build_nvidia_resize(self, target_w, target_h, keep_highest_res=False):
        """
        Builds a CUDA-accelerated scaling filter.
        """
        if keep_highest_res:
            return "scale_cuda=format=nv12"
        if target_w == 1920:
             return "scale_cuda=1920:-2:interp_algo=lanczos:format=nv12"
        elif target_w == 1280:
             return "scale_cuda=1280:-2:interp_algo=lanczos:format=nv12"
        elif target_w == 960:
             return "scale_cuda=960:-2:interp_algo=lanczos:format=nv12"
        return f"scale_cuda={target_w}:-2:interp_algo=lanczos:format=nv12"

    def build_mobile_filter(self, mobile_coords, original_res_str, is_boss_hp, show_teammates):
        coords_data = mobile_coords
        
        def get_rect(section, key):
            return tuple(coords_data.get(section, {}).get(key, [0,0,0,0]))
        scales = coords_data.get('scales', {})
        overlays = coords_data.get('overlays', {})
        hp_key = 'boss_hp' if is_boss_hp else 'normal_hp'
        hp_1080 = get_rect('crops_1080p', hp_key)
        hp_scale = float(scales.get(hp_key, 1.0))
        hp_ov = overlays.get(hp_key, {'x': 0, 'y': 0})
        self.logger.info(f"Using {'Boss' if is_boss_hp else 'Normal'} HP coordinates.")
        loot_1080 = get_rect('crops_1080p', 'loot')
        loot_scale = float(scales.get('loot', 1.0))
        stats_1080 = get_rect('crops_1080p', 'stats')
        stats_scale = float(scales.get('stats', 1.0))
        team_1080 = get_rect('crops_1080p', 'team')
        team_scale = float(scales.get('team', 1.0))
        try:
            res_parts = original_res_str.split('x')
            if len(res_parts) != 2:
                raise ValueError("Invalid resolution format")
            in_w, in_h = map(int, res_parts)
        except (ValueError, AttributeError):
            in_w, in_h = 1920, 1080
            self.logger.warning(f"Failed to parse resolution '{original_res_str}', defaulting to 1920x1080")
        self.logger.info(f"Mobile Crop Processing: Input={in_w}x{in_h}")

        def get_original_crop(crop_ui):
            w_ui, h_ui, x_ui, y_ui = crop_ui
            transformed = inverse_transform_from_content_area_int((x_ui, y_ui, w_ui, h_ui), original_res_str)
            return (transformed[2], transformed[3], transformed[0], transformed[1])
        hp_orig = get_original_crop(hp_1080)
        loot_orig = get_original_crop(loot_1080)
        stats_orig = get_original_crop(stats_1080)
        team_orig = get_original_crop(team_1080)
        f_main = "[main]scale=1280:1920:force_original_aspect_ratio=increase:flags=bilinear,crop=1280:1920[main_cropped]"
        
        def make_hud_filter(name, crop_orig, ui_w, ui_h, user_scale):
            crop_str = f"{crop_orig[0]}:{crop_orig[1]}:{crop_orig[2]}:{crop_orig[3]}"
            render_w = int(round(ui_w * user_scale * 1280 / 1080))
            render_h = int(round(ui_h * user_scale * 1280 / 1080))
            return f"[{name}]crop={crop_str},scale={render_w}:{render_h}:flags=bilinear,pad=iw+4:ih+4:2:2:black,format=yuva444p[{name}_scaled]"
        f_hp = make_hud_filter("healthbar", hp_orig, hp_1080[0], hp_1080[1], hp_scale)
        f_loot = make_hud_filter("lootbar", loot_orig, loot_1080[0], loot_1080[1], loot_scale)
        f_stats = make_hud_filter("stats", stats_orig, stats_1080[0], stats_1080[1], stats_scale)
        common_filters = f"{f_main};{f_hp};{f_loot};{f_stats}"
        z_orders = coords_data.get('z_orders', {})
        layers = [
            {'name': 'lootbar', 'filter_out': '[lootbar_scaled]', 'x': overlays.get('loot', {}).get('x', 0), 'y': overlays.get('loot', {}).get('y', 0), 'z': z_orders.get('loot', 10)},
            {'name': 'healthbar', 'filter_out': '[healthbar_scaled]', 'x': hp_ov.get('x', 0), 'y': hp_ov.get('y', 0), 'z': z_orders.get(hp_key, 20)},
            {'name': 'stats', 'filter_out': '[stats_scaled]', 'x': overlays.get('stats', {}).get('x', 0), 'y': overlays.get('stats', {}).get('y', 0), 'z': z_orders.get('stats', 30)}
        ]
        if show_teammates:
            f_team = make_hud_filter("team", team_orig, team_1080[0], team_1080[1], team_scale)
            common_filters += f";{f_team}"
            layers.append({'name': 'team', 'filter_out': '[team_scaled]', 'x': overlays.get('team', {}).get('x', 0), 'y': overlays.get('team', {}).get('y', 0), 'z': z_orders.get('team', 40)})
        layers.sort(key=lambda item: item['z'])
        overlay_chain = ""
        current_pad = "[main_cropped]"
        for i, layer in enumerate(layers):
            next_pad = f"[t{i+1}]" if i < len(layers) - 1 else "[vpreout]"
            raw_x = layer['x']
            raw_y = layer['y']
            if raw_x > 1080:
                lx = int(round(raw_x))
                ly = int(round(raw_y - (150 * 1280 / 1080)))
            else:
                lx = int(round(raw_x * 1280 / 1080))
                ly = int(round((raw_y - 150) * 1280 / 1080))
            overlay_chain += f"{current_pad}{layer['filter_out']}overlay={lx-2}:{ly-2}{next_pad};"
            current_pad = next_pad
        split_count = 5 if show_teammates else 4
        split_cmd = f"split={split_count}[main][healthbar][lootbar][stats]"
        if show_teammates: split_cmd += "[team]"
        video_filter_cmd = f"{split_cmd};{common_filters};{overlay_chain.rstrip(';')}"
        video_filter_cmd += ";[vpreout]scale=1080:-2,pad=1080:1920:(ow-iw)/2:(oh-ih)/2:black"
        return video_filter_cmd
        if overlay_chain.endswith(";"):
            overlay_chain = overlay_chain[:-1]
        split_cmd = f"split={split_count}[main][lootbar][healthbar][stats]"
        if show_teammates:
            split_cmd += "[team]"
        video_filter_cmd = (
            f"{split_cmd};"
            f"{common_filters};"
            f"{overlay_chain}"
        )
        video_filter_cmd += ";[vpreout]scale=1080:-2,pad=1080:1920:(ow-iw)/2:(oh-ih)/2:black"
        return video_filter_cmd

    def add_drawtext_filter(self, video_filter_cmd, text_file_path, font_px, line_spacing):
        ff_textfile = text_file_path.replace("\\", "/").replace(":", "\\:").replace("'", "'\\\''")
        candidates = [
            os.path.join(os.environ.get('WINDIR', 'C:/Windows'), "Fonts", "arial.ttf"),
            os.path.join(os.environ.get('WINDIR', 'C:/Windows'), "Fonts", "segoeui.ttf"),
            "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
            "/System/Library/Fonts/Helvetica.ttc"
        ]
        font_path = "arial"
        for c in candidates:
            if os.path.exists(c):
                font_path = c.replace("\\", "/").replace(":", "\\:")
                break
        drawtext_parts = [
            f"drawtext=fontfile='{font_path}'",
            f"textfile='{ff_textfile}':reload=0:text_shaping=1",
            f"fontcolor=white:fontsize={int(font_px)}",
            f"x=(w-text_w)/2:y=(150-text_h)/2:line_spacing={line_spacing}",
            f"shadowcolor=black:shadowx=3:shadowy=3"
        ]
        drawtext_str = ":".join(drawtext_parts)
        return video_filter_cmd + "," + drawtext_str

    def build_granular_speed_chain(self, video_path, duration_ms, speed_segments, base_speed):
        """
        Builds a complex filter chain for variable speed segments.
        Returns:
            video_filter_complex_str: The full complex filter string.
            v_label: The output video pad label (e.g., "[v_speed_out]").
            a_label: The output audio pad label (e.g., "[a_speed_out]").
            final_duration_sec: The calculated total duration of the retimed video.
        """
        segments = sorted(speed_segments, key=lambda x: x['start'])
        chunks = []
        current_time = 0.0
        total_duration_sec = duration_ms / 1000.0
        for seg in segments:
            seg_start = seg['start'] / 1000.0
            seg_end = seg['end'] / 1000.0
            seg_speed = float(seg['speed'])
            if seg_start > current_time:
                chunks.append({
                    'start': current_time,
                    'end': seg_start,
                    'speed': float(base_speed)
                })
            chunks.append({
                'start': seg_start,
                'end': seg_end,
                'speed': seg_speed
            })
            current_time = seg_end
        if current_time < total_duration_sec:
            chunks.append({
                'start': current_time,
                'end': total_duration_sec,
                'speed': float(base_speed)
            })
        filter_parts = []
        v_pads = []
        a_pads = []
        final_duration = 0.0
        for i, chunk in enumerate(chunks):
            start = chunk['start']
            end = chunk['end']
            speed = chunk['speed']
            dur = end - start
            if dur <= 0.0001: 
                continue
            final_duration += (dur / speed)
            filter_parts.append(
                f"[0:v]trim=start={start:.4f}:end={end:.4f},setpts=(PTS-STARTPTS)/{speed:.4f}[v_chunk_{i}]"
            )
            v_pads.append(f"[v_chunk_{i}]")
            audio_speed_filters = []
            temp_speed = speed
            while temp_speed < 0.5:
                audio_speed_filters.append("atempo=0.5")
                temp_speed /= 0.5
            while temp_speed > 2.0:
                audio_speed_filters.append("atempo=2.0")
                temp_speed /= 2.0
            audio_speed_filters.append(f"atempo={temp_speed:.4f}")
            audio_speed_cmd = ",".join(audio_speed_filters)
            chunk_audio_dur = dur / speed
            fade_dur = min(0.005, chunk_audio_dur / 2)
            filter_parts.append(
                f"[0:a]atrim=start={start:.4f}:end={end:.4f},asetpts=PTS-STARTPTS,"
                f"{audio_speed_cmd},"
                f"afade=t=in:st=0:d={fade_dur:.4f},"
                f"afade=t=out:st={chunk_audio_dur - fade_dur:.4f}:d={fade_dur:.4f}"
                f"[a_chunk_{i}]"
            )
            a_pads.append(f"[a_chunk_{i}]")
        n_chunks = len(v_pads)
        if n_chunks == 0:
            return f"[0:v]null[v_speed_out];[0:a]anull[a_speed_out]", "[v_speed_out]", "[a_speed_out]", total_duration_sec
        v_concat_in = "".join(v_pads)
        a_concat_in = "".join(a_pads)
        filter_parts.append(
            f"{v_concat_in}concat=n={n_chunks}:v=1:a=0[v_speed_out]"
        )
        filter_parts.append(
            f"{a_concat_in}concat=n={n_chunks}:v=0:a=1[a_speed_out]"
        )
        full_filter = ";".join(filter_parts)
        return full_filter, "[v_speed_out]", "[a_speed_out]", final_duration

    def build_audio_chain(self, music_config, video_start_time, video_end_time, speed_factor, disable_fades, vfade_in_d, audio_filter_cmd):
        chain = []
        main_audio_filter_parts = [audio_filter_cmd if audio_filter_cmd else "anull"]
        if vfade_in_d > 0:
            main_audio_filter_parts.append(f"afade=t=in:st=0:d={vfade_in_d:.3f}")
        main_audio_filter = ",".join(main_audio_filter_parts)
        chain.append(f"[0:a]{main_audio_filter},aresample=48000,asetpts=PTS-STARTPTS[a_main_prepared]")
        if music_config and music_config.get("path"):
            mc = music_config
            timeline_start = mc.get('timeline_start_sec', 0.0)
            user_end = mc.get('timeline_end_sec')
            if user_end is None:
                user_end = video_end_time
            else:
                user_end = float(user_end)
            file_offset = mc.get('file_offset_sec', 0.0)
            relative_start = timeline_start - video_start_time
            start_skip = 0.0
            delay_ms = 0
            if relative_start < 0:
                start_skip = abs(relative_start)
            else:
                delay_ms = int((relative_start / speed_factor) * 1000)
            final_start_pos = file_offset + start_skip
            eff_end = min(video_end_time, user_end)
            eff_start = video_start_time
            dur_v = max(0.0, eff_end - eff_start)
            dur_a = dur_v / speed_factor
            self.logger.info(f"Music Filter Calc: VideoStart={eff_start:.2f}, EffEnd={eff_end:.2f}, RawDur={dur_v:.2f}, AudioDur={dur_a:.2f}")
            music_filters = [
                f"atrim=start={final_start_pos:.3f}:duration={dur_a:.3f}",
                "asetpts=PTS-STARTPTS"
            ]
            if not disable_fades:
                FADE_DUR = 1.0
                if dur_a > (FADE_DUR * 2):
                    music_filters.append(f"afade=t=in:st=0:d={FADE_DUR}")
                    is_early_cut = (user_end < (video_end_time - 0.1))
                    if not is_early_cut:
                        out_st = max(0.0, dur_a - FADE_DUR)
                        music_filters.append(f"afade=t=out:st={out_st:.3f}:d={FADE_DUR}")
            raw_vol = mc.get('volume', mc.get('music_vol', 1.0))
            if raw_vol is None: raw_vol = 1.0
            vol = max(0.0, min(1.0, float(raw_vol)))
            music_filters.append(f"volume={vol:.4f}")
            music_filters.append("aresample=48000")
            chain.append(f"[1:a]{','.join(music_filters)}[a_music_prepared]")
            if delay_ms > 0:
                chain.append(f"[a_music_prepared]adelay={delay_ms}|{delay_ms}[a_music_delayed]")
                chain.append("[a_music_delayed]asplit=2[mus_base][mus_to_filter]")
            else:
                chain.append("[a_music_prepared]asplit=2[mus_base][mus_to_filter]")
            chain.append("[mus_base]lowpass=f=150[mus_low]")
            chain.append("[mus_to_filter]highpass=f=150[mus_high]")
            chain.append("[a_main_prepared]asplit=2[game_out][game_trig]")
            kill_switch_start = max(0, dur_a - 3.5)
            chain.append(f"[game_trig]afade=t=out:st={kill_switch_start:.3f}:d=0.5,highpass=f=200,lowpass=f=3500,agate=threshold=0.05:attack=5:release=100[trig_cleaned]")
            chain.append("[trig_cleaned]equalizer=f=1000:t=q:w=2:g=10[trig_final]")
            duck_params = "threshold=0.2:ratio=4:attack=1:release=400:detection=rms"
            chain.append(f"[mus_high][trig_final]sidechaincompress={duck_params}[mus_high_ducked]")
            chain.append("[mus_low][mus_high_ducked]amix=inputs=2:weights=1 1:normalize=0[a_music_reconstructed]")
            chain.append(
                "[game_out][a_music_reconstructed]"
                "amix=inputs=2:duration=first:dropout_transition=3:weights=1 1:normalize=0,"
                "alimiter=limit=0.95:attack=5:release=50[acore_pre_limiter]"
            )
            chain.append("[acore_pre_limiter]aresample=48000[acore]")
        else:
            chain.append("[a_main_prepared]anull[acore]")
        return chain