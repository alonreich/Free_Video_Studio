import os, sys, subprocess
from PyQt5.QtCore import *
from PyQt5.QtGui import *
from PyQt5.QtWidgets import *

class MainWindowToolsMixin:
    def launch_crop_tool(self):
        try:
            self.hide()
            root_dir = os.path.abspath(self.base_dir); script_path = os.path.join(root_dir, 'developer_tools', 'crop_tools.py')
            if not os.path.exists(script_path): raise FileNotFoundError(f"Crop Tool script not found: {script_path}")
            state = {"input_file": self.input_file_path, "trim_start": self.trim_start_ms, "trim_end": self.trim_end_ms, "speed_segments": self.speed_segments, "hardware_mode": getattr(self, "hardware_strategy", "CPU"), "resolution": getattr(self, "original_resolution", None)}

            from system.state_transfer import StateTransfer
            StateTransfer.save_state(state)
            if self.player: self.player.stop()
            env = os.environ.copy(); env["PYTHONPATH"] = os.pathsep.join(filter(None, [os.path.join(root_dir, 'developer_tools'), root_dir, env.get("PYTHONPATH", "")]))
            cmd = [sys.executable, "-B", script_path]
            if self.input_file_path: cmd.append(self.input_file_path)
            creation_flags = 0
            if sys.platform == "win32": creation_flags = 0x00000008 | 0x00000200
            proc = subprocess.Popen(cmd, cwd=os.path.join(root_dir, 'developer_tools'), env=env, creationflags=creation_flags, close_fds=True, shell=False)

            def _check_proc():
                if proc.poll() is not None:
                    self.show(); QMessageBox.critical(self, "Launch Failed", f"Crop Tool failed to start (Code: {proc.returncode})")
                else: self.close()
            QTimer.singleShot(2000, _check_proc)
        except Exception as e:
            self.show(); QMessageBox.critical(self, "Launch Failed", f"Could not launch Crop Tool: {e}")

    def launch_advanced_editor(self):
        try:
            from system.state_transfer import StateTransfer
            state = {"input_file": self.input_file_path, "hardware_mode": getattr(self, "hardware_strategy", "CPU")}; StateTransfer.save_state(state)
            command = [sys.executable, os.path.join(self.base_dir, 'advanced', 'advanced_video_editor.py')]
            if self.input_file_path: command.append(self.input_file_path)
            if self.player: self.player.stop()
            subprocess.Popen(command, cwd=self.base_dir); self.close()
        except Exception as e: QMessageBox.critical(self, "Launch Failed", f"Could not launch Advanced Editor: {e}")
