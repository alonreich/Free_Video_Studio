import sys
import os
import time
import socket
import threading
import json
import ctypes
import mmap
import struct
BIN_DIR = os.path.join(os.getcwd(), 'binaries')
if os.path.exists(BIN_DIR):
    os.environ["PATH"] = BIN_DIR + os.pathsep + os.environ.get("PATH", "")
    if hasattr(os, 'add_dll_directory'):
        try:
            os.add_dll_directory(BIN_DIR)
        except Exception: pass
try:
    import vlc
except ImportError:
    sys.path.append(os.getcwd())
    try:
        import vlc
    except ImportError:
        vlc = None
LOG_DIR = os.path.join(os.getcwd(), 'logs')
os.makedirs(LOG_DIR, exist_ok=True)
WORKER_LOG = os.path.join(LOG_DIR, f"vlc_worker_native_{os.getpid()}.log")

def worker_log(msg):
    """Writes a timestamped message to the worker log file."""
    try:
        with open(WORKER_LOG, "a", encoding="utf-8") as f:
            f.write(f"{time.ctime()} | {msg}\n")
    except Exception:
        pass

class StatusMemoryBridge:
    """[NEW] Fast Shared Memory Bridge for zero-latency status updates."""

    def __init__(self, port):
        self.tag = f"FVS_VLC_STATUS_{port}"
        self.size = 64
        self.shm = mmap.mmap(-1, self.size, tagname=self.tag, access=mmap.ACCESS_WRITE)
        worker_log(f"Shared Memory Bridge initialized: {self.tag}")

    def update(self, state, current_time, length):
        try:
            s = int(state)
            t = int(current_time)
            l = int(length)
            data = struct.pack("iqq", s, t, l)
            self.shm.seek(0)
            self.shm.write(data)
        except Exception as e:
            worker_log(f"SHM Update Error: {e}")

    def close(self):
        try: self.shm.close()
        except: pass

