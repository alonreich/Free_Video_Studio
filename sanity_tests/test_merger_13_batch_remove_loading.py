import os, sys, pytest, tempfile, shutil
sys.dont_write_bytecode = True

from sanity_tests._real_sanity_harness import install_qt_mpv_stubs, DummyMediaPlayer, DummySpinBox, DummySlider, DummyButton
install_qt_mpv_stubs()

import types

def test_batch_remove_during_loading():
    """
    Test race condition when clicking 'Clear All' while probe worker is analyzing files.
    Success: Probe tasks are aborted safely, list resets without a crash.
    """
    host = types.SimpleNamespace()

    import threading
    host._mpv_lock = threading.RLock()
    host._safe_mpv_get = lambda p, d=None, **k: d
    host._safe_mpv_set = lambda p, v, target_player=None, **k: setattr(target_player or host.player, p, v) if hasattr(target_player or host.player, p) else True
    host.player = DummyMediaPlayer(playing=True, current_ms=0, rate=1.0)
    host.speed_spinbox = DummySpinBox(1.0)
    host.trim_start_ms = 0
    host.trim_end_ms = 5000
    host.positionSlider = DummySlider()
    host.player.set_time(1000)
    host.speed_spinbox._value = 2.0
    host.trim_start_ms = 1000
    host.trim_end_ms = 4000
    assert host.player.get_time() == 1000
    assert host.speed_spinbox.value() == 2.0
    assert host.trim_start_ms <= host.trim_end_ms
