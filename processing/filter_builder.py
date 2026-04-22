from .processing_utils import make_multiple, make_even, fps_to_float, add_drawtext_filter
from .filter_mobile import MobileFilterMixin

class FilterResult(tuple):
    def __contains__(self, item):
        return any(item in str(x) for x in self)

class AudioFilterMixin:
    def build_audio_chain(self, music_config, video_start_time, video_end_time, speed_factor, disable_fades, vfade_in_d, audio_filter_cmd, time_mapper=None, sample_rate=48000, music_tracks=None, music_start_index=1, total_project_duration=None):
        chain = []
        target_sample_rate = sample_rate or 48000
        raw_parts = []
        if isinstance(audio_filter_cmd, list): raw_parts.extend(audio_filter_cmd)
        elif audio_filter_cmd: raw_parts.append(audio_filter_cmd)
        if vfade_in_d > 0: raw_parts.append(f"afade=t=in:st=0:d={vfade_in_d:.3f}")
        cleaned_parts = []

        def flatten(item):
            if isinstance(item, list):
                for sub in item: flatten(sub)
            elif item:
                if isinstance(item, str): cleaned_parts.append(item)
                else: cleaned_parts.append(str(item))
        flatten(raw_parts)
        if not cleaned_parts: cleaned_parts = ["anull"]
        main_audio_filter = ",".join(cleaned_parts)
        chain.append(f"[0:a]{main_audio_filter}[a_main_raw]")
        tracks = music_tracks if music_tracks else []
        if not tracks and music_config and music_config.get("path"):
            path = music_config.get("path")
            offset = music_config.get("file_offset_sec", 0.0)
            if total_project_duration is not None: dur = total_project_duration
            else: dur = (video_end_time - video_start_time) / speed_factor
            tracks = [(path, offset, dur)]
        if not tracks:
            chain.append(f"[a_main_raw]aresample={target_sample_rate}:async=1[a_main_prepared]")
            return chain, "[a_main_prepared]"
        initial_delay_sec = 0.0
        if music_config:
            m_start_proj = float(music_config.get('timeline_start_sec', 0.0))
            initial_delay_sec = max(0.0, m_start_proj)
        prepared_music_labels = []
        accum_project_sec = initial_delay_sec
        for i, (path, file_offset, dur_sec) in enumerate(tracks):
            input_label = f"[{music_start_index + i}:a]"
            out_label = f"[a_mus_{i}]"; pre_label = f"[a_mus_{i}_pre]"
            music_filters = [f"atrim=start={file_offset:.3f}:duration={dur_sec:.3f}"]
            if not disable_fades and dur_sec > 0.5:
                FADE_DUR = min(0.5, dur_sec / 4.0)
                music_filters.append(f"afade=t=in:st=0:d={FADE_DUR:.3f}")
                music_filters.append(f"afade=t=out:st={max(0.0, dur_sec - FADE_DUR):.3f}:d={FADE_DUR:.3f}")
            m_vol = float(music_config.get('music_vol', music_config.get('volume', 0.8))) if music_config else 0.8
            music_filters.append(f"volume={m_vol:.4f}")
            chain.append(f"{input_label}{','.join(music_filters)}{pre_label}")
            delay_ms = int(accum_project_sec * 1000)
            if delay_ms > 0: chain.append(f"{pre_label}adelay={delay_ms}|{delay_ms}{out_label}")
            else: chain.append(f"{pre_label}anull{out_label}")
            prepared_music_labels.append(out_label)
            accum_project_sec += dur_sec
        if len(prepared_music_labels) > 1:
            mix_inputs = "".join(prepared_music_labels)
            chain.append(f"{mix_inputs}amix=inputs={len(prepared_music_labels)}:duration=longest:dropout_transition=0[a_bg_music]")
            bg_music_label = "[a_bg_music]"
        else: bg_music_label = prepared_music_labels[0]
        v_vol = float(music_config.get('main_vol', music_config.get('video_volume', 0.8))) if music_config else 0.8
        chain.append(f"[a_main_raw]volume={v_vol:.4f}[game_scaled]")
        chain.append(f"[game_scaled]asplit=2[game_out_pre][game_trig]")
        chain.append("[game_trig]highpass=f=200,lowpass=f=3500,agate=threshold=0.05:attack=5:release=100[trig_cleaned]")
        chain.append("[trig_cleaned]equalizer=f=1000:t=q:w=2:g=10[trig_final]")
        chain.append(f"{bg_music_label}asplit=2[mus_base][mus_to_filter]")
        chain.append("[mus_base]lowpass=f=150[mus_low]")
        chain.append("[mus_to_filter]highpass=f=150[mus_high]")
        d_thresh = music_config.get('ducking_threshold', 0.15); d_ratio = music_config.get('ducking_ratio', 2.5)
        duck_params = f"threshold={d_thresh}:ratio={d_ratio}:attack=1:release=400:detection=rms"
        chain.append(f"[mus_high][trig_final]sidechaincompress={duck_params}[mus_high_ducked]")
        chain.append("[mus_low][mus_high_ducked]amix=inputs=2:weights=1 1:normalize=0[a_music_reconstructed]")
        chain.append(f"[game_out_pre][a_music_reconstructed]amix=inputs=2:duration=first:dropout_transition=3:weights=1 1:normalize=0,dynaudnorm=f=150:g=15,alimiter=limit=0.95:attack=5:release=50,aresample={target_sample_rate}:async=1[a_music_prepared]")
        return chain, "[a_music_prepared]"

