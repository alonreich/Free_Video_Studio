from __future__ import annotations
from pathlib import Path
import tempfile
import types
from sanity_tests._real_sanity_harness import install_qt_vlc_stubs
install_qt_vlc_stubs()

from ui.parts.ui_builder_mixin import UiBuilderMixin

class _DummyBtn:
    def __init__(self) -> None:
        self.text = ""

    def setText(self, text: str) -> None:
        self.text = text

class _DummySig:
    def __init__(self) -> None:
        self.calls: list[str] = []

    def emit(self, text: str) -> None:
        self.calls.append(str(text))

def _sync_threads(monkeypatch) -> None:
    class _InstantThread:
        def __init__(self, target=None, *args, **kwargs):
            self._target = target

        def start(self):
            if self._target:
                self._target()
    monkeypatch.setattr("threading.Thread", _InstantThread)

def test_thumbnail_pick_uses_absolute_slider_time_even_when_speed_changes(monkeypatch, tmp_path: Path) -> None:
    video_file = tmp_path / "video.mp4"
    video_file.write_bytes(b"ok")
    captured: list[list[str]] = []
    monkeypatch.setattr("subprocess.run", lambda cmd, **kwargs: captured.append(list(cmd)))
    _sync_threads(monkeypatch)
    host = types.SimpleNamespace()
    host.input_file_path = str(video_file)
    host.original_duration_ms = 10_000
    host.positionSlider = types.SimpleNamespace(value=lambda: 4321)
    host.vlc_player = types.SimpleNamespace(get_time=lambda: 9999)
    host.speed_spinbox = types.SimpleNamespace(value=lambda: 3.1)
    host.speed_segments = [
        {"start": 0, "end": 2000, "speed": 0.5},
        {"start": 2000, "end": 7000, "speed": 2.2},
    ]
    host.bin_dir = tempfile.gettempdir()
    host.thumb_pick_btn = _DummyBtn()
    host.status_update_signal = _DummySig()
    host.logger = types.SimpleNamespace(info=lambda *a, **k: None, exception=lambda *a, **k: None)
    UiBuilderMixin._pick_thumbnail_from_current_frame(host)
    assert abs(host.selected_intro_abs_time - 4.321) < 1e-3
    assert "SET: 00:04.32" in host.thumb_pick_btn.text
    assert captured, "Expected ffmpeg thumbnail extraction command"
    ss_idx = captured[0].index("-ss")
    assert captured[0][ss_idx + 1] == "4.321"

def test_thumbnail_pick_clamps_to_duration_when_slider_exceeds_length(monkeypatch, tmp_path: Path) -> None:
    video_file = tmp_path / "video2.mp4"
    video_file.write_bytes(b"ok")
    captured: list[list[str]] = []
    monkeypatch.setattr("subprocess.run", lambda cmd, **kwargs: captured.append(list(cmd)))
    _sync_threads(monkeypatch)
    host = types.SimpleNamespace()
    host.input_file_path = str(video_file)
    host.original_duration_ms = 10_000
    host.positionSlider = types.SimpleNamespace(value=lambda: 15_000)
    host.vlc_player = types.SimpleNamespace(get_time=lambda: 0)
    host.bin_dir = tempfile.gettempdir()
    host.thumb_pick_btn = _DummyBtn()
    host.status_update_signal = _DummySig()
    host.logger = types.SimpleNamespace(info=lambda *a, **k: None, exception=lambda *a, **k: None)
    UiBuilderMixin._pick_thumbnail_from_current_frame(host)
    assert abs(host.selected_intro_abs_time - 10.0) < 1e-6
    assert "SET: 00:10.00" in host.thumb_pick_btn.text
    ss_idx = captured[0].index("-ss")
    assert captured[0][ss_idx + 1] == "10.000"
