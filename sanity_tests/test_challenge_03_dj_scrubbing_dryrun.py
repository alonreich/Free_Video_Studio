from sanity_tests._ai_sanity_helpers import assert_all_present, read_source

def test_challenge_03_dj_scrubbing_dryrun() -> None:
    src = read_source("ui/parts/player_mixin.py")
    assert_all_present(
        src,
        [
            "if abs(m_pos - target_m_sec) > 0.15:",
            "music_player.seek(target_m_sec, reference='absolute', precision='exact')",
        ],
    )
