from sanity_tests._ai_sanity_helpers import assert_all_present, read_source

def test_core_03_scrub_protection_dryrun() -> None:
    src = read_source("ui/parts/player_mixin.py")
    assert_all_present(
        src,
        [
            "if not force_pause and (now - self._last_scrub_ts < 0.05):",
            "self._last_scrub_ts = now",
            "self.timer.start(50)",
        ],
    )
