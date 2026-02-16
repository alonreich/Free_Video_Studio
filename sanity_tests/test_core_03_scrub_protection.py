from __future__ import annotations
import types
from sanity_tests._real_sanity_harness import (
    DummyCheckBox,
    DummyMediaPlayer,
    DummySpinBox,
    install_qt_vlc_stubs,
)
install_qt_vlc_stubs()

from ui.parts.player_mixin import PlayerMixin

def test_core_03_scrub_protection() -> None:
    host = types.SimpleNamespace()
    host.vlc_player = DummyMediaPlayer(playing=True, current_ms=0, rate=2.0)
    host.vlc_music_player = DummyMediaPlayer(playing=True, current_ms=0, rate=1.0)
    host.music_timeline_start_ms = 0
    host.music_timeline_end_ms = 10_000
    host.wants_to_play = True
    host.speed_spinbox = DummySpinBox(2.0)
    host.granular_checkbox = DummyCheckBox(False)
    host.speed_segments = []
    host._wizard_tracks = [("song.mp3", 0.0, 10.0)]
    host._get_music_offset_ms = lambda: 0
    host.logger = types.SimpleNamespace(error=lambda *a, **k: None)

    import time
    original_time = time.time
    ticks = iter([10.0, 10.02, 10.08])
    time.time = lambda: next(ticks)
    try:
        PlayerMixin.set_vlc_position(host, 2000, sync_only=True)
        first_calls = len(host.vlc_music_player.set_time_calls)
        PlayerMixin.set_vlc_position(host, 2600, sync_only=True)
        assert len(host.vlc_music_player.set_time_calls) == first_calls
        PlayerMixin.set_vlc_position(host, 3000, sync_only=True)
        assert len(host.vlc_music_player.set_time_calls) > first_calls
    finally:
        time.time = original_time


