from sanity_tests._ai_sanity_helpers import assert_all_present, read_source

def test_challenge_03_dj_scrubbing_dryrun() -> None:
    src = read_source("ui/parts/player_mixin.py")
    assert_all_present(
        src,
        [
            "if not force_pause and (now - self._last_scrub_ts < 0.05):",
            "if abs(music_player.get_time() - music_target_in_file_ms) > 50:",
            "music_player.set_time(music_target_in_file_ms)",
        ],
    )
