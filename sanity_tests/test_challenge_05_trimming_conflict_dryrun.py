from sanity_tests._ai_sanity_helpers import assert_all_present, read_source

def test_challenge_05_trimming_conflict_dryrun() -> None:
    src = read_source("ui/main_window.py")
    assert_all_present(
        src,
        [
            "def _on_slider_trim_changed(self, start_ms, end_ms):",
            "new_m_start = max(start_ms, self.music_timeline_start_ms)",
            "new_m_end = min(end_ms, self.music_timeline_end_ms)",
            "self.positionSlider.set_music_times(new_m_start, new_m_end)",
        ],
    )
