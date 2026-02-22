import os
import tempfile
from typing import Tuple
from .text_ops import safe_text
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

    def _make_even(self, n):
        """Ensure dimensions are even numbers for NVENC/CUDA compatibility."""
        i = int(round(n))
        return i if i % 2 == 0 else i + 1

    def _drawtext(self, text, x, y, size, color, font_path, alpha=1.0):
        safe_t = safe_text(text)
        alpha_hex = hex(int(alpha * 255))[2:].zfill(2)
        return (
            f"drawtext=fontfile='{font_path}':text='{safe_t}':"
            f"text_shaping=0:"
            f"fontsize={size}:fontcolor={color}{alpha_hex}:"
            f"x={x}:y={y}:shadowcolor=black@0.6:shadowx=2:shadowy=2"
        )

    def build_nvidia_resize(self, target_w, target_h, keep_highest_res=False):
        w = self._make_even(target_w)
        if keep_highest_res:
            return "scale_cuda=format=nv12"
        return f"scale_cuda={w}:-2:format=nv12"

    def build_mobile_filter(self, mobile_coords, original_res_str, is_boss_hp, show_teammates, use_nvidia=False, needs_text_overlay=False):
        """
        Builds the complex filter graph for mobile layout.
        Optimized to keep pipeline on GPU even with text overlay.
        """
        coords_data = mobile_coords

        def get_rect(section, key):
            return tuple(coords_data.get(section, {}).get(key, [0,0,0,0]))
        scales = coords_data.get("scales", {})
        overlays = coords_data.get("overlays", {})
        z_orders = coords_data.get("z_orders", {})
        hp_key = "boss_hp" if is_boss_hp else "normal_hp"
        active_layers = []
        
        def is_valid_config(rect, scale):
            return rect and len(rect) >= 4 and rect[0] >= 1 and rect[1] >= 1 and float(scale) > 0.001

        def register_layer(name, conf_key, crop_key_1080, ov_key):
            rect_1080 = get_rect("crops_1080p", crop_key_1080)
            sc = float(scales.get(conf_key, 1.0))
            if is_valid_config(rect_1080, sc):
                w_ui, h_ui, x_ui, y_ui = rect_1080
                if any(k in name.lower() for k in ["stats", "healthbar", "team"]):
                    x_ui -= 1; w_ui += 1
                elif "loot" in name.lower():
                    w_ui += 1
                transformed = inverse_transform_from_content_area_int((x_ui, y_ui, w_ui, h_ui), original_res_str)
                crop_orig = (transformed[2], transformed[3], transformed[0], transformed[1])
                ov = overlays.get(ov_key, {"x": 0, "y": 0})
                z = z_orders.get(ov_key, 50)
                active_layers.append({
                    "name": name,
                    "crop_orig": crop_orig,
                    "ui_wh": (rect_1080[0], rect_1080[1]),
                    "scale": sc,
                    "pos": (ov["x"], ov["y"]),
                    "z": z
                })
        register_layer("healthbar", hp_key, hp_key, hp_key)
        register_layer("lootbar", "loot", "loot", "loot")
        register_layer("stats", "stats", "stats", "stats")
        register_layer("spectating", "spectating", "spectating", "spectating")
        if show_teammates:
            register_layer("team", "team", "team", "team")
        active_layers.sort(key=lambda x: x["z"])
        if use_nvidia:
            split_count = 1 + len(active_layers)
            cmd = f"split={split_count}[bg_in]" + "".join([f"[{l['name']}_in]" for l in active_layers]) + ";"
            cmd += "[bg_in]scale=1280:1920:force_original_aspect_ratio=increase:flags=bilinear,crop=1280:1920,hwupload_cuda[bg_pre];"
            hud_gpu_names = []
            for layer in active_layers:
                cw, ch, cx, cy = layer['crop_orig']
                render_w = max(2, self._make_even(layer['ui_wh'][0] * layer['scale'] * BACKEND_SCALE))
                render_h = max(2, self._make_even(layer['ui_wh'][1] * layer['scale'] * BACKEND_SCALE))
                f_str = (
                    f"[{layer['name']}_in]"
                    f"crop={cw}:{ch}:{cx}:{cy},"
                    f"scale={render_w}:{render_h}:flags=bilinear,"
                    f"format=nv12,hwupload_cuda"
                    f"[{layer['name']}_gpu]"
                )
                cmd += f_str + ";"
                hud_gpu_names.append(f"{layer['name']}_gpu")
            current_pad = "[bg_pre]"
            for i, layer in enumerate(active_layers):
                lx = self._make_even(float(layer['pos'][0]) * BACKEND_SCALE)
                ly = self._make_even(float(layer['pos'][1]) * BACKEND_SCALE)
                next_pad = f"[vov_{i}]" if i < len(active_layers) - 1 else "[vpreout_gpu]"
                cmd += f"{current_pad}[{hud_gpu_names[i]}]overlay_cuda=x={lx}:y={ly}{next_pad};"
                current_pad = next_pad
            if not active_layers:
                cmd = "scale=1280:1920:force_original_aspect_ratio=increase:flags=bilinear,crop=1280:1920,format=nv12,hwupload_cuda[vpreout_gpu];"
            cmd += "[vpreout_gpu]hwdownload,format=nv12,scale=1080:1620:flags=bilinear,pad=1080:1920:0:150:black,format=nv12"
            return cmd
        else:
            f_main_inner = "scale=1280:1920:force_original_aspect_ratio=increase:flags=bilinear,crop=1280:1920"
            split_count = 1 + len(active_layers)
            cmd = f"split={split_count}[main_base]" + "".join([f"[{l['name']}_in]" for l in active_layers]) + ";"
            cmd += f"[main_base]{f_main_inner}[main_cropped];"
            hud_filters = []
            for layer in active_layers:
                cw, ch, cx, cy = layer['crop_orig']
                render_w = max(1, scale_round(layer['ui_wh'][0] * layer['scale'] * BACKEND_SCALE))
                render_h = max(1, scale_round(layer['ui_wh'][1] * layer['scale'] * BACKEND_SCALE))
                f_str = f"[{layer['name']}_in]crop={cw}:{ch}:{cx}:{cy},scale={render_w}:{render_h}:flags=bilinear[{layer['name']}_out]"
                hud_filters.append(f_str)
            if hud_filters:
                cmd += ";".join(hud_filters) + ";"
            current_pad = "[main_cropped]"
            for i, layer in enumerate(active_layers):
                lx = scale_round(float(layer['pos'][0]) * BACKEND_SCALE)
                ly = scale_round(float(layer['pos'][1]) * BACKEND_SCALE)
                next_pad = f"[t{i}]" if i < len(active_layers) - 1 else "[vpreout]"
                cmd += f"{current_pad}[{layer['name']}_out]overlay=x={lx}:y={ly}{next_pad};"
                current_pad = next_pad
            if not active_layers:
                 cmd = f"split=1[main_base];[main_base]{f_main_inner}[vpreout];"
            cmd += "[vpreout]scale=1080:-2,pad=1080:1920:(ow-iw)/2:(oh-ih)/2:black,format=nv12"
            return cmd

    def add_drawtext_filter(self, video_filter_cmd, text_file_path, font_px, line_spacing):
        norm_path = os.path.normpath(text_file_path)
        ff_textfile = norm_path.replace("\\", "/").replace(":", "\\:").replace("'", "'\\\\''")
        font_path = "arial"
        candidates = []
        try:
            import matplotlib.font_manager
            found = matplotlib.font_manager.findfont("Arial")
            if found: candidates.append(found)
        except ImportError:
            pass
        if os.name == 'nt':
            win_fonts = os.path.join(os.environ.get('WINDIR', 'C:/Windows'), "Fonts")
            candidates.append(os.path.join(win_fonts, "arial.ttf"))
            candidates.append(os.path.join(win_fonts, "segoeui.ttf"))
        else:
            candidates.append("/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf")
            candidates.append("/System/Library/Fonts/Helvetica.ttc")
        candidates.append(os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "binaries", "fonts", "arial.ttf"))
        for c in candidates:
            if os.path.exists(c):
                font_path = c.replace("\\", "/").replace(":", "\\:")
                break
        drawtext_parts = [
            f"drawtext=fontfile='{font_path}'",
            f"textfile='{ff_textfile}':reload=0:text_shaping=0",
            f"fontcolor=white:fontsize={int(font_px)}",
            f"x=(w-text_w)/2:y=(150-text_h)/2:line_spacing={line_spacing}",
            f"shadowcolor=black:shadowx=3:shadowy=3"
        ]
        drawtext_str = ":".join(drawtext_parts)
        return video_filter_cmd + "," + drawtext_str

    def build_granular_speed_chain(self, video_path, duration_ms, speed_segments, base_speed, source_cut_start_ms=0):
        segments = []
        for s in speed_segments:
            rel_start = s['start'] - source_cut_start_ms
            rel_end = s['end'] - source_cut_start_ms
            if rel_end <= 0 or rel_start >= duration_ms:
                continue
            segments.append({
                'start': max(0.0, float(rel_start)),
                'end': min(float(duration_ms), float(rel_end)),
                'speed': float(s['speed'])
            })
        segments.sort(key=lambda x: x['start'])
        if segments:
            merged = []
            curr = segments[0]
            for i in range(1, len(segments)):
                nxt = segments[i]
                if abs(curr['end'] - nxt['start']) < 1.0 and abs(curr['speed'] - nxt['speed']) < 0.01:
                    curr['end'] = max(curr['end'], nxt['end'])
                else:
                    merged.append(curr)
                    curr = nxt
            merged.append(curr)
            segments = merged
        chunks = []
        current_time = 0.0
        total_duration_sec = duration_ms / 1000.0
        for seg in segments:
            seg_start = seg['start'] / 1000.0
            seg_end = seg['end'] / 1000.0
            seg_speed = seg['speed']
            if seg_start > current_time + 0.001:
                chunks.append({'start': current_time, 'end': seg_start, 'speed': float(base_speed)})
            chunks.append({'start': seg_start, 'end': seg_end, 'speed': seg_speed})
            current_time = seg_end
        if current_time < total_duration_sec - 0.001:
            chunks.append({'start': current_time, 'end': total_duration_sec, 'speed': float(base_speed)})

        def time_mapper(t_orig):
            t_new = 0.0
            for ch in chunks:
                if t_orig > ch['end']:
                    t_new += (ch['end'] - ch['start']) / ch['speed']
                elif t_orig > ch['start']:
                    t_new += (t_orig - ch['start']) / ch['speed']
                    return t_new
                else:
                    break
            return t_new
        chunks = [ch for ch in chunks if (ch['end'] - ch['start']) > 0.001]
        final_duration = 0.0
        n_chunks = len(chunks)
        if n_chunks == 0:
            return f"[0:v]null[v_speed_out];[0:a]anull[a_speed_out]", "[v_speed_out]", "[a_speed_out]", total_duration_sec, lambda x: x
        self.logger.info(f"FFMPEG_ENGINE: Building granular chain with {n_chunks} chunks.")
        v_expr_parts = []
        current_out_time = 0.0
        a_filter_parts = []
        a_pads = []
        if n_chunks > 1:
            a_filter_parts.append(f"[0:a]asplit={n_chunks}{''.join([f'[a_split_{i}]' for i in range(n_chunks)])}")
        for i, chunk in enumerate(chunks):
            start = chunk['start']
            end = chunk['end']
            speed = chunk['speed']
            dur = end - start
            chunk_out_dur = dur / speed
            term = f"if(between(T,{start:.5f},{end:.5f}),(T-{start:.5f})/{speed:.5f}+{current_out_time:.5f},0)"
            v_expr_parts.append(term)
            a_in = f"[a_split_{i}]" if n_chunks > 1 else "[0:a]"
            audio_speed_filters = []
            temp_speed = speed
            while temp_speed < 0.5:
                audio_speed_filters.append("atempo=0.5"); temp_speed /= 0.5
            while temp_speed > 2.0:
                audio_speed_filters.append("atempo=2.0"); temp_speed /= 2.0
            audio_speed_filters.append(f"atempo={temp_speed:.4f}")
            audio_speed_cmd = ",".join(audio_speed_filters)
            micro_fade = 0.005 
            a_filter_parts.append(
                f"{a_in}atrim=start={start:.4f}:end={end:.4f},asetpts=PTS-STARTPTS,"
                f"{audio_speed_cmd},"
                f"afade=t=in:st=0:d={micro_fade:.3f},"
                f"afade=t=out:st={chunk_out_dur - micro_fade:.3f}:d={micro_fade:.3f}"
                f"[a_chunk_{i}]"
            )
            a_pads.append(f"[a_chunk_{i}]")
            current_out_time += chunk_out_dur
            final_duration += chunk_out_dur
        v_full_expr = "+".join(v_expr_parts)
        v_filter_cmd = f"[0:v]setpts='({v_full_expr})/TB'[v_speed_out]"
        a_concat_in = "".join(a_pads)
        a_concat_cmd = f"{a_concat_in}concat=n={len(a_pads)}:v=0:a=1[a_speed_out]"
        full_chain = f"{v_filter_cmd};{';'.join(a_filter_parts)};{a_concat_cmd}"
        return full_chain, "[v_speed_out]", "[a_speed_out]", final_duration, time_mapper

    def build_audio_chain(self, music_config, video_start_time, video_end_time, speed_factor, disable_fades, vfade_in_d, audio_filter_cmd, time_mapper=None, sample_rate=48000):
        chain = []
        target_sample_rate = sample_rate or 48000
        main_audio_filter_parts = [audio_filter_cmd if audio_filter_cmd else "anull"]
        if vfade_in_d > 0:
            main_audio_filter_parts.append(f"afade=t=in:st=0:d={vfade_in_d:.3f}")
        main_audio_filter = ",".join(main_audio_filter_parts)
        chain.append(f"[0:a]{main_audio_filter},aresample={target_sample_rate},asetpts=PTS-STARTPTS[a_main_prepared]")
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
                start_skip = abs(relative_start) if relative_start < 0 else 0.0
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
            chain.append(f"[a_main_prepared]volume={v_vol:.4f}[game_scaled]")
            chain.append(f"[game_scaled]{mus_input}amix=inputs=2:duration=first:dropout_transition=0:normalize=0[acore]")
        else:
            chain.append("[a_main_prepared]anull[acore]")
        return chain
        