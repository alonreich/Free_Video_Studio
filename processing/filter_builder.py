import os
import tempfile
from typing import Tuple
try:
    from developer_tools.coordinate_math import (
        TARGET_W, TARGET_H, CONTENT_W, CONTENT_H, BACKEND_SCALE,
        PADDING_TOP, scale_round, inverse_transform_from_content_area_int
    )
except ImportError:
    import sys
    base_dir = os.path.abspath(os.path.join(os.path.dirname(__file__), '..'))
    if base_dir not in sys.path:
        sys.path.append(base_dir)

    from developer_tools.coordinate_math import (
        TARGET_W, TARGET_H, CONTENT_W, CONTENT_H, BACKEND_SCALE,
        PADDING_TOP, scale_round, inverse_transform_from_content_area_int
    )
COORDINATE_MATH_AVAILABLE = True

class FilterBuilder:
    def __init__(self, logger):
        self.logger = logger

    def _make_multiple(self, n, m=8):
        i = int(round(n))
        return (i // m) * m

    def _make_even(self, n):
        return self._make_multiple(n, 2)

    def _fps_to_float(self, fps_val):
        from fractions import Fraction
        try:
            return float(Fraction(str(fps_val)))
        except:
            try: return float(fps_val)
            except: return 60.0

    def build_mobile_filter(self, mobile_coords, original_res_str, is_boss_hp, show_teammates, use_nvidia=False, needs_text_overlay=False, use_hwaccel=False, needs_hw_download=True, target_fps=60, input_pad="[v_stabilized]", txt_input_label=None, speed_factor=1.0):
        coords_data = mobile_coords
        FINAL_W, FINAL_H = 1080, 1920
        CONTENT_AREA_W, CONTENT_AREA_H = 1080, 1620
        INTERNAL_W, INTERNAL_H = 1280, 1920
        CONTENT_OFFSET_Y = 150
        parts = []
        curr_input = input_pad

        def get_rect(section, key):
            return tuple(coords_data.get(section, {}).get(key, [0,0,0,0]))
        scales = coords_data.get("scales", {})
        overlays = coords_data.get("overlays", {})
        z_orders = coords_data.get("z_orders", {})
        hp_key = "boss_hp" if is_boss_hp else "normal_hp"
        active_layers = []

        def register_layer(name, conf_key, crop_key_1080, ov_key):
            rect_1080 = get_rect("crops_1080p", crop_key_1080)
            sc = float(scales.get(conf_key, 1.0))
            if rect_1080 and rect_1080[0] >= 1:
                w_ui, h_ui, x_ui, y_ui = rect_1080
                transformed = inverse_transform_from_content_area_int((x_ui, y_ui, w_ui, h_ui), original_res_str)
                crop_orig = (transformed[2], transformed[3], transformed[0], transformed[1])
                ov = overlays.get(ov_key, {"x": 0, "y": 0})
                z = z_orders.get(ov_key, 50)
                active_layers.append({
                    "name": name, "crop_orig": crop_orig, "ui_wh": (rect_1080[0], rect_1080[1]),
                    "scale": sc, "pos": (ov["x"], ov["y"]), "z": z
                })
        register_layer("healthbar", hp_key, hp_key, hp_key)
        register_layer("lootbar", "loot", "loot", "loot")
        register_layer("stats", "stats", "stats", "stats")
        register_layer("spectating", "spectating", "spectating", "spectating")
        if show_teammates: register_layer("team", "team", "team", "team")
        active_layers.sort(key=lambda x: x["z"])
        split_count = 1 + len(active_layers)
        parts.append(f"{curr_input}split={split_count}[main_base_sw]" + "".join([f"[{l['name']}_in_sw]" for l in active_layers]))
        f_main_sw = f"scale={INTERNAL_W}:{INTERNAL_H}:force_original_aspect_ratio=increase:flags=bicubic,crop={INTERNAL_W}:{INTERNAL_H}:(iw-{INTERNAL_W})/2:(ih-{INTERNAL_H})/2"
        parts.append(f"[main_base_sw]{f_main_sw}[main_base_composed]")
        current_pad = "[main_base_composed]"
        for i, layer in enumerate(active_layers):
            cw, ch, cx, cy = layer['crop_orig']
            rw = max(2, self._make_even(layer['ui_wh'][0] * layer['scale'] * BACKEND_SCALE))
            rh = max(2, self._make_even(layer['ui_wh'][1] * layer['scale'] * BACKEND_SCALE))
            lx, ly = self._make_even(float(layer['pos'][0]) * BACKEND_SCALE), self._make_even(float(layer['pos'][1]) * BACKEND_SCALE)
            next_pad = f"[t{i}]" if i < len(active_layers) - 1 else "[internal_composed_sw]"
            parts.append(f"[{layer['name']}_in_sw]crop={cw}:{ch}:{cx}:{cy},scale={rw}:{rh}:flags=lanczos[{layer['name']}_out_sw]")
            parts.append(f"{current_pad}[{layer['name']}_out_sw]overlay=x={lx}:y={ly}:eof_action=pass{next_pad}")
            current_pad = next_pad
        if not active_layers:
            parts.append(f"[main_base_composed]null[internal_composed_sw]")
        parts.append(f"[internal_composed_sw]scale={CONTENT_AREA_W}:{CONTENT_AREA_H}:flags=bicubic[content_scaled_sw]")
        parts.append(f"color=c=black:s={FINAL_W}x{FINAL_H}:r={target_fps}:d=3600,format=nv12[bg_canvas_sw]")
        parts.append(f"[bg_canvas_sw][content_scaled_sw]overlay=x=0:y={CONTENT_OFFSET_Y}:eof_action=pass[vpreout_sw]")
        last_v_pad = "[vpreout_sw]"
        if txt_input_label:
            parts.append(f"{last_v_pad}{txt_input_label}overlay=x=0:y=0:eof_action=repeat,setsar=1[vfinal_sw]")
            last_v_pad = "[vfinal_sw]"
        else:
            parts.append(f"{last_v_pad}setsar=1[vfinal_sw]")
            last_v_pad = "[vfinal_sw]"
        return ";".join(parts), last_v_pad

    def add_drawtext_filter(self, filter_cmd, textfile_path, font_size, line_spacing):
        safe_path = textfile_path.replace("\\", "/").replace(":", "\\:")
        font_arg = ""
        if os.name == 'nt':
            for fpath in ["C:/Windows/Fonts/arial.ttf", "C:/Windows/Fonts/segoeui.ttf"]:
                if os.path.exists(fpath):
                    safe_font = fpath.replace("\\", "/").replace(":", "\\:")
                    font_arg = f":fontfile='{safe_font}'"
                    break
        drawtext = f",drawtext=textfile='{safe_path}':fontcolor=white:fontsize={font_size}:x=(w-tw)/2:y=50:line_spacing={line_spacing}{font_arg}"
        return filter_cmd + drawtext

    def build_granular_speed_chain(self, video_path, duration_ms, speed_segments, base_speed, source_cut_start_ms=0, input_v_label="[v_stabilized]", input_a_label="[0:a]", target_fps="60"):
        segments = []
        for s in speed_segments:
            rel_start = s['start'] - source_cut_start_ms
            rel_end = s['end'] - source_cut_start_ms
            if rel_end <= 0 or rel_start >= duration_ms: continue
            segments.append({'start': max(0.0, float(rel_start)), 'end': min(float(duration_ms), float(rel_end)), 'speed': float(s['speed'])})
        segments.sort(key=lambda x: x['start'])
        chunks = []
        current_time = 0.0
        total_duration_sec = duration_ms / 1000.0
        for seg in segments:
            seg_start, seg_end, seg_speed = seg['start'] / 1000.0, seg['end'] / 1000.0, seg['speed']
            if seg_start > current_time + 0.001: chunks.append({'start': current_time, 'end': seg_start, 'speed': float(base_speed)})
            chunks.append({'start': seg_start, 'end': seg_end, 'speed': seg_speed})
            current_time = seg_end
        if current_time < total_duration_sec - 0.001: chunks.append({'start': current_time, 'end': total_duration_sec, 'speed': float(base_speed)})
        chunks = [ch for ch in chunks if (ch['end'] - ch['start']) > 0.001]
        n_chunks = len(chunks)
        if n_chunks == 0:
            return f"{input_v_label}null[v_speed_out];{input_a_label}anull[a_speed_out]", "[v_speed_out]", "[a_speed_out]", total_duration_sec, lambda x: x
        v_pads, a_pads, full_chain_parts = [], [], []
        full_chain_parts.append(f"{input_v_label}split={n_chunks}{''.join([f'[v_split_{i}]' for i in range(n_chunks)])}")
        full_chain_parts.append(f"{input_a_label}asplit={n_chunks}{''.join([f'[a_split_{i}]' for i in range(n_chunks)])}")
        final_duration = 0.0
        for i, chunk in enumerate(chunks):
            start, end, speed = chunk['start'], chunk['end'], chunk['speed']
            dur = end - start
            out_dur = dur / speed
            full_chain_parts.append(f"[v_split_{i}]trim=start={start:.4f}:end={end:.4f},setpts=(PTS-STARTPTS)/{speed:.4f}[v_chunk_{i}]")
            v_pads.append(f"[v_chunk_{i}]")
            audio_speed_filters = []
            tmp_s = speed
            while tmp_s < 0.5: audio_speed_filters.append("atempo=0.5"); tmp_s /= 0.5
            while tmp_s > 2.0: audio_speed_filters.append("atempo=2.0"); tmp_s /= 2.0
            audio_speed_filters.append(f"atempo={tmp_s:.4f}")
            a_chain = [f"[a_split_{i}]atrim=start={start:.4f}:end={end:.4f}", "asetpts=PTS-STARTPTS", ",".join(audio_speed_filters)]
            a_chain.append("aresample=48000:async=1:min_comp=0.001:min_hard_comp=0.1")
            full_chain_parts.append(f"{','.join(a_chain)}[a_chunk_{i}]")
            a_pads.append(f"[a_chunk_{i}]")
            final_duration += out_dur
        full_chain_parts.append(f"{''.join(v_pads)}concat=n={len(v_pads)}:v=1:a=0[v_speed_out]")
        full_chain_parts.append(f"{''.join(a_pads)}concat=n={len(a_pads)}:v=0:a=1,aresample=48000:async=1:min_comp=0.001[a_speed_out]")
        
        def time_mapper(t):
            return sum([(ch['end'] - ch['start']) / ch['speed'] for ch in chunks if t > ch['end']]) + (max(0, t - next(ch['start'] for ch in chunks if t <= ch['end'])) / next(ch['speed'] for ch in chunks if t <= ch['end'])) if any(ch['start'] <= t <= ch['end'] for ch in chunks) else 0.0
        return ";".join(full_chain_parts), "[v_speed_out]", "[a_speed_out]", final_duration, time_mapper

    def build_audio_chain(self, music_config, video_start_time, video_end_time, speed_factor, disable_fades, vfade_in_d, audio_filter_cmd, time_mapper=None, sample_rate=48000):
        chain = []
        target_sample_rate = sample_rate or 48000
        main_audio_filter_parts = [audio_filter_cmd if audio_filter_cmd else "anull"]
        if vfade_in_d > 0:
            main_audio_filter_parts.append(f"afade=t=in:st=0:d={vfade_in_d:.3f}")
        main_audio_filter = ",".join(main_audio_filter_parts)
        chain.append(f"[0:a]{main_audio_filter},aresample={target_sample_rate}:async=1:first_pts=0:min_comp=0.001[a_main_prepared]")
        if music_config and music_config.get("path"):
            mc = music_config
            t_mapper = time_mapper if time_mapper else (lambda t: (t - video_start_time) / speed_factor)
            orig_timeline_start = mc.get('timeline_start_sec', 0.0)
            orig_user_end = mc.get('timeline_end_sec', video_end_time)
            file_offset = mc.get('file_offset_sec', 0.0)
            if time_mapper:
                new_timeline_start = t_mapper(orig_timeline_start)
                new_timeline_end = t_mapper(orig_user_end)
                new_video_start = t_mapper(video_start_time)
                new_video_end = t_mapper(video_end_time)
                relative_start_new = new_timeline_start - new_video_start
                dur_a = max(0.0, new_timeline_end - new_timeline_start)
                delay_ms = max(0, int(relative_start_new * 1000))
                start_skip = max(0.0, new_video_start - new_timeline_start)
            else:
                relative_start = orig_timeline_start - video_start_time
                start_skip = (abs(relative_start) / speed_factor) if relative_start < 0 else 0.0
                delay_ms = int((relative_start / speed_factor) * 1000) if relative_start > 0 else 0
                dur_v = max(0.0, orig_user_end - orig_timeline_start)
                dur_a = dur_v / speed_factor
            final_start_pos = file_offset + start_skip
            music_filters = [
                f"atrim=start={final_start_pos:.3f}:duration={dur_a:.3f}",
                "asetpts=PTS-STARTPTS",
                f"aresample={target_sample_rate}"
            ]
            if not disable_fades:
                MIN_CLIP_FOR_FADE = 0.3
                if dur_a > 0.1:
                    if dur_a > MIN_CLIP_FOR_FADE:
                        FADE_DUR = min(1.0, dur_a / 3.0)
                        music_filters.append(f"afade=t=in:st=0:d={FADE_DUR:.3f}")
                        music_filters.append(f"afade=t=out:st={max(0.0, dur_a - FADE_DUR):.3f}:d={FADE_DUR:.3f}")
            vol = max(0.0, min(1.0, float(mc.get('volume', 1.0))))
            music_filters.append(f"volume={vol:.4f}")
            chain.append(f"[1:a]{','.join(music_filters)}[a_music_prepared]")
            if delay_ms > 0:
                chain.append(f"[a_music_prepared]adelay={delay_ms}|{delay_ms}[a_music_delayed]")
                mus_input = "[a_music_delayed]"
            else:
                mus_input = "[a_music_prepared]"
            v_vol = float(mc.get('main_vol', 1.0))
            chain.append(f"[a_main_prepared]volume={v_vol:.4f},highpass=f=150[game_scaled]")
            chain.append(f"[game_scaled]asplit=2[game_mix][game_sc]")
            chain.append(f"{mus_input}[game_sc]sidechaincompress=threshold=0.08:ratio=3:attack=5:release=50[music_ducked]")
            chain.append(f"[game_mix][music_ducked]amix=inputs=2:duration=first:dropout_transition=0:normalize=0,alimiter=limit=0.98[acore]")
        else:
            chain.append("[a_main_prepared]anull[acore]")
        return chain