class FilterBuilder(AudioFilterMixin, MobileFilterMixin):
    def __init__(self, logger=None):
        self.logger = logger

    def build_granular_speed_chain(self, input_path=None, total_duration_ms=0, segments=None, base_speed=1.0, source_cut_start_ms=0, input_v_label="[0:v]", input_a_label="[0:a]", target_fps="60", video_path=None, duration_ms=None, speed_segments=None):
        input_path = input_path or video_path; total_duration_ms = total_duration_ms or duration_ms or 0
        segments = segments or speed_segments or []; total_duration_sec = float(total_duration_ms) / 1000.0
        timeline_origin_sec = float(source_cut_start_ms or 0.0) / 1000.0

        def _to_clip_relative_sec(t_abs_sec):
            try: rel = float(t_abs_sec) - timeline_origin_sec
            except: rel = 0.0
            return max(0.0, min(rel, total_duration_sec))
        source_chunks = []; current_sec = 0.0
        speed_segs = sorted([s for s in segments if abs(float(s.get('speed', 1.1))) > 0.001], key=lambda x: float(x.get('start', x.get('start_ms', 0))))
        for seg in speed_segs:
            s_start = _to_clip_relative_sec(float(seg.get('start', seg.get('start_ms', 0))) / 1000.0)
            s_end = _to_clip_relative_sec(float(seg.get('end', seg.get('end_ms', 0))) / 1000.0)
            if s_start > current_sec + 0.001:
                source_chunks.append({'start': current_sec, 'end': s_start, 'speed': float(base_speed)})
            source_chunks.append({'start': s_start, 'end': s_end, 'speed': float(seg['speed'])})
            current_sec = s_end
        if current_sec < total_duration_sec - 0.001:
            source_chunks.append({'start': current_sec, 'end': total_duration_sec, 'speed': float(base_speed)})
        freezes = [s for s in segments if abs(float(s.get('speed', 1.1))) < 0.001]
        final_chunks = []
        for ch in source_chunks:
            ch_start, ch_end = ch['start'], ch['end']
            chunk_freezes = sorted([f for f in freezes if ch_start <= _to_clip_relative_sec(float(f.get('start', f.get('start_ms', 0))) / 1000.0) < ch_end], key=lambda x: float(x.get('start', x.get('start_ms', 0))))
            curr_ch_start = ch_start
            for f in chunk_freezes:
                f_start = _to_clip_relative_sec(float(f.get('start', f.get('start_ms', 0))) / 1000.0)
                f_dur = float(f.get('end', f.get('end_ms', 0)) - f.get('start', f.get('start_ms', 0))) / 1000.0
                if f_start > curr_ch_start + 0.001:
                    final_chunks.append({'start': curr_ch_start, 'end': f_start, 'speed': ch['speed']})
                final_chunks.append({'start': f_start, 'end': f_start + 0.001, 'speed': 0.0, 'freeze_dur': f_dur})
                curr_ch_start = f_start
            if curr_ch_start < ch_end - 0.001:
                final_chunks.append({'start': curr_ch_start, 'end': ch_end, 'speed': ch['speed']})
        chunks = [ch for ch in final_chunks if (abs(ch['speed']) < 0.001 or (ch['end'] - ch['start']) > 0.001)]

        def time_mapper(timeline_sec):
            target = _to_clip_relative_sec(timeline_sec); mapped = 0.0
            for ch in chunks:
                ch_start, ch_end, ch_speed = ch['start'], ch['end'], ch['speed']
                is_freeze = abs(ch_speed) < 0.001
                if is_freeze:
                    f_dur = ch.get('freeze_dur', ch_end - ch_start)
                    if target > ch_start: mapped += f_dur
                    continue
                if target <= ch_start: break
                if target >= ch_end: mapped += (ch_end - ch_start) / ch_speed
                else:
                    mapped += (target - ch_start) / ch_speed
                    break
            return max(0.0, mapped)
        n_chunks = len(chunks)
        if n_chunks == 0:
            v_chain = f"{input_v_label}setpts='(PTS-STARTPTS)/{base_speed:.4f}',fps={target_fps}:round=near[v_speed_out]"
            tmp_s = float(base_speed); audio_speed_filters = []
            while tmp_s < 0.5: audio_speed_filters.append("atempo=0.5"); tmp_s /= 0.5
            while tmp_s > 2.0: audio_speed_filters.append("atempo=2.0"); tmp_s /= 2.0
            audio_speed_filters.append(f"atempo={tmp_s:.4f}")
            a_chain = f"{input_a_label}asetpts=PTS-STARTPTS,{','.join(audio_speed_filters)},aresample=48000:async=1:min_comp=0.01[a_speed_out]"
            return f"{v_chain};{a_chain}", "[v_speed_out]", "[a_speed_out]", (total_duration_sec/base_speed), time_mapper
        full_chain_parts = []; v_a_pads, final_duration = [], 0.0
        for i, chunk in enumerate(chunks):
            start, end, speed = chunk['start'], chunk['end'], chunk['speed']; v_src = input_v_label; a_src = input_a_label; v_chunk_label = f"[v_chunk_{i}]"; a_chunk_label = f"[a_chunk_{i}]"
            if abs(speed) < 0.001:
                dur = chunk.get('freeze_dur', end - start)
                full_chain_parts.append(f"{v_src}trim=start={start:.4f}:end={start+0.001:.4f},setpts=PTS-STARTPTS,loop=loop=-1:size=1:start=0,trim=duration={dur:.4f},setpts=PTS-STARTPTS,fps={target_fps}{v_chunk_label}")
                full_chain_parts.append(f"anullsrc=r=48000:cl=stereo,atrim=duration={dur:.4f},asetpts=PTS-STARTPTS{a_chunk_label}")
                out_dur = dur
            else:
                out_dur = (end - start) / speed
                full_chain_parts.append(f"{v_src}trim=start={start:.4f}:end={end:.4f},setpts=PTS-STARTPTS,fps={target_fps}:round=near,setpts='(PTS-STARTPTS)/{speed:.4f}'{v_chunk_label}")
                tmp_s = speed; audio_speed_filters = []
                while tmp_s < 0.5: audio_speed_filters.append("atempo=0.5"); tmp_s /= 0.5
                while tmp_s > 2.0: audio_speed_filters.append("atempo=2.0"); tmp_s /= 2.0
                audio_speed_filters.append(f"atempo={tmp_s:.4f}")
                full_chain_parts.append(f"{a_src}atrim=start={start:.4f}:end={end:.4f},asetpts=PTS-STARTPTS,{','.join(audio_speed_filters)},asetpts=PTS-STARTPTS,aresample=48000:async=1:min_comp=0.001:max_comp=0.1{a_chunk_label}")
            v_a_pads.append(f"{v_chunk_label}{a_chunk_label}"); final_duration += out_dur
        full_chain_parts.append(f"{''.join(v_a_pads)}concat=n={n_chunks}:v=1:a=1[v_speed_out][a_speed_out]")
        return ";".join(full_chain_parts), "[v_speed_out]", "[a_speed_out]", final_duration, time_mapper
