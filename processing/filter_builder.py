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

    def _make_even(self, n):
        """Ensure dimensions are even numbers for NVENC/CUDA compatibility."""
        i = int(round(n))
        return i if i % 2 == 0 else i + 1

    def build_mobile_filter(self, mobile_coords, original_res_str, is_boss_hp, show_teammates, use_nvidia=False, needs_text_overlay=False, use_hwaccel=False, needs_hw_download=True, target_fps=60, input_pad="[v_stabilized]"):
        """
        Builds the complex filter graph for mobile layout.
        REFACTORED: Prioritizes CUDA-residency and eliminates redundant CPU-GPU roundtrips.
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
            try:
                iw_orig, ih_orig = map(int, original_res_str.lower().split('x'))
            except:
                iw_orig, ih_orig = 1920, 1080
            target_w, target_h = 1280, 1920
            target_ar = target_w / target_h
            src_ar = iw_orig / ih_orig
            if src_ar > target_ar:
                scale_h = 1920
                scale_w = self._make_even(scale_h * src_ar)
            else:
                scale_w = 1280
                scale_h = self._make_even(scale_w / src_ar)
            offset_x = self._make_even((1280 - scale_w) / 2)
            offset_y = self._make_even((1920 - scale_h) / 2)
            sc_fmt = ":format=nv12" if getattr(self, "cuda_caps", {}).get("scale_format") else ""
            safe_fps = str(int(target_fps)) if float(target_fps).is_integer() else str(target_fps)
            split_count = 1 + len(active_layers)
            cmd = f"{input_pad}split=outputs={split_count}[main_base]" + "".join([f"[hud_in_{i}]" for i in range(len(active_layers))]) + ";"
            cmd += f"[main_base]hwupload_cuda,scale_cuda={scale_w}:{scale_h}[main_scaled];"
            cmd += f"color=c=black:s=1280x1920:r={safe_fps},format=nv12,hwupload_cuda[bg_canvas];"
            cmd += f"[bg_canvas][main_scaled]overlay_cuda=x={offset_x}:y={offset_y}[bg_ready];"
            current_pad = "[bg_ready]"
            for i, layer in enumerate(active_layers):
                cw, ch, cx, cy = layer['crop_orig']
                cw_e, ch_e = self._make_even(cw), self._make_even(ch)
                render_w = self._make_even(layer['ui_wh'][0] * layer['scale'] * BACKEND_SCALE)
                render_h = self._make_even(layer['ui_wh'][1] * layer['scale'] * BACKEND_SCALE)
                lx = self._make_even(float(layer['pos'][0]) * BACKEND_SCALE)
                ly = self._make_even(float(layer['pos'][1]) * BACKEND_SCALE)
                layer_pad = f"[hud_{i}]"
                cmd += f"[hud_in_{i}]crop={cw_e}:{ch_e}:{cx}:{cy},hwupload_cuda,scale_cuda={render_w}:{render_h}{layer_pad};"
                next_pad = f"[vov_{i}]" if i < len(active_layers) - 1 else "[vcontent_gpu]"
                cmd += f"{current_pad}{layer_pad}overlay_cuda=x={lx}:y={ly}{next_pad};"
                current_pad = next_pad
            if not active_layers:
                cmd += "[bg_ready]null[vcontent_gpu];"
            cmd += f"[vcontent_gpu]scale_cuda=1080:1620[vcontent_scaled];"
            cmd += f"color=c=black:s=1080x1920:r={safe_fps},format=nv12,hwupload_cuda[black_canvas];"
            cmd += f"[black_canvas][vcontent_scaled]overlay_cuda=x=0:y=150[vpreout_gpu]"
            if needs_hw_download:
                return cmd + f";[vpreout_gpu]hwdownload,format=nv12"
            else:
                return cmd + f";[vpreout_gpu]format=cuda"
        else:
            f_main_inner = f"scale=1280:1920:force_original_aspect_ratio=increase:flags=bilinear,crop=1280:1920"
            split_count = 1 + len(active_layers)
            cmd = f"{input_pad}split=outputs={split_count}[main_base]" + "".join([f"[{l['name']}_in]" for l in active_layers]) + ";"
            cmd += f"[main_base]{f_main_inner}[main_cropped];"
            current_pad = "[main_cropped]"
            for i, layer in enumerate(active_layers):
                cw, ch, cx, cy = layer['crop_orig']
                render_w = max(2, self._make_even(layer['ui_wh'][0] * layer['scale'] * BACKEND_SCALE))
                render_h = max(2, self._make_even(layer['ui_wh'][1] * layer['scale'] * BACKEND_SCALE))
                lx = self._make_even(float(layer['pos'][0]) * BACKEND_SCALE)
                ly = self._make_even(float(layer['pos'][1]) * BACKEND_SCALE)
                next_pad = f"[t{i}]" if i < len(active_layers) - 1 else "[vpreout]"
                cmd += f"[{layer['name']}_in]crop={cw}:{ch}:{cx}:{cy},scale={render_w}:{render_h}:flags=lanczos[{layer['name']}_out];"
                cmd += f"{current_pad}[{layer['name']}_out]overlay=x={lx}:y={ly}{next_pad};"
                current_pad = next_pad
            if not active_layers:
                 cmd = f"{input_pad}{f_main_inner}[vpreout];"
            cmd += f"[vpreout]scale=1080:-2,pad=1080:1920:(ow-iw)/2:(oh-ih)/2:black,format=nv12"
            return cmd

    def add_drawtext_filter(self, filter_cmd, textfile_path, font_size, line_spacing):
        """Appends a drawtext filter to the end of the filter chain."""
        safe_path = textfile_path.replace("\\", "/").replace(":", "\\:")
        font_arg = ""
        if os.name == 'nt':
            for fpath in ["C:/Windows/Fonts/arial.ttf", "C:/Windows/Fonts/segoeui.ttf"]:
                if os.path.exists(fpath):
                    safe_font = fpath.replace("\\", "/").replace(":", "\\:")
                    font_arg = f":fontfile='{safe_font}'"
                    break
        drawtext = f",drawtext=textfile='{safe_path}':fontcolor=white:fontsize={font_size}:x=(w-tw)/2:y=(h-th-50):line_spacing={line_spacing}{font_arg}"
        return filter_cmd + drawtext

    def build_granular_speed_chain(self, video_path, duration_ms, speed_segments, base_speed, source_cut_start_ms=0, input_v_label="[v_stabilized]", input_a_label="[0:a]"):
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
            full_chain_parts.append(f"[v_split_{i}]trim=start={start:.4f}:end={end:.4f},setpts=PTS-STARTPTS,setpts='(PTS)/{speed:.4f}'[v_chunk_{i}]")
            v_pads.append(f"[v_chunk_{i}]")
            audio_speed_filters = []
            tmp_s = speed
            while tmp_s < 0.5: audio_speed_filters.append("atempo=0.5"); tmp_s /= 0.5
            while tmp_s > 2.0: audio_speed_filters.append("atempo=2.0"); tmp_s /= 2.0
            audio_speed_filters.append(f"atempo={tmp_s:.4f}")
            fade_ms = 0.005
            a_chain = [f"[a_split_{i}]atrim=start={start:.4f}:end={end:.4f}", "asetpts=PTS-STARTPTS", ",".join(audio_speed_filters)]
            if out_dur > (fade_ms * 2.1):
                a_chain.append(f"afade=t=in:st=0:d={fade_ms:.3f}")
                a_chain.append(f"afade=t=out:st={max(0.0, out_dur - fade_ms):.3f}:d={fade_ms:.3f}")
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
            chain.append(f"[game_scaled]{mus_input}amix=inputs=2:duration=first:dropout_transition=0:normalize=0,volume=0.9[acore]")
        else:
            chain.append("[a_main_prepared]anull[acore]")
        return chain
