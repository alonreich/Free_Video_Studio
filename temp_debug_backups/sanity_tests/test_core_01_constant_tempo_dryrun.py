from sanity_tests._ai_sanity_helpers import assert_all_present, read_source

def test_core_01_constant_tempo_dryrun() -> None:
    src = read_source("ui/parts/player_mixin.py")
    assert_all_present(
        src,
        [
            "if abs(m_pos - target_m_sec) > 0.15:",
            "project_pos_sec = (wall_now - wall_start) / 1000.0",
        ],
    )
