import os
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
        if keep_highest_res:
            return "scale_cuda=format=nv12"
        if target_w == 1920:
             return "scale_cuda=1920:-2:interp_algo=lanczos:format=nv12"
        elif target_w == 1280:
             return "scale_cuda=1280:-2:interp_algo=lanczos:format=nv12"
        elif target_w == 960:
             return "scale_cuda=960:-2:interp_algo=lanczos:format=nv12"
        return f"scale_cuda={target_w}:-2:interp_algo=lanczos:format=nv12"

    def build_mobile_filter(self, mobile_coords, original_res_str, is_boss_hp, show_teammates, use_nvidia=False):
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
            cmd_chain = "[main_base]hwdownload,format=nv12[cpu_master];"
            total_splits = 1 + len(active_layers)
            split_pads = "".join([f"[raw_{l['name']}]" for l in active_layers])
            cmd_chain += f"[cpu_master]split={total_splits}[raw_bg]{split_pads};"
            bg_filter = (
                "scale=1280:1920:force_original_aspect_ratio=increase:flags=bilinear,"
                "crop=1280:1920,"
                "hwupload_cuda"
            )
            cmd_chain += f"[raw_bg]{bg_filter}[gpu_bg];"
            overlay_chain_gpu = ""
            current_base = "[gpu_bg]"
            for i, layer in enumerate(active_layers):
                name = layer['name']
                cw, ch, cx, cy = layer['crop_orig']
                render_w = max(1, scale_round(layer['ui_wh'][0] * layer['scale'] * BACKEND_SCALE))
                render_h = max(1, scale_round(layer['ui_wh'][1] * layer['scale'] * BACKEND_SCALE))
                layer_filter = (
                    f"crop={cw}:{ch}:{cx}:{cy},"
                    f"scale={render_w}:{render_h}:flags=bilinear,"
                    f"hwupload_cuda"
                )
                cmd_chain += f"[raw_{name}]{layer_filter}[gpu_{name}];"
                lx = scale_round(float(layer['pos'][0]) * BACKEND_SCALE)
                ly = scale_round((float(layer['pos'][1]) - float(PADDING_TOP)) * BACKEND_SCALE)
                next_pad = f"[comp_{i}]" if i < len(active_layers) - 1 else "[vpreout_gpu]"
                overlay_chain_gpu += f"{current_base}[gpu_{name}]overlay_cuda=x={lx}:y={ly}{next_pad};"
                current_base = next_pad
            if not active_layers:
                 return "hwdownload,format=nv12,scale=1280:1920:force_original_aspect_ratio=increase:flags=bilinear,crop=1280:1920,hwupload_cuda,scale_cuda=1080:-2,pad=1080:1920:(ow-iw)/2:(oh-ih)/2:black"
            cmd_chain += overlay_chain_gpu
            cmd_chain += f"[vpreout_gpu]hwdownload,format=nv12,scale=1080:-2,pad=1080:1920:(ow-iw)/2:(oh-ih)/2:black,hwupload_cuda"
            return cmd_chain
        else:
            f_main_inner = "scale=1280:1920:force_original_aspect_ratio=increase:flags=bilinear,crop=1280:1920"
            split_count = 1 + len(active_layers)
            split_pads = "[main_base]" + "".join([f"[{l['name']}_in]" for l in active_layers])
            cmd = f"split={split_count}{split_pads};[main_base]{f_main_inner}[main_cropped];"
            hud_filters = []
            for layer in active_layers:
                cw, ch, cx, cy = layer['crop_orig']
                render_w = max(1, scale_round(layer['ui_wh'][0] * layer['scale'] * BACKEND_SCALE))
                render_h = max(1, scale_round(layer['ui_wh'][1] * layer['scale'] * BACKEND_SCALE))
                f_str = f"[{layer['name']}_in]crop={cw}:{ch}:{cx}:{cy},scale={render_w}:{render_h}:flags=bilinear[{layer['name']}_out]"
                hud_filters.append(f_str)
            cmd += ";".join(hud_filters) + ";"
            current_pad = "[main_cropped]"
            for i, layer in enumerate(active_layers):
                lx = scale_round(float(layer['pos'][0]) * BACKEND_SCALE)
                ly = scale_round((float(layer['pos'][1]) - float(PADDING_TOP)) * BACKEND_SCALE)
                next_pad = f"[t{i}]" if i < len(active_layers) - 1 else "[vpreout]"
                cmd += f"{current_pad}[{layer['name']}_out]overlay=x={lx}:y={ly}{next_pad};"
                current_pad = next_pad
            if not active_layers:
                 cmd = f"{f_main_inner}[vpreout];"
            cmd += "[vpreout]scale=1080:-2,pad=1080:1920:(ow-iw)/2:(oh-ih)/2:black"
            return cmd

    def add_drawtext_filter(self, video_filter_cmd, text_file_path, font_px, line_spacing):
        norm_path = os.path.normpath(text_file_path)
        ff_textfile = norm_path.replace("\\", "/").replace(":", "\\:").replace("'", "'''")
        candidates = [
            os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "binaries", "fonts", "arial.ttf"),
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
            fade_dur = min(0.05, chunk_out_dur / 3)
            a_filter_parts.append(
                f"{a_in}atrim=start={start:.4f}:end={end:.4f},asetpts=PTS-STARTPTS,"
                f"{audio_speed_cmd},"
                f"afade=t=in:st=0:d={fade_dur:.4f},"
                f"afade=t=out:st={chunk_out_dur - fade_dur:.4f}:d={fade_dur:.4f}"
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

    def build_audio_chain(self, music_config, video_start_time, video_end_time, speed_factor, disable_fades, vfade_in_d, audio_filter_cmd, time_mapper=None, sample_rate=None):
        chain = []
        main_audio_filter_parts = [audio_filter_cmd if audio_filter_cmd else "anull"]
        if vfade_in_d > 0:
            main_audio_filter_parts.append(f"afade=t=in:st=0:d={vfade_in_d:.3f}")
        main_audio_filter = ",".join(main_audio_filter_parts)
        resample_filter = f",aresample={sample_rate}" if sample_rate else ""
        chain.append(f"[0:a]{main_audio_filter}{resample_filter},asetpts=PTS-STARTPTS[a_main_prepared]")
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
                "asetpts=PTS-STARTPTS"
            ]
            if not disable_fades:
                FADE_DUR = 1.0
                if dur_a > (FADE_DUR * 2):
                    music_filters.append(f"afade=t=in:st=0:d={FADE_DUR}")
                    music_filters.append(f"afade=t=out:st={max(0.0, dur_a - FADE_DUR):.3f}:d={FADE_DUR}")
            vol = max(0.0, min(1.0, float(mc.get('volume', 1.0))))
            music_filters.append(f"volume={vol:.4f}")
            music_filters.append(f"aresample={sample_rate}")
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
            duck_params = "threshold=0.15:ratio=2.5:attack=1:release=400:detection=rms"
            chain.append(f"[mus_high][trig_final]sidechaincompress={duck_params}[mus_high_ducked]")
            chain.append("[mus_low][mus_high_ducked]amix=inputs=2:weights=1 1:normalize=0[a_music_reconstructed]")
            chain.append(
                "[game_out][a_music_reconstructed]"
                "amix=inputs=2:duration=first:dropout_transition=3:weights=1 1:normalize=0,"
                "alimiter=limit=0.95:attack=5:release=50[acore_pre_limiter]"
            )
            chain.append(f"[acore_pre_limiter]aresample={sample_rate}[acore]")
        else:
            chain.append("[a_main_prepared]anull[acore]")
        return chain
