import time
import os
import threading
import traceback
from PyQt5.QtWidgets import QDialog, QVBoxLayout, QHBoxLayout, QPushButton, QStackedWidget, QStyle
from PyQt5.QtCore import Qt, pyqtSignal, QTimer
from ui.widgets.music_wizard_style import MergerUIStyle
from ui.widgets.music_wizard_constants import PREVIEW_VISUAL_LEAD_MS
from ui.widgets.music_wizard_workers import VideoFilmstripWorker, MusicWaveformWorker
from ui.widgets.music_wizard_widgets import SearchableListWidget, MusicItemWidget
from ui.widgets.music_wizard_step_pages import MergerMusicWizardStepPagesMixin
from ui.widgets.music_wizard_page3 import MergerMusicWizardStep3PageMixin
from ui.widgets.music_wizard_navigation import MergerMusicWizardNavigationMixin
from ui.widgets.music_wizard_waveform import MergerMusicWizardWaveformMixin
from ui.widgets.music_wizard_playback import MergerMusicWizardPlaybackMixin
from ui.widgets.music_wizard_timeline import MergerMusicWizardTimelineMixin
from ui.widgets.music_wizard_misc import MergerMusicWizardMiscMixin
try:
    import vlc as _vlc_mod
except Exception:
    _vlc_mod = None

import json
import socket
import subprocess
import random
import sys
import mmap
import struct

class StatusMemoryReader:
    """[NEW] Reads status data directly from Shared Memory (mmap)."""

    def __init__(self, port):
        self.tag = f"FVS_VLC_STATUS_{port}"
        self.shm = None
        self._is_ready = False

    def connect(self):
        try:
            self.shm = mmap.mmap(-1, 64, tagname=self.tag, access=mmap.ACCESS_READ)
            self._is_ready = True
            return True
        except:
            return False

    def read(self):
        if not self._is_ready and not self.connect():
            return None
        try:
            self.shm.seek(0)
            data = self.shm.read(20)
            if len(data) < 20: return None
            st, tm, ln = struct.unpack("iqq", data)
            return {'state': st, 'time': tm, 'length': ln}
        except:
            return None

    def close(self):
        try: self.shm.close()
        except: pass

class VLCRemotePlayer:
    """Lightweight player handle that delegates to a mode-specific VLCProcessProxy."""

    def __init__(self, engine):
        self._engine = engine

    def play(self):
        return self._engine.play()

    def pause(self):
        return self._engine.pause()

    def stop(self):
        return self._engine.stop()

    def set_pause(self, p):
        return self._engine.set_pause(p)

    def audio_set_volume(self, vol):
        return self._engine.audio_set_volume(vol)

    def set_media(self, mrl):
        return self._engine.set_media(mrl)

    def get_media(self):
        return self._engine.get_media()

    def set_time(self, ms):
        return self._engine.set_time(ms)

    def set_rate(self, rate):
        return self._engine.set_rate(rate)

    def set_hwnd(self, wid):
        return self._engine.set_hwnd(wid)

    def audio_set_mute(self, m):
        return self._engine.audio_set_mute(m)

    def audio_get_track_description(self):
        return self._engine.audio_get_track_description()

    def audio_set_track(self, track_id):
        return self._engine.audio_set_track(track_id)

    def get_state(self):
        return self._engine.get_state()

    def get_time(self):
        return self._engine.get_time()

    def get_length(self):
        return self._engine.get_length()

    def get_full_state(self):
        return self._engine.get_full_state()

    def release(self):
        """Player handle is intentionally non-owning; engine lifecycle is managed by dialog."""
        return None