class VLCWorker:
    def __init__(self, port, mode):
        self.port = port
        self.mode = mode
        self.player = None
        self.instance = None
        self.running = True
        self.bridge = StatusMemoryBridge(port)
        self.parent_pid = os.getppid()
        self.monitor_thread = threading.Thread(target=self._monitor_parent, daemon=True)
        self.monitor_thread.start()
        self.server_ready = threading.Event()
        self.server_thread = threading.Thread(target=self._run_server, daemon=True)
        self.server_thread.start()
        if not self.server_ready.wait(timeout=5.0):
            worker_log("FATAL: Server thread failed to bind socket in time.")
            sys.exit(1)
        vlc_args = [
            "--verbose=2", 
            "--no-osd", 
            "--no-video-title-show",
            "--ignore-config",
            "--avcodec-hw=any",
            "--vout=direct3d11",
            "--file-caching=500",
            "--network-caching=500",
            "--drop-late-frames",
            "--skip-frames",
            "--clock-jitter=0",
            "--clock-synchro=0"
        ]
        try:
            worker_log(f"Initializing VLC Instance ({self.mode}) with args: {vlc_args}")
            self.instance = vlc.Instance(vlc_args)
            self.player = self.instance.media_player_new()

            def vlc_log_callback(data, level, ctx, fmt, args):
                pass
            worker_log("VLC Player created successfully.")
            self.status_thread = threading.Thread(target=self._status_loop, daemon=True)
            self.status_thread.start()
        except Exception as e:
            worker_log(f"FATAL: VLC Initialization failed: {e}")
            sys.exit(1)

    def _coerce_int(self, value, default=0):
        """Best-effort conversion for VLC values that may arrive as enum/bytes/strings."""
        try:
            if value is None:
                return int(default)
            if hasattr(value, 'value'):
                return int(value.value)
            if isinstance(value, int):
                return int(value)
            if isinstance(value, float):
                return int(value)
            if isinstance(value, bytes):
                if len(value) == 0:
                    return int(default)
                if len(value) <= 4:
                    return int.from_bytes(value, byteorder="little", signed=False)
                if len(value) <= 8:
                    return int.from_bytes(value[:8], byteorder="little", signed=False)
            if isinstance(value, str):
                raw = value.strip()
                if raw.startswith("b'") or raw.startswith('b"'):
                    import ast
                    try:
                        b = ast.literal_eval(raw)
                        if isinstance(b, (bytes, bytearray)):
                            return int.from_bytes(bytes(b), byteorder="little", signed=False)
                    except: pass
                return int(float(raw))
            return int(value)
        except Exception:
            return int(default)

    def _monitor_parent(self):
        """Periodically checks if parent process is still alive."""

        import psutil
        while self.running:
            try:
                parent = psutil.Process(self.parent_pid)
                if not parent.is_running() or parent.status() == psutil.STATUS_ZOMBIE:
                    worker_log(f"Parent process {self.parent_pid} terminated. Shutting down worker.")
                    self.running = False
                    break
            except (psutil.NoSuchProcess, psutil.AccessDenied):
                worker_log(f"Parent process {self.parent_pid} not found. Shutting down worker.")
                self.running = False
                break
            time.sleep(2.0)

    def _status_loop(self):
        """High-speed loop to update shared memory."""
        while self.running:
            try:
                if self.player:
                    st = self._coerce_int(self.player.get_state(), 0)
                    tm = self._coerce_int(self.player.get_time(), 0)
                    ln = self._coerce_int(self.player.get_length(), 0)
                    self.bridge.update(st, tm, ln)
            except: pass
            time.sleep(0.025)

    def _run_server(self):
        """Runs the IPC server to listen for commands from the main app."""
        try:
            sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
            sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
            sock.bind(('127.0.0.1', self.port))
            sock.listen(5)
            sock.settimeout(1.0)
            self.server_ready.set()
            worker_log(f"IPC Server listening on port {self.port}")
        except Exception as e:
            worker_log(f"FATAL: IPC Server failed to bind: {e}")
            return
        while self.running:
            try:
                conn, addr = sock.accept()
                with conn:
                    conn.settimeout(None)
                    buffer = ""
                    while self.running:
                        data = conn.recv(8192).decode('utf-8')
                        if not data:
                            break
                        buffer += data
                        while "\n" in buffer:
                            line, buffer = buffer.split("\n", 1)
                            if not line.strip():
                                continue
                            try:
                                cmd = json.loads(line)
                                response = self._handle_command(cmd)
                                conn.sendall((json.dumps(response) + "\n").encode('utf-8'))
                                if cmd.get('action') == 'quit':
                                    self.running = False
                                    break
                            except json.JSONDecodeError:
                                worker_log(f"JSON Error: Received malformed line: {line}")
                            except Exception as e:
                                worker_log(f"Command Execution Error: {e}")
            except socket.timeout:
                continue
            except Exception as e: 
                worker_log(f"Server Loop Error: {e}")
                time.sleep(0.1)

    def _handle_command(self, cmd):
        """Dispatch commands to the VLC player."""
        action = cmd.get('action')
        if action == 'ping':
            return {'status': 'ok', 'pid': os.getpid()}
        elif action == 'load':
            path = cmd.get('path')
            m = self.instance.media_new(path)
            self.player.set_media(m)
            return {'status': 'ok'}
        elif action == 'play':
            self.player.play()
            return {'status': 'ok'}
        elif action == 'pause':
            self.player.pause()
            return {'status': 'ok'}
        elif action == 'stop':
            self.player.stop()
            return {'status': 'ok'}
        elif action == 'set_volume':
            try:
                vol_pct = self._coerce_int(cmd.get('volume', 100), 100)
                self.player.audio_set_volume(vol_pct)
            except (ValueError, TypeError) as e:
                worker_log(f"Volume Parse Error: {e} | Value: {cmd.get('volume')}")
            return {'status': 'ok'}
        elif action == 'set_mute':
            try:
                mute = bool(cmd.get('mute', False))
                self.player.audio_set_mute(mute)
            except Exception as e:
                worker_log(f"Mute Parse/Error: {e} | Value: {cmd.get('mute')}")
            return {'status': 'ok'}
        elif action == 'set_time':
            try:
                ms = self._coerce_int(cmd.get('time', 0), 0)
                self.player.set_time(ms)
            except (ValueError, TypeError) as e:
                worker_log(f"Time Parse Error: {e} | Value: {cmd.get('time')}")
            return {'status': 'ok'}
        elif action == 'get_state':
            return {
                'state': self._coerce_int(self.player.get_state(), 0), 
                'time': self._coerce_int(self.player.get_time(), 0), 
                'length': self._coerce_int(self.player.get_length(), 0)
            }
        elif action == 'set_rate':
            try:
                rate = float(cmd.get('rate', 1.0))
                self.player.set_rate(rate)
            except (ValueError, TypeError) as e:
                worker_log(f"Rate Parse Error: {e} | Value: {cmd.get('rate')}")
            return {'status': 'ok'}
        elif action == 'set_hwnd':
            try:
                raw_hwnd = cmd.get('hwnd', 0)
                if isinstance(raw_hwnd, str) and raw_hwnd.startswith("b'"):
                    import ast
                    raw_hwnd = ast.literal_eval(raw_hwnd)
                if isinstance(raw_hwnd, bytes):
                    if len(raw_hwnd) == 4:
                        wid = int.from_bytes(raw_hwnd, byteorder='little', signed=False)
                    elif len(raw_hwnd) == 8:
                        wid = int.from_bytes(raw_hwnd, byteorder='little', signed=False)
                    else:
                        wid = self._coerce_int(raw_hwnd, 0)
                else:
                    wid = self._coerce_int(raw_hwnd, 0)
                if sys.platform.startswith('win'):
                    self.player.set_hwnd(wid)
            except Exception as e:
                worker_log(f"HWND Parse Error: {e} | Value: {cmd.get('hwnd')} (Type: {type(cmd.get('hwnd'))})")
            return {'status': 'ok'}
        elif action == 'get_tracks':
            raw_tracks = self.player.audio_get_track_description()
            tracks = []
            for tid, tname in raw_tracks:
                if isinstance(tname, bytes):
                    tname = tname.decode('utf-8', errors='replace')
                tracks.append((tid, tname))
            return {'status': 'ok', 'tracks': tracks}
        elif action == 'set_track':
            try:
                track_id = self._coerce_int(cmd.get('track_id', 1), 1)
                self.player.audio_set_track(track_id)
            except (ValueError, TypeError):
                pass
            return {'status': 'ok'}
        elif action == 'quit':
            self.running = False
            return {'status': 'ok'}
        return {'status': 'error', 'message': 'unknown action'}
if __name__ == "__main__":
    if len(sys.argv) < 3:
        worker_log("FATAL: Not enough arguments provided.")
        sys.exit(1)
    try:
        port = int(sys.argv[1])
        mode = sys.argv[2]
        worker = VLCWorker(port, mode)
        while worker.running:
            time.sleep(0.1)
    except KeyboardInterrupt:
        worker_log("Worker terminated by user.")
    except Exception as e:
        worker_log(f"Worker crashed: {e}")
    finally:
        if 'worker' in locals():
            if worker.player:
                worker.player.stop()
                worker.player.release()
            if worker.instance:
                worker.instance.release()
        worker_log("--- WORKER EXIT ---")