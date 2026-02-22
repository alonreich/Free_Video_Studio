from sanity_tests._ai_sanity_helpers import assert_all_present, read_source

def test_challenge_02_impossible_fade_dryrun() -> None:
    src = read_source("processing/filter_builder.py")
    assert_all_present(
        src,
        [
            "FADE_DUR = min(1.0, dur_a / 3.0)",
            "if dur_a > 0.1:",
            "max(0.0, dur_a - FADE_DUR)",
        ],
    )
