from __future__ import annotations
from sanity_tests._ai_sanity_helpers import assert_all_present, read_source

def test_core_15_main_app_player_acceleration_contracts_dryrun() -> None:
    app_boot_src = read_source("app.py")
    main_preview_src = read_source("ui/main_window.py")
    granular_src = read_source("ui/widgets/granular_speed_editor.py")
    main_wizard_src = read_source("ui/widgets/music_wizard.py")
    assert_all_present(
        app_boot_src,
        [
            'os.environ["VIDEO_FORCE_CPU"] = "1"',
            'if check_encoder_capability(ffmpeg_path, "h264_nvenc"):',
            'if check_encoder_capability(ffmpeg_path, "h264_amf"):',
            'if check_encoder_capability(ffmpeg_path, "h264_qsv"):',
        ],
    )
    assert_all_present(
        main_preview_src,
        [
            "'--avcodec-hw=any'",
            "'--vout=direct3d11'",
            "self.vlc_instance = vlc.Instance(vlc_args)",
        ],
    )
    assert_all_present(
        granular_src,
        [
            "'--avcodec-hw=any'",
            "'--vout=direct3d11'",
            "self.vlc_instance = vlc.Instance(vlc_args)",
            "if vlc_instance:",
        ],
    )
    assert_all_present(
        main_wizard_src,
        [
            '"--avcodec-hw=any",',
            '"--vout=direct3d11",',
            "self._video_player = self.vlc_v.media_player_new() if self.vlc_v else None",
        ],
    )

def test_core_15_crop_and_merger_step3_acceleration_contracts_dryrun() -> None:
    crop_src = read_source("developer_tools/media_processor.py")
    merger_wizard_src = read_source("utilities/merger_music_wizard.py")
    assert_all_present(
        crop_src,
        [
            "'--avcodec-hw=any'",
            "'--vout=direct3d11'",
            "fallback_args = [",
            "'--vout=dummy'",
        ],
    )
    assert_all_present(
        merger_wizard_src,
        [
            '"--avcodec-hw=any",',
            '"--vout=direct3d11",',
            "self._video_player = self.vlc_v.media_player_new() if self.vlc_v else None",
        ],
    )
