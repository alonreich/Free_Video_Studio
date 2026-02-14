from __future__ import annotations
from pathlib import Path
import types
from sanity_tests._real_sanity_harness import install_qt_vlc_stubs
install_qt_vlc_stubs()

from processing.filter_builder import FilterBuilder
from processing.worker import ProcessThread

class _Sig:
    def __init__(self) -> None:
        self.calls: list[tuple] = []

    def emit(self, *args) -> None:
        self.calls.append(args)

def _logger() -> object:
    return types.SimpleNamespace(
        info=lambda *a, **k: None,
        warning=lambda *a, **k: None,
        error=lambda *a, **k: None,
        exception=lambda *a, **k: None,
        critical=lambda *a, **k: None,
    )

def test_trim_start_ss_is_correct_with_speed_and_granular_segments(monkeypatch, tmp_path: Path) -> None:
    captured_cmds: list[list[str]] = []

    class _Proc:
        pid = 111
        returncode = 0

        def wait(self, timeout=None):
            return 0

    def _fake_create_subprocess(cmd, _logger):
        captured_cmds.append(list(cmd))
        return _Proc()
    monkeypatch.setattr("processing.worker.create_subprocess", _fake_create_subprocess)
    monkeypatch.setattr("processing.worker.monitor_ffmpeg_progress", lambda *a, **k: None)
    monkeypatch.setattr("processing.worker.check_disk_space", lambda *a, **k: True)
    monkeypatch.setattr("processing.worker.calculate_video_bitrate", lambda *a, **k: 1500)
    monkeypatch.setattr("processing.worker.MediaProber.get_audio_bitrate", lambda self: 128)
    monkeypatch.setattr("processing.worker.MediaProber.get_sample_rate", lambda self: 48000)
    out_file = tmp_path / "rendered.mp4"
    out_file.write_bytes(b"ok")
    monkeypatch.setattr("processing.worker.ConcatProcessor.run_concat", lambda *a, **k: str(out_file))
    thr = ProcessThread(
        input_path=str(out_file),
        start_time_ms=12000,
        end_time_ms=22000,
        original_resolution="1920x1080",
        is_mobile_format=False,
        speed_factor=2.7,
        script_dir=str(tmp_path),
        progress_update_signal=_Sig(),
        status_update_signal=_Sig(),
        finished_signal=_Sig(),
        logger=_logger(),
        disable_fades=True,
        intro_still_sec=0.0,
        speed_segments=[
            {"start": 12000, "end": 15000, "speed": 0.5},
            {"start": 15000, "end": 18000, "speed": 2.4},
            {"start": 18000, "end": 22000, "speed": 1.3},
        ],
    )
    thr.run()
    assert captured_cmds, "ffmpeg command should be invoked"
    core_cmd = captured_cmds[0]
    ss_idx = core_cmd.index("-ss")
    assert core_cmd[ss_idx + 1] == "12.000"

def test_trim_relative_time_mapper_with_multiple_speed_segments() -> None:
    fb = FilterBuilder(logger=_logger())
    _chain, _v, _a, _dur, tmap = fb.build_granular_speed_chain(
        video_path="dummy.mp4",
        duration_ms=7000,
        speed_segments=[
            {"start": 12000, "end": 14000, "speed": 0.5},
            {"start": 14000, "end": 16000, "speed": 2.0},
            {"start": 16000, "end": 19000, "speed": 1.0},
        ],
        base_speed=1.5,
        source_cut_start_ms=12000,
    )
    assert abs(tmap(0.0) - 0.0) < 1e-6
    assert abs(tmap(2.0) - 4.0) < 0.05
    assert abs(tmap(4.0) - 5.0) < 0.05
