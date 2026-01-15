import os

class FilterBuilder:
    def __init__(self, config, logger):
        self.cfg = config
        self.logger = logger

    def build_mobile_filter(self, original_res_str, is_boss_hp, show_teammates):
        coords_data = self.cfg.get_mobile_coordinates(self.logger)
        loot_1080 = tuple(coords_data['crops_1080p']['loot'])
        stats_1080 = tuple(coords_data['crops_1080p']['stats'])
        team_1080 = tuple(coords_data['crops_1080p']['team'])
        if is_boss_hp:
            hp_1080 = tuple(coords_data['crops_1080p']['boss_hp'])
            healthbar_scale = float(coords_data['scales']['boss_hp'])
            hp_ov = coords_data['overlays']['boss_hp']
            self.logger.info("Using Boss HP coordinates.")
        else:
            hp_1080 = tuple(coords_data['crops_1080p']['normal_hp'])
            healthbar_scale = float(coords_data['scales']['normal_hp'])
            hp_ov = coords_data['overlays']['normal_hp']
            self.logger.info("Using Normal HP coordinates.")
        loot_scale = float(coords_data['scales']['loot'])
        stats_scale = float(coords_data['scales']['stats'])
        team_scale = float(coords_data['scales']['team'])
        in_w, in_h = map(int, original_res_str.split('x'))
        if in_h > in_w:
            scale_factor = in_w / 1080.0
        else:
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
        lx = coords_data['overlays']['loot']['x']
        ly = coords_data['overlays']['loot']['y']
        sx = coords_data['overlays']['stats']['x']
        sy = coords_data['overlays']['stats']['y']
        hpx = hp_ov['x']
        hpy = hp_ov['y']
        f_main = f"[main]scale=1280:1920:force_original_aspect_ratio=increase,crop=1280:1920[main_cropped];"
        f_loot = f"[lootbar]crop={loot_crop},drawbox=t=2:c=black,{loot_s_str},format=yuva444p[lootbar_scaled];"
        f_hp = f"[healthbar]crop={hp_crop},drawbox=t=2:c=black,{hp_s_str},format=yuva444p[healthbar_scaled];"
        f_stats = f"[stats]crop={stats_crop},drawbox=t=2:c=black,{stats_s_str},format=yuva444p[stats_scaled];"
        common_filters = f_main + f_loot + f_hp + f_stats
        ov_1 = f"[main_cropped][lootbar_scaled]overlay={lx}:{ly}[t1];"
        ov_2 = f"[t1][healthbar_scaled]overlay={hpx}:{hpy}[t2];"
        ov_3 = f"[t2][stats_scaled]overlay={sx}:{sy}"
        common_overlays = ov_1 + ov_2 + ov_3
        if show_teammates:
            tx = coords_data['overlays']['team']['x']
            ty = coords_data['overlays']['team']['y']
            f_team = f"[team]crop={team_crop},drawbox=t=2:c=black,{team_s_str},format=yuva444p[team_scaled];"
            video_filter_cmd = (
                f"split=5[main][lootbar][healthbar][stats][team];"
                f"{common_filters}"
                f"{f_team}"
                f"{common_overlays}[t3];"
                f"[t3][team_scaled]overlay={tx}:{ty}"
            )
        else:
            video_filter_cmd = f"split=4[main][lootbar][healthbar][stats];{common_filters}{common_overlays}"
        video_filter_cmd += ",scale=1080:-2,pad=1080:1920:(ow-iw)/2:(oh-ih)/2:black"
        return video_filter_cmd

    def add_drawtext_filter(self, video_filter_cmd, text_file_path, font_px):
        ff_textfile = text_file_path.replace("\\", "/").replace(":", "\\:").replace("'", "'\\\\''")
        font_check = os.path.join(self.cfg.bin_dir, 'arial.ttf')
        if not os.path.exists(font_check) and os.path.exists("C:/Windows/Fonts/arial.ttf"):
            font_path = "C:/Windows/Fonts/arial.ttf"
        else:
            font_path = font_check
        font_path = font_path.replace("\\", "/").replace(":", "\\:")
        drawtext_parts = [
            f"drawtext=fontfile='{font_path}'",
            f"textfile='{ff_textfile}':reload=0:text_shaping=1",
            f"fontcolor=white:fontsize={int(font_px)}",
            f"x=(w-text_w)/2:y=40:line_spacing={self.cfg.line_spacing}",
            f"shadowcolor=black:shadowx=3:shadowy=3"
        ]
        drawtext_str = ":" .join(drawtext_parts)
        return video_filter_cmd + "," + drawtext_str

    def build_audio_chain(self, have_bg, bg_volume, bg_offset, duration, disable_fades, audio_filter_cmd, vfade_in_d=0, vfade_out_d=0, vfade_out_st=0):
        chain = []
        audio_src_node = "[0:a]"
        if audio_filter_cmd:
            main_audio_filter = audio_filter_cmd
        else:
            main_audio_filter = "aformat=sample_fmts=fltp:sample_rates=48000:channel_layouts=stereo"
        if vfade_in_d > 0:
            main_audio_filter += f",afade=t=in:st=0:d={vfade_in_d:.3f}"
        chain.append(f"{audio_src_node}{main_audio_filter},aresample=48000,asetpts=PTS-STARTPTS[a_main_speed_corrected]")
        if not have_bg:
            chain.append("[a_main_speed_corrected]anull[acore]")
            return chain
        vol = bg_volume if bg_volume is not None else 0.35
        vol = max(0.0, min(1.0, float(vol)))
        mo = max(0.0, float(bg_offset or 0.0))
        a1_chain = (
            f"atrim=start={mo:.3f}:duration={duration:.3f},"
            f"asetpts=PTS-STARTPTS,volume={vol:.4f},aresample=48000"
        )
        if not disable_fades:
            FADE_DUR = self.cfg.fade_duration
            out_start = max(0.0, duration - FADE_DUR)
            a1_chain += f",afade=t=in:st=0:d={FADE_DUR},afade=t=out:st={out_start:.3f}:d={FADE_DUR}"
        chain.append(f"[1:a]{a1_chain}[a_music_prepared]")
        chain.append(f"[a_music_prepared]asplit=2[mus_base][mus_to_filter]")
        chain.append(f"[mus_base]lowpass=f=150[mus_low]")
        chain.append(f"[mus_to_filter]highpass=f=150[mus_high]")
        chain.append(f"[a_main_speed_corrected]asplit=2[game_out][game_trig]")
        kill_switch_start = max(0, duration - 3.5)
        chain.append(f"[game_trig]afade=t=out:st={kill_switch_start:.3f}:d=0.5,highpass=f=200,lowpass=f=3500,agate=threshold=0.05:attack=5:release=100[trig_cleaned]")
        chain.append(f"[trig_cleaned]equalizer=f=1000:t=q:w=2:g=10[trig_final]")
        duck_params = "threshold=0.2:ratio=4:attack=1:release=400:detection=rms"
        chain.append(f"[mus_high][trig_final]sidechaincompress={duck_params}[mus_high_ducked]")
        chain.append(f"[mus_low][mus_high_ducked]amix=inputs=2:weights=1 1:normalize=0[a_music_reconstructed]")
        chain.append(
            "[game_out][a_music_reconstructed]"
            "amix=inputs=2:duration=first:dropout_transition=3,"
            "alimiter=limit=0.95:attack=5:release=50:asc=1,aresample=48000[acore]"
        )
        return chain