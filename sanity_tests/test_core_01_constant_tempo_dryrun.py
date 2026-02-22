from sanity_tests._ai_sanity_helpers import assert_all_present, read_source

def test_core_01_constant_tempo_dryrun() -> None:
    src = read_source("ui/parts/player_mixin.py")
    assert_all_present(
        src,
        [
            "music_player.set_rate(1.0)",
            "real_audio_ms = time_since_music_start_project_ms / speed",
            "real_audio_ms = (wall_now - wall_start) * 1000.0",
        ],
    )
