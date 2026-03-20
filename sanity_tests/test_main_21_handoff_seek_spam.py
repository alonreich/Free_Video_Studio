import os, sys, pytest, types, threading, time, random
from PyQt5.QtCore import QObject
sys.dont_write_bytecode = True

from ui.parts.player_mixin import PlayerMixin

class SegfaultSimulation(Exception):
    pass

class RealityMockPlayer:
    """
    [REALITY CHECK] Simulates libmpv's background C-threading.
    If two commands are called simultaneously, it 'Segfaults' (raises Exception).
    """

    def __init__(self):
        self.commands = []
        self.wid = None
        self._busy_lock = threading.Lock()
        self._is_shutdown = False

    def command(self, *args):
        if not self._busy_lock.acquire(blocking=False):
            raise SegfaultSimulation("Access Violation: Multiple threads calling libmpv concurrently!")
        try:
            if self._is_shutdown:
                raise SegfaultSimulation("Access Violation: calling libmpv after destroy")
            time.sleep(0.01) 
            self.commands.append(args)
        finally:
            self._busy_lock.release()

    def set_property(self, prop, val):
        if prop == "wid": self.wid = val
        self.command("set_property", prop, val)

class MockHost(QObject):
    def __init__(self):
        super().__init__()
        self.player = RealityMockPlayer()
        self.video_surface = types.SimpleNamespace(winId=lambda: 12345)
        self.positionSlider = types.SimpleNamespace(isSliderDown=lambda: True)
        self._mpv_lock = threading.RLock()
        self._scrub_lock = threading.RLock()
        self._pending_seek_ms = None
        self._is_seeking_active = False
        self.logger = types.SimpleNamespace(error=lambda m: print(f"ERROR: {m}"), info=lambda m: None)
        self._wizard_tracks = []
        self.speed_spinbox = types.SimpleNamespace(value=lambda: 1.1)

    def _safe_mpv_get(self, prop, default=None):
        return 10.0
    
    def _safe_mpv_command(self, *a, target_player=None):
        p = target_player if target_player else self.player
        try:
            p.command(*a)
            return True
        except SegfaultSimulation as e:
            pytest.fail(str(e))
        return False

    def _calculate_wall_clock_time(self, *a): return a[0]
    
    def _execute_throttled_seek(self):
        PlayerMixin._execute_throttled_seek(self)

def test_reality_seek_collision():
    """
    [CRITICAL] This test will FAIL if the Python logic allows 
    concurrent calls to the player.command() method.
    """
    host = MockHost()
    errors = []

    def spam_task():
        try:
            for i in range(10):
                PlayerMixin.set_player_position(host, i * 100)
                host._execute_throttled_seek()
                time.sleep(0.001)
        except Exception as e:
            errors.append(e)
    threads = [threading.Thread(target=spam_task) for _ in range(5)]
    for t in threads: t.start()
    for t in threads: t.join()
    if errors:
        pytest.fail(f"Caught {len(errors)} collisions! First: {errors[0]}")
    assert host._is_seeking_active is False
    assert len(host.player.commands) > 0
