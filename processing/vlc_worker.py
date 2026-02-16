import sys
import os
import time
import socket
import threading
import json
import ctypes
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
worker_log(f"--- WORKER START (PID={os.getpid()}) ---")
worker_log(f"Args: {sys.argv}")
worker_log(f"CWD: {os.getcwd()}")
BIN_DIR = os.path.join(os.getcwd(), 'binaries')
os.environ["PATH"] = BIN_DIR + os.pathsep + os.environ.get("PATH", "")
if hasattr(os, 'add_dll_directory'):
    try:
        os.add_dll_directory(BIN_DIR)
        worker_log(f"Added DLL directory: {BIN_DIR}")
    except Exception as e:
        worker_log(f"Failed to add DLL directory: {e}")
try:
    import vlc
    worker_log("VLC module imported successfully.")
except ImportError:
    try:
        sys.path.append(os.getcwd())

        import vlc
        worker_log("VLC module imported from CWD.")
    except ImportError:
        worker_log("FATAL: vlc module not found in worker")
        sys.exit(1)

class VLCWorker:
    def __init__(self, port, mode):
        self.port = port
        self.mode = mode
        self.player = None
        self.instance = None
        self.running = True
        self.server_ready = threading.Event()
        self.server_thread = threading.Thread(target=self._run_server, daemon=True)
        self.server_thread.start()
        if not self.server_ready.wait(timeout=5.0):
            worker_log("FATAL: Server thread failed to bind socket in time.")
            sys.exit(1)
        vlc_args = [
            "--verbose=0", 
            "--no-osd", 
            "--ignore-config",
            "--avcodec-hw=any",
            "--vout=direct3d11",
            "--file-caching=1000",
            "--network-caching=1000",
            "--clock-jitter=0",
            "--clock-synchro=0"
        ]
        try:
            worker_log(f"Initializing VLC Instance with args: {vlc_args}")
            self.instance = vlc.Instance(vlc_args)
            self.player = self.instance.media_player_new()
            worker_log("VLC Player created successfully.")
        except Exception as e:
            worker_log(f"FATAL: VLC Initialization failed: {e}")
            sys.exit(1)

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
                    while self.running:
                        data = conn.recv(8192).decode('utf-8')
                        if not data: break
                        try:
                            cmd = json.loads(data)
                            response = self._handle_command(cmd)
                            conn.sendall(json.dumps(response).encode('utf-8'))
                            if cmd.get('action') == 'quit':
                                break
                        except json.JSONDecodeError:
                             worker_log(f"JSON Error: Received malformed data: {data}")
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
                vol_pct = int(cmd.get('volume', 100))
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
                ms = int(cmd.get('time', 0))
                self.player.set_time(ms)
            except (ValueError, TypeError) as e:
                worker_log(f"Time Parse Error: {e} | Value: {cmd.get('time')}")
            return {'status': 'ok'}
        elif action == 'get_state':
            return {
                'state': int(self.player.get_state()), 
                'time': self.player.get_time(), 
                'length': self.player.get_length()
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
                    import struct
                    if len(raw_hwnd) == 4: wid = struct.unpack('<I', raw_hwnd)[0]
                    elif len(raw_hwnd) == 8: wid = struct.unpack('<Q', raw_hwnd)[0]
                    else: wid = int(raw_hwnd)
                else:
                    wid = int(raw_hwnd)
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
                track_id = int(cmd.get('track_id', 1))
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