import os
from .text_ops import safe_text

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

    def build_mobile_filter(self, mobile_coords, original_res_str, is_boss_hp, show_teammates):
        coords_data = mobile_coords
        
        def get_rect(section, key):
            return tuple(coords_data.get(section, {}).get(key, [0,0,0,0]))
        loot_1080 = get_rect('crops_1080p', 'loot')
        stats_1080 = get_rect('crops_1080p', 'stats')
        team_1080 = get_rect('crops_1080p', 'team')
        scales = coords_data.get('scales', {})
        overlays = coords_data.get('overlays', {})
        if is_boss_hp:
            hp_1080 = get_rect('crops_1080p', 'boss_hp')
            healthbar_scale = float(scales.get('boss_hp', 1.0))
            hp_ov = overlays.get('boss_hp', {'x': 0, 'y': 0})
            self.logger.info("Using Boss HP coordinates.")
        else:
            hp_1080 = get_rect('crops_1080p', 'normal_hp')
            healthbar_scale = float(scales.get('normal_hp', 1.0))
            hp_ov = overlays.get('normal_hp', {'x': 0, 'y': 0})
            self.logger.info("Using Normal HP coordinates.")
        loot_scale = float(scales.get('loot', 1.0))
        stats_scale = float(scales.get('stats', 1.0))
        team_scale = float(scales.get('team', 1.0))
        try:
            in_w, in_h = map(int, original_res_str.split('x'))
        except:
            in_w, in_h = 1920, 1080
        scale_factor = in_h / 1080.0
        self.logger.info(f"Mobile Crop: Scale factor: {scale_factor:.4f} (Input: {in_w}x{in_h})")

        def scale_box(box, s):
            return tuple((int(round(v * s)) // 2) * 2 for v in box)
        hp = scale_box(hp_1080, scale_factor)
        loot = scale_box(loot_1080, scale_factor)
        stats = scale_box(stats_1080, scale_factor)
        team = scale_box(team_1080, scale_factor)
        hp_crop = f"{hp[0]}:{hp[1]}:{hp[2]}:{hp[3]}"
        loot_crop = f"{loot[0]}:{loot[1]}:{loot[2]}:{loot[3]}"
        stats_crop = f"{stats[0]}:{stats[1]}:{stats[2]}:{stats[3]}"
        team_crop = f"{team[0]}:{team[1]}:{team[2]}:{team[3]}"
        loot_s_str = f"scale={int(round(loot_1080[0] * loot_scale))}:{int(round(loot_1080[1] * loot_scale))}"
        hp_s_str = f"scale={int(round(hp_1080[0] * healthbar_scale))}:{int(round(hp_1080[1] * healthbar_scale))}"
        stats_s_str = f"scale={int(round(stats_1080[0] * stats_scale))}:{int(round(stats_1080[1] * stats_scale))}"
        team_s_str = f"scale={int(round(team_1080[0] * team_scale))}:{int(round(team_1080[1] * team_scale))}"
        lx = overlays.get('loot', {}).get('x', 0)
        ly = overlays.get('loot', {}).get('y', 0)
        sx = overlays.get('stats', {}).get('x', 0)
        sy = overlays.get('stats', {}).get('y', 0)
        hpx = hp_ov.get('x', 0)
        hpy = hp_ov.get('y', 0)
        f_main = "[main]scale=1280:1920:force_original_aspect_ratio=increase,crop=1280:1920[main_cropped]"
        f_loot = f"[lootbar]crop={loot_crop},drawbox=t=2:c=black,{loot_s_str},format=yuva444p[lootbar_scaled]"
        f_hp = f"[healthbar]crop={hp_crop},drawbox=t=2:c=black,{hp_s_str},format=yuva444p[healthbar_scaled]"
        f_stats = f"[stats]crop={stats_crop},drawbox=t=2:c=black,{stats_s_str},format=yuva444p[stats_scaled]"
        common_filters = f"{f_main};{f_loot};{f_hp};{f_stats}"
        ov_1 = f"[main_cropped][lootbar_scaled]overlay={lx}:{ly}[t1]"
        ov_2 = f"[t1][healthbar_scaled]overlay={hpx}:{hpy}[t2]"
        if show_teammates:
            ov_3 = f"[t2][stats_scaled]overlay={sx}:{sy}[t3]"
            tx = overlays.get('team', {}).get('x', 0)
            ty = overlays.get('team', {}).get('y', 0)
            f_team = f"[team]crop={team_crop},drawbox=t=2:c=black,{team_s_str},format=yuva444p[team_scaled]"
            video_filter_cmd = (
                f"split=5[main][lootbar][healthbar][stats][team];"
                f"{common_filters};"
                f"{f_team};"
                f"{ov_1};{ov_2};{ov_3};"
                f"[t3][team_scaled]overlay={tx}:{ty}"
            )
        else:
            ov_3 = f"[t2][stats_scaled]overlay={sx}:{sy}"
            video_filter_cmd = (
                f"split=4[main][lootbar][healthbar][stats];"
                f"{common_filters};"
                f"{ov_1};{ov_2};{ov_3}"
            )
        video_filter_cmd += ",scale=1080:-2,pad=1080:1920:(ow-iw)/2:(oh-ih)/2:black"
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
            f"x=(w-text_w)/2:y=40:line_spacing={line_spacing}",
            f"shadowcolor=black:shadowx=3:shadowy=3"
        ]
        drawtext_str = ":".join(drawtext_parts)
        return video_filter_cmd + "," + drawtext_str

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