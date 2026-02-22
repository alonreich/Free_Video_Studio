from sanity_tests._ai_sanity_helpers import assert_all_present, read_source

def test_challenge_09_constant_pitch_dryrun() -> None:
    src = read_source("ui/parts/player_mixin.py")
    assert_all_present(
        src,
        [
            "music_player.set_rate(1.0)",
            "speed = float(getattr(self, 'speed_spinbox', None).value() if hasattr(self, 'speed_spinbox') else 1.1)",
            "real_audio_ms = time_since_music_start_project_ms / speed",
        ],
    )
