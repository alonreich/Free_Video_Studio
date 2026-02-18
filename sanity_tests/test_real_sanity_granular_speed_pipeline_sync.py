from __future__ import annotations
import types
from sanity_tests._ai_sanity_helpers import read_source
from sanity_tests._real_sanity_harness import DummyCheckBox, DummySpinBox, install_qt_vlc_stubs
install_qt_vlc_stubs()

import vlc
from ui.parts.player_mixin import PlayerMixin
from ui.widgets.music_wizard_misc import MergerMusicWizardMiscMixin
from ui.widgets.music_wizard_timeline import MergerMusicWizardTimelineMixin

def _logger() -> object:
    return types.SimpleNamespace(
        info=lambda *a, **k: None,
        warning=lambda *a, **k: None,
        error=lambda *a, **k: None,
        exception=lambda *a, **k: None,
        critical=lambda *a, **k: None,
        debug=lambda *a, **k: None,
    )

def test_granular_speed_segments_reach_export_backend_contract() -> None:
    worker_src = read_source("processing/worker.py")
    builder_src = read_source("processing/filter_builder.py")
    assert "if self.speed_segments:" in worker_src
    assert "build_granular_speed_chain(" in worker_src
    assert "granular_filter_str = g_str" in worker_src
    assert "granular_video_label = g_v" in worker_src
    assert "time_mapper = t_map" in worker_src
    assert "attempt_core_filters.append(granular_filter_str)" in worker_src
    assert "def build_granular_speed_chain(" in builder_src
    assert "concat=n=" in builder_src
    assert "time_mapper(" in builder_src

class _PreviewSlider:
    def __init__(self) -> None:
        self.values: list[int] = []

    def isSliderDown(self) -> bool:
        return False

    def blockSignals(self, _block: bool) -> None:
        return None

    def setValue(self, value: int) -> None:
        self.values.append(int(value))

class _PreviewPlayer:
    def __init__(self, initial_rate: float = 1.1) -> None:
        self._time_ms = 0
        self._rate = float(initial_rate)
        self.set_rate_calls: list[float] = []

    def is_playing(self) -> bool:
        return True

    def get_time(self) -> int:
        return int(self._time_ms)

    def get_rate(self) -> float:
        return float(self._rate)

    def set_rate(self, value: float) -> None:
        self._rate = float(value)
        self.set_rate_calls.append(float(value))

    def get_state(self) -> int:
        return vlc.State.Playing

def test_main_preview_player_reflects_granular_segment_speeds(monkeypatch) -> None:
    host = types.SimpleNamespace()
    host.vlc_player = _PreviewPlayer(initial_rate=1.1)
    host.vlc_music_player = None
    host.positionSlider = _PreviewSlider()
    host.granular_checkbox = DummyCheckBox(True)
    host.speed_spinbox = DummySpinBox(1.1)
    host.speed_segments = [
        {"start": 0, "end": 2000, "speed": 0.5},
        {"start": 2000, "end": 4000, "speed": 2.0},
        {"start": 4000, "end": 7000, "speed": 1.1},
    ]
    host.logger = _logger()
    host.is_playing = False
    fake_now = {"v": 1.0}
    monkeypatch.setattr("time.time", lambda: fake_now["v"])
    host.vlc_player._time_ms = 1000
    PlayerMixin.update_player_state(host)
    fake_now["v"] = 1.3
    host.vlc_player._time_ms = 2500
    PlayerMixin.update_player_state(host)
    fake_now["v"] = 1.6
    host.vlc_player._time_ms = 5000
    PlayerMixin.update_player_state(host)
    assert host.vlc_player.set_rate_calls[-3:] == [0.5, 2.0, 1.1]

class _WizardVideoPlayer:
    def get_full_state(self) -> dict[str, int]:
        return {"state": 3, "time": 0, "length": 10_000}

class _WizardMusicPlayer:
    def __init__(self) -> None:
        self.set_rate_calls: list[float] = []
        self.set_time_calls: list[int] = []

    def set_media(self, _media) -> None:
        return None

    def play(self) -> None:
        return None

    def set_rate(self, value: float) -> None:
        self.set_rate_calls.append(float(value))

    def set_time(self, value: int) -> None:
        self.set_time_calls.append(int(value))

    def audio_set_mute(self, _mute: bool) -> None:
        return None

    def audio_set_volume(self, _vol: int) -> None:
        return None

def test_wizard_step3_accounts_for_granular_segments_and_music_timeline() -> None:
    playback_src = read_source("ui/widgets/music_wizard_playback.py")
    music_mixin_src = read_source("ui/parts/music_mixin.py")
    assert "wall_now = self._calculate_wall_clock_time(v_time_ms, self.speed_segments, self.speed_factor)" in playback_src
    assert "self._sync_music_only_to_time(project_time)" in playback_src
    assert "speed_segments=speed_segments" in music_mixin_src
    host = types.SimpleNamespace()
    host.trim_start_ms = 1000
    host.speed_factor = 1.5
    host.speed_segments = [
        {"start": 1000, "end": 2500, "speed": 0.5},
        {"start": 2500, "end": 5000, "speed": 2.0},
        {"start": 5000, "end": 9000, "speed": 1.1},
    ]
    host._video_player = _WizardVideoPlayer()
    host._player = _WizardMusicPlayer()
    host.vlc_m = types.SimpleNamespace(media_new=lambda path: path)
    host.selected_tracks = [("song.mp3", 0.0, 30.0)]
    host._last_m_mrl = ""
    host.music_vol_slider = types.SimpleNamespace(value=lambda: 80)
    host._scaled_vol = lambda v: int(v)
    host.logger = _logger()
    host._calculate_wall_clock_time_raw = types.MethodType(
        MergerMusicWizardMiscMixin._calculate_wall_clock_time_raw, host
    )
    MergerMusicWizardMiscMixin._cache_wall_times(host)
    target_video_ms = 3500
    wall_now = MergerMusicWizardMiscMixin._calculate_wall_clock_time(
        host, target_video_ms, host.speed_segments, host.speed_factor
    )
    project_time = max(0.0, wall_now - host._wall_trim_start)
    MergerMusicWizardTimelineMixin._sync_music_only_to_time(host, project_time)
    assert host._player.set_rate_calls and host._player.set_rate_calls[-1] == 1.0
    assert host._player.set_time_calls, "Music player must seek according to segment-aware project time"
    assert abs(host._player.set_time_calls[-1] - int(project_time * 1000)) <= 1