class VLCProcessProxy:
    """Drastic measure: Proxies VLC commands to a completely separate OS process via Sockets."""

    def __init__(self, mode, logger, bin_dir):
        self.mode = mode
        self.logger = logger
        self.port = random.randint(10000, 20000)
        self.proc = None
        self.worker_pid = None
        self.bin_dir = bin_dir
        self._last_vol = -1
        self._last_time_set = 0
        self._cached_state = 0
        self._last_path = ""
        self._released = False
        self.is_ready = False
        self.is_fallback = False
        self.init_error = None
        self.local_player = None
        self.local_instance = None
        self._persistent_socket = None
        self._response_buffer = ""
        self.status_reader = StatusMemoryReader(self.port)
        self._spawn_worker()

    def _spawn_worker(self):
        threading.Thread(target=self._do_spawn_and_wait, daemon=True).start()

    def _do_spawn_and_wait(self):
        try:
            base_dir = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
            worker_script = os.path.join(base_dir, "processing", "vlc_worker.py")
            if not os.path.exists(worker_script):
                self.logger.error(f"FATAL: VLC Worker script missing at {worker_script}")
                self._activate_fallback("Worker script missing")
                return
            cmd = [sys.executable, worker_script, str(self.port), self.mode]
            self.logger.info(f"SPAWNING ISOLATED VLC ({self.mode.upper()}): Port {self.port} | CWD: {base_dir}")
            self.proc = subprocess.Popen(
                cmd, 
                cwd=base_dir,
                creationflags=subprocess.CREATE_NO_WINDOW if sys.platform == "win32" else 0
            )
            if not self._wait_for_ready():
                self._activate_fallback("Timeout connecting to worker")
        except Exception as e:
            self.logger.error(f"SPAWN ERROR ({self.mode}): {e}")
            self._activate_fallback(str(e))

    def _activate_fallback(self, reason):
        """Attempt local in-process VLC fallback. If that fails, remain not ready."""
        self.init_error = reason
        self.logger.warning(f"{self.mode.upper()} ENGINE: Remote worker unavailable ({reason}).")
        try:
            if self.proc and self.proc.poll() is None:
                self.proc.terminate()
        except Exception:
            pass
        if _vlc_mod is None:
            self.is_fallback = False
            self.is_ready = False
            self.logger.error(f"{self.mode.upper()} ENGINE: No python-vlc available for local fallback.")
            return
        try:
            plugin_path = os.path.join(self.bin_dir, "plugins").replace('\\', '/')
            vlc_args = [
                "--verbose=0",
                "--no-osd",
                "--ignore-config",
                f"--plugin-path={plugin_path}",
            ]
            self.local_instance = _vlc_mod.Instance(vlc_args)
            self.local_player = self.local_instance.media_player_new()
            self.is_fallback = True
            self.is_ready = True
            self.logger.warning(f"{self.mode.upper()} ENGINE: Switched to local fallback VLC instance.")
        except Exception as e:
            self.local_instance = None
            self.local_player = None
            self.is_fallback = False
            self.is_ready = False
            self.init_error = f"{reason}; local fallback failed: {e}"
            self.logger.error(f"{self.mode.upper()} ENGINE: local fallback failed: {e}")

    def _wait_for_ready(self):
        for i in range(50):
            if self.proc and self.proc.poll() is not None:
                self.logger.error(f"{self.mode.upper()} WORKER exited early with code {self.proc.poll()}.")
                return False
            try:
                with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as s:
                    s.settimeout(0.2)
                    s.connect(('127.0.0.1', self.port))
                    s.sendall((json.dumps({'action': 'ping'}) + "\n").encode('utf-8'))
                    response_buffer = ""
                    start_recv = time.time()
                    while "\n" not in response_buffer and time.time() - start_recv < 0.5:
                        chunk = s.recv(1024).decode('utf-8')
                        if not chunk:
                            break
                        response_buffer += chunk
                    if "\n" in response_buffer:
                        line = response_buffer.split("\n")[0]
                        res = json.loads(line)
                        if res.get('status') == 'ok':
                            self.worker_pid = res.get('pid')
                            self.logger.info(f"ISOLATED VLC ({self.mode.upper()}) READY. Remote PID: {self.worker_pid}")
                            self.is_ready = True
                            return True
            except:
                pass
            time.sleep(0.1)
        return False

    def _get_socket(self):
        if self._persistent_socket:
            return self._persistent_socket
        try:
            s = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
            s.settimeout(0.2)
            s.connect(('127.0.0.1', self.port))
            self._persistent_socket = s
            return s
        except:
            return None

    def _send(self, payload, retries=1):
        if self._released:
            return {'status': 'error', 'message': 'engine_released'}
        if self.is_fallback:
            return self._handle_local(payload)
        if not self.is_ready and payload.get('action') != 'quit':
            return {'status': 'error', 'message': 'engine_not_ready'}
        for i in range(retries + 1):
            s = self._get_socket()
            if not s: continue
            try:
                s.setblocking(False)
                try:
                    while s.recv(8192): pass
                except: pass
                finally: s.setblocking(True)
                msg = (json.dumps(payload) + "\n").encode('utf-8')
                s.sendall(msg)
                start_recv = time.time()
                while "\n" not in self._response_buffer and time.time() - start_recv < 1.0:
                    try:
                        s.settimeout(0.2)
                        chunk = s.recv(4096).decode('utf-8')
                        if not chunk: break
                        self._response_buffer += chunk
                    except socket.timeout:
                        break
                if "\n" in self._response_buffer:
                    line, self._response_buffer = self._response_buffer.split("\n", 1)
                    try:
                        return json.loads(line)
                    except json.JSONDecodeError as je:
                        self.logger.error(f"IPC JSON ERROR: {je} | Line: {line}")
                        return {}
            except (socket.timeout, ConnectionResetError, BrokenPipeError, OSError):
                if self._persistent_socket:
                    try: self._persistent_socket.close()
                    except: pass
                    self._persistent_socket = None
                self._response_buffer = ""
                continue
            except Exception as e:
                self.logger.error(f"IPC GENERAL ERROR: {e}")
                continue
        return {}

    def _handle_local(self, payload):
        """Mocks the worker logic using local player."""
        if not self.local_player: return {}
        action = payload.get('action')
        try:
            if action == 'play': self.local_player.play()
            elif action == 'pause': self.local_player.pause()
            elif action == 'stop': self.local_player.stop()
            elif action == 'set_volume': self.local_player.audio_set_volume(payload.get('volume', 100))
            elif action == 'set_mute': self.local_player.audio_set_mute(bool(payload.get('mute', False)))
            elif action == 'set_time': self.local_player.set_time(int(payload.get('time', 0)))
            elif action == 'set_rate': self.local_player.set_rate(float(payload.get('rate', 1.0)))
            elif action == 'set_hwnd': self.local_player.set_hwnd(payload.get('hwnd', 0))
            elif action == 'load':
                path = payload.get('path')
                if path:
                    m = self.local_player.get_instance().media_new(path)
                    self.local_player.set_media(m)
            elif action == 'get_state':
                return {
                    'state': int(self.local_player.get_state()),
                    'time': self.local_player.get_time(),
                    'length': self.local_player.get_length()
                }
            elif action == 'get_tracks':
                return {'tracks': self.local_player.audio_get_track_description()}
            elif action == 'set_track':
                self.local_player.audio_set_track(payload.get('track_id', 1))
        except Exception as e:
            self.logger.debug(f"Local Fallback Error: {e}")
        return {'status': 'ok'}

    def play(self): self._send({'action': 'play'})

    def pause(self): self._send({'action': 'pause'})

    def stop(self): self._send({'action': 'stop'})

    def set_pause(self, p): self._send({'action': 'pause' if p else 'play'})
    
    def audio_set_volume(self, vol):
        if vol == self._last_vol: return
        self._last_vol = vol
        self._send({'action': 'set_volume', 'volume': vol})

    def set_media(self, mrl):
        path = mrl if isinstance(mrl, str) else ""
        self._last_path = path
        self._send({'action': 'load', 'path': path})

    def get_media(self):
        if self.is_fallback:
            return self.local_player.get_media()
        
        class MockMedia:
            def __init__(self, path): self.path = path

            def get_mrl(self): return self.path
        return MockMedia(self._last_path)

    def set_time(self, ms): 
        if abs(ms - self._last_time_set) < 100: return
        self._last_time_set = ms
        self._send({'action': 'set_time', 'time': ms})

    def set_rate(self, rate): self._send({'action': 'set_rate', 'rate': rate})

    def set_hwnd(self, wid): self._send({'action': 'set_hwnd', 'hwnd': wid})

    def audio_set_mute(self, m):
        self._send({'action': 'set_mute', 'mute': bool(m)})
    
    def audio_get_track_description(self):
        res = self._send({'action': 'get_tracks'})
        return res.get('tracks', [])

    def get_state(self):
        data = self.status_reader.read()
        if data: return data['state']
        res = self._send({'action': 'get_state'})
        return res.get('state', 0)

    def get_time(self):
        data = self.status_reader.read()
        if data: return data['time']
        res = self._send({'action': 'get_state'})
        return res.get('time', 0)

    def get_length(self):
        data = self.status_reader.read()
        if data: return data['length']
        res = self._send({'action': 'get_state'})
        return res.get('length', 0)

    def get_full_state(self):
        """Fetches state, time, and length in a single IPC call for performance."""
        data = self.status_reader.read()
        if data: return data
        res = self._send({'action': 'get_state'})
        return {
            'state': res.get('state', 0),
            'time': res.get('time', 0),
            'length': res.get('length', 0)
        }

    def audio_set_track(self, track_id):
        self._send({'action': 'set_track', 'track_id': track_id})

    def release(self):
        if self._released:
            return
        self._released = True
        try:
            if hasattr(self, "status_reader"):
                self.status_reader.close()
            if self._persistent_socket:
                try:
                    self._send({'action': 'quit'})
                except Exception:
                    pass
                finally:
                    try:
                        self._persistent_socket.close()
                    except Exception:
                        pass
                    self._persistent_socket = None
                    self._response_buffer = ""
            elif self.is_ready:
                try:
                    self._send({'action': 'quit'})
                except Exception:
                    pass
        except Exception:
            pass
        if self.proc:
            try:
                if self.proc.poll() is None:
                    self.proc.terminate()

                    import time
                    time.sleep(0.5)
                    if self.proc.poll() is None:
                        self.proc.kill()
                        self.proc.wait(timeout=2)
            except Exception:
                pass
        if self.local_player:
            try:
                self.local_player.stop()
                self.local_player.release()
            except Exception:
                pass
            self.local_player = None
        if self.local_instance:
            try:
                self.local_instance.release()
            except Exception:
                pass
            self.local_instance = None

    def media_player_new(self):
        """Return a non-owning player handle bound to this engine."""
        return VLCRemotePlayer(self)

    def media_new(self, path): return path 

