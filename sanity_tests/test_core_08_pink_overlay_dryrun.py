from sanity_tests._ai_sanity_helpers import assert_all_present, read_source

def test_core_08_pink_overlay_dryrun() -> None:
    src = read_source("ui/parts/music_mixin.py")
    assert_all_present(
        src,
        [
            "self.positionSlider.set_music_visible(True)",
            "self.positionSlider.set_music_times(t_start, t_end)",
            "self.music_timeline_start_ms = t_start",
            "self.music_timeline_end_ms = t_end",
        ],
    )
