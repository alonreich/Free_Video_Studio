import os, sys, pytest, types, threading, time
sys.dont_write_bytecode = True

from PyQt5.QtCore import QObject
from utilities.merger_music_wizard_waveform import MergerMusicWizardWaveformMixin
from utilities.merger_music_wizard_timeline import MergerMusicWizardTimelineMixin

class MockPlayer:
    def __init__(self):
        self._time = 0
        self.commands = []

    def set_time(self, ms):
        self._time = ms
        self.commands.append(("set_time", ms))

    def seek(self, sec):
        self._time = int(sec * 1000)
        self.commands.append(("seek", sec))

class MockWizardHost(QObject):
    def __init__(self):
        super().__init__()
        self._draw_w = 1000
        self._draw_x0 = 0
        self.offset_slider = types.SimpleNamespace(value=0, setValue=lambda v: None, maximum=lambda: 10000)
        self.player = MockPlayer()
        self._last_good_mpv_ms = 0
        self._sync_caret = lambda override_ms=None: None
        self.logger = types.SimpleNamespace(debug=lambda m: None, error=lambda m: print(m))
        self._wave_dragging = True
        self._pending_step2_seek_ms = None
        self._is_seeking_active = False
        self._show_caret_step2 = True
        self.total_video_sec = 60.0
        self.timeline = types.SimpleNamespace(set_current_time=lambda v: None)
        self._last_seek_ts = 0.0
        self._is_scrubbing_timeline = False
        self.stack = types.SimpleNamespace(currentIndex=lambda: 2)

    def _request_step2_seek(self, ms, immediate=False):
        if immediate: self.player.set_time(ms)
        else: setattr(self, "_timer_triggered", True)

    def _sync_all_players_to_time(self, t):
        pass

    def _project_time_to_source_ms(self, s):
        return int(s * 1000)

    def _safe_mpv_set(self, *a):
        pass

    def _safe_mpv_get(self, *a):
        return None

    def _safe_mpv_seek(self, p, s, **k):
        p.seek(s)

    def _ensure_step3_seek_timer(self, ms=None):
        setattr(self, "_timer3_triggered", True)

    def _flush_pending_step3_seek(self):
        pass

def test_wizard_seek_spam_stability():
    """
    [REALITY CHECK] Simulates spamming seeks in the Music Wizard steps.
    """
    host = MockWizardHost()
    host._seek_timer = types.SimpleNamespace(start=lambda ms: setattr(host, "_timer_triggered", True), isActive=lambda: False)
    for x in range(0, 1000, 10):
        MergerMusicWizardWaveformMixin._set_time_from_wave_x(host, x)
    assert len(host.player.commands) == 0, "Seek spam should be coalesced by the timer."
    assert getattr(host, "_timer_triggered", False) is True
    host3 = MockWizardHost()
    host3._step3_seek_timer = types.SimpleNamespace(start=lambda ms: setattr(host3, "_timer3_triggered", True), isActive=lambda: False)
    for ratio in [i/100.0 for i in range(100)]:
        MergerMusicWizardTimelineMixin._on_timeline_seek(host3, ratio)
    assert len(host3.player.commands) == 0, "Timeline seek spam should be throttled by step3 timer."
    assert getattr(host3, "_timer3_triggered", True) is True