class MergerMusicWizard(
    MergerMusicWizardStepPagesMixin,
    MergerMusicWizardStep3PageMixin,
    MergerMusicWizardNavigationMixin,
    MergerMusicWizardWaveformMixin,
    MergerMusicWizardPlaybackMixin,
    MergerMusicWizardTimelineMixin,
    MergerMusicWizardMiscMixin,
    QDialog,
):
    _ui_call = pyqtSignal(object)

    def __init__(self, parent, vlc_instance, bin_dir, mp3_dir, total_project_sec, speed_factor=1.1, trim_start_ms=0, trim_end_ms=0, speed_segments=None):
        super().__init__(parent)
        self.parent_window = parent
        self.bin_dir = bin_dir
        self.mp3_dir = mp3_dir
        self.total_video_sec = total_project_sec
        self.speed_factor = speed_factor
        self.trim_start_ms = trim_start_ms
        self.trim_end_ms = trim_end_ms
        self.speed_segments = speed_segments or []
        self.logger = parent.logger
        self._cache_wall_times()
        self.setWindowTitle("Background Music Selection Wizard")
        self.setModal(True)
        if os.name == 'nt':
            import ctypes
            try:
                ctypes.windll.ole32.CoInitializeEx(None, 0x0)
            except: pass
        log_dir = os.path.join(getattr(self.parent_window, "base_dir", "."), "logs")
        os.makedirs(log_dir, exist_ok=True)
        self._v_native_log = os.path.join(log_dir, "vlc_wizard_video.log")
        self._m_native_log = os.path.join(log_dir, "vlc_wizard_music.log")
        for p in [self._v_native_log, self._m_native_log]:
            if os.path.exists(p):
                try: os.remove(p)
                except: pass
        plugin_path = os.path.join(self.bin_dir, "plugins").replace('\\', '/')
        vlc_args_v = [
            "--verbose=2",
            "--no-osd",
            "--aout=waveout",
            "--ignore-config",
            f"--plugin-path={plugin_path}",
            "--user-agent=VLC_VIDEO_WORKER"
        ]
        vlc_args_m = [
            "--verbose=2",
            "--no-osd",
            "--aout=directsound",
            "--ignore-config",
            f"--plugin-path={plugin_path}",
            "--user-agent=VLC_MUSIC_WORKER"
        ]
        os.environ["VLC_PLUGIN_PATH"] = os.path.join(self.bin_dir, "plugins")
        self.vlc_v = None
        self.vlc_m = None
        self._player = None
        self._video_player = None
        self.setStyleSheet('''
            QDialog { background-color: #2c3e50; color: #ecf0f1; }
            QWidget { background-color: #2c3e50; color: #ecf0f1; font-family: "Helvetica Neue", Arial, sans-serif; }
            QLabel { background: transparent; }
            QLineEdit { background: #0b141d; border: 2px solid #1f3545; border-radius: 8px; padding: 8px 12px; color: #ecf0f1; }
            QListWidget { background-color: #142d37; border: 2px solid #1f3545; border-radius: 12px; outline: none; padding: 2px; color: white; }
            QListWidget::item:selected { background: #1a5276; border-radius: 4px; }
            QScrollBar:vertical { width: 22px; background: #142d37; border: 1px solid #1f3545; border-radius: 10px; margin: 2px; }
            QScrollBar::handle:vertical { min-height: 34px; border-radius: 9px; border: 1px solid #b8c0c8; background: qlineargradient(x1:0, y1:0, x2:1, y2:0, stop:0 #c9d0d6, stop:0.5 #e1e6eb, stop:1 #b6bec6); }
        ''')
        self.current_track_path = None
        self.current_track_dur = 0.0
        self.selected_tracks = []
        self._editing_track_index = -1
        self._pending_offset_ms = 0
        self._show_caret_step2 = False
        self._step2_media_ready = False
        self._geometry_restored = False
        self._startup_complete = False
        self._temp_png = None
        self._pm_src = None
        self._waveform_worker = None
        self._wave_target_path = ""
        self._draw_w = 0; self._draw_h = 0
        self._draw_x0 = 0; self._draw_y0 = 0
        self._dragging = False; self._wave_dragging = False
        self._last_tick_ts = 0.0; self._is_syncing = False 
        self._current_elapsed_offset = 0.0; self._last_seek_ts = 0.0 
        self._last_clock_ts = time.time()
        self._vlc_state_playing = 3; self._last_good_vlc_ms = 0
        self._last_v_mrl = ""; self._last_m_mrl = ""
        self.main_layout = QVBoxLayout(self)
        self.main_layout.setContentsMargins(20, 10, 20, 20)
        self.main_layout.setSpacing(15)
        self.stack = QStackedWidget()
        self.setup_step1_select()
        self.setup_step2_offset()
        self.setup_step3_timeline()
        self.main_layout.addWidget(self.stack)
        nav_layout = QHBoxLayout()
        self.btn_cancel_wizard = QPushButton("CANCEL")
        self.btn_cancel_wizard.setFixedWidth(140); self.btn_cancel_wizard.setFixedHeight(42)
        self.btn_cancel_wizard.setStyleSheet(MergerUIStyle.BUTTON_DANGER)
        self.btn_cancel_wizard.setCursor(Qt.PointingHandCursor)
        self.btn_cancel_wizard.clicked.connect(self._on_nav_cancel_clicked)
        self.btn_back = QPushButton("  BACK")
        self.btn_back.setFixedWidth(135); self.btn_back.setFixedHeight(42)
        self.btn_back.setStyleSheet(MergerUIStyle.BUTTON_STANDARD)
        self.btn_back.setCursor(Qt.PointingHandCursor)
        self.btn_back.clicked.connect(self._on_nav_back_clicked)
        self.btn_back.hide()
        self.btn_play_video = QPushButton("  PLAY")
        self.btn_play_video.setFixedWidth(150); self.btn_play_video.setStyleSheet(MergerUIStyle.BUTTON_STANDARD)
        self.btn_play_video.setCursor(Qt.PointingHandCursor)
        self.btn_play_video.setIcon(self.style().standardIcon(QStyle.SP_MediaPlay))
        self.btn_play_video.clicked.connect(self.toggle_video_preview)
        self.btn_play_video.hide()
        self.btn_nav_next = QPushButton("NEXT")
        self.btn_nav_next.setFixedWidth(135); self.btn_nav_next.setFixedHeight(42)
        self.btn_nav_next.setStyleSheet(MergerUIStyle.BUTTON_MERGE)
        self.btn_nav_next.setCursor(Qt.PointingHandCursor)
        self.btn_nav_next.clicked.connect(self._on_nav_next_clicked)
        nav_layout.addWidget(self.btn_cancel_wizard); nav_layout.addWidget(self.btn_back)
        nav_layout.addStretch(); nav_layout.addWidget(self.btn_play_video)
        nav_layout.addSpacing(80); nav_layout.addStretch(); nav_layout.addWidget(self.btn_nav_next)
        self.main_layout.addLayout(nav_layout)
        self.btn_nav_next.setEnabled(False)
        self._prev_next_text = "NEXT"
        self.btn_nav_next.setText("PREPARING...")
        QTimer.singleShot(100, self._initialize_audio_engines)
        self._apply_step_geometry(0)
        self._startup_complete = True
        self.stack.currentChanged.connect(self._on_page_changed)
        self._search_timer = QTimer(self); self._search_timer.setSingleShot(True)
        self._search_timer.timeout.connect(self._do_search)
        self.update_coverage_ui()
        if self.mp3_dir:
            QTimer.singleShot(150, lambda: self.load_tracks(self.mp3_dir))

    def _initialize_audio_engines(self):
        """Heavy lifting: start background VLC processes after window is shown."""
        self.vlc_v = VLCProcessProxy('video', self.logger, self.bin_dir)
        self.vlc_m = VLCProcessProxy('music', self.logger, self.bin_dir)
        self._player = self.vlc_m.media_player_new() if self.vlc_m else None
        self._video_player = self.vlc_v.media_player_new() if self.vlc_v else None
        if self._player:
            self._player.audio_set_mute(False)
        if self._video_player:
            self._video_player.audio_set_mute(False)
            self._bind_video_output()
        self._engine_timer = QTimer(self)
        self._engine_timer.setInterval(500)
        self._engine_timer.timeout.connect(self._check_engine_ready)
        self._engine_timer.start()
        self._readiness_count = 0
        self.btn_nav_next.setText("CONNECTING...")

    def _check_engine_ready(self):
        self._readiness_count += 1
        v_ready = bool(getattr(self.vlc_v, "is_ready", False))
        m_ready = bool(getattr(self.vlc_m, "is_ready", False))
        ready = v_ready and m_ready
        if ready:
            self._engine_timer.stop()
            self.btn_nav_next.setEnabled(True)
            self.btn_nav_next.setText(self._prev_next_text)
            v_shm = self.vlc_v.status_reader.connect()
            m_shm = self.vlc_m.status_reader.connect()
            self.logger.info(
                "WIZARD: Audio Engines Connected. PID[V]=%s PID[M]=%s | SHM[V]=%s SHM[M]=%s",
                getattr(self.vlc_v, "worker_pid", None),
                getattr(self.vlc_m, "worker_pid", None),
                v_shm, m_shm
            )
            return
        if self._readiness_count >= 20:
            self.logger.warning("WIZARD: Engine connection timed out. Attempting fallback/restart...")
            self.btn_nav_next.setText("RETRYING...")
            if self._readiness_count < 60:
                if not v_ready: self.vlc_v._spawn_worker()
                if not m_ready: self.vlc_m._spawn_worker()
                return
            self._engine_timer.stop()
            self.btn_nav_next.setEnabled(True)
            self.btn_nav_next.setText("TRY ANYWAY")
            v_err = getattr(self.vlc_v, "init_error", None)
            m_err = getattr(self.vlc_m, "init_error", None)
            self.logger.error(
                "WIZARD: Audio engines failed after retries. video_ready=%s music_ready=%s",
                v_ready, m_ready
            )

    def showEvent(self, event):
        super().showEvent(event)

    def reject(self):
        try:
            if hasattr(self, "_save_step_geometry"):
                self._save_step_geometry()
        except Exception:
            pass
        self.stop_previews()
        self._release_vlc()
        super().reject()

    def closeEvent(self, event):
        try:
            if hasattr(self, "_save_step_geometry"):
                self._save_step_geometry()
        except Exception:
            pass
        self.stop_previews()
        self._release_vlc()
        super().closeEvent(event)

    def _release_vlc(self):
        """Safely release VLC players and instances."""
        try:
            if hasattr(self, "_player") and self._player:
                self._player.stop()
            if hasattr(self, "_video_player") and self._video_player:
                self._video_player.stop()
        except Exception as ex:
            self.logger.debug(f"WIZARD: stop before release failed: {ex}")
        finally:
            self._player = None
            self._video_player = None
        released = set()
        for engine_name in ("vlc_v", "vlc_m"):
            engine = getattr(self, engine_name, None)
            if not engine:
                continue
            if id(engine) in released:
                setattr(self, engine_name, None)
                continue
            try:
                engine.release()
                released.add(id(engine))
            except Exception as ex:
                self.logger.debug(f"WIZARD: {engine_name} release failed: {ex}")
            finally:
                setattr(self, engine_name, None)

    def _disconnect_all_worker_signals(self):
        """Safely disconnect all worker signal connections to prevent memory leaks."""
        workers_to_disconnect = [
            '_track_scanner',
            '_waveform_worker', 
            '_wave_worker',
            '_filmstrip_worker',
            '_video_worker',
            '_music_worker'
        ]
        for worker_name in workers_to_disconnect:
            worker = getattr(self, worker_name, None)
            if not worker:
                continue
            try:
                if hasattr(worker, 'ready'):
                    try: worker.ready.disconnect()
                    except: pass
                if hasattr(worker, 'error'):
                    try: worker.error.disconnect()
                    except: pass
                if hasattr(worker, 'finished'):
                    try: worker.finished.disconnect()
                    except: pass
                if hasattr(worker, 'asset_ready'):
                    try: worker.asset_ready.disconnect()
                    except: pass
                if hasattr(worker, 'scanning_started'):
                    try: worker.scanning_started.disconnect()
                    except: pass
                if hasattr(worker, 'scanning_finished'):
                    try: worker.scanning_finished.disconnect()
                    except: pass
                if hasattr(worker, 'scanning_error'):
                    try: worker.scanning_error.disconnect()
                    except: pass
            except Exception as e:
                self.logger.debug(f"WIZARD: Failed to disconnect signals from {worker_name}: {e}")

    def stop_previews(self):
        if hasattr(self, '_stop_waveform_worker'): self._stop_waveform_worker()
        if hasattr(self, '_temp_sync') and self._temp_sync and os.path.exists(self._temp_sync):
            try: os.remove(self._temp_sync)
            except: pass
        self._temp_sync = None
        if hasattr(self, '_player') and self._player: self._player.stop()
        if hasattr(self, '_video_player') and self._video_player: self._video_player.stop()
        if hasattr(self, '_play_timer'): self._play_timer.stop()
        if hasattr(self, '_filmstrip_worker') and self._filmstrip_worker:
            try:
                if self._filmstrip_worker.isRunning(): self._filmstrip_worker.stop(); self._filmstrip_worker.wait(1000)
            except: pass
        if hasattr(self, '_wave_worker') and self._wave_worker:
            try:
                if self._wave_worker.isRunning(): self._wave_worker.stop(); self._wave_worker.wait(1000)
            except: pass
        if hasattr(self, '_stop_timeline_workers'):
            try:
                self._stop_timeline_workers()
            except Exception as e:
                self.logger.debug(f"WIZARD: timeline workers cleanup failed: {e}")
        if hasattr(self, '_stop_track_scanner'):
            try:
                self._stop_track_scanner()
            except Exception as e:
                self.logger.debug(f"WIZARD: track scanner cleanup failed: {e}")
__all__ = ["PREVIEW_VISUAL_LEAD_MS", "VideoFilmstripWorker", "MusicWaveformWorker", "SearchableListWidget", "MusicItemWidget", "MergerMusicWizard"]
