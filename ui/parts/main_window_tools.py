import os, sys, time, threading, logging, subprocess, traceback
from PyQt5.QtCore import *
from PyQt5.QtGui import *
from PyQt5.QtWidgets import *

class MainWindowToolsMixin:
    def launch_crop_tool(self):
        self.logger.info("TRIGGER: launch_crop_tool called.")
        try:
            self.setEnabled(False)
            self.show_priority_message("🚀 Launching Crop Tool... Please wait.", 8000, is_critical=True)
            QCoreApplication.processEvents()
            root_dir = os.path.abspath(self.base_dir)
            dev_tools_dir = os.path.join(root_dir, 'developer_tools')
            script_path = os.path.join(dev_tools_dir, 'crop_tools.py')
            if not os.path.exists(script_path):
                raise FileNotFoundError(f"Crop Tool script not found at: {script_path}")
            state = {
                "input_file": self.input_file_path,
                "trim_start": self.trim_start_ms,
                "trim_end": self.trim_end_ms,
                "speed_segments": self.speed_segments,
                "hardware_mode": getattr(self, "hardware_strategy", "CPU"),
                "resolution": getattr(self, "original_resolution", None)
            }

            from system.state_transfer import StateTransfer
            StateTransfer.save_state(state)
            if self.player:
                self.player.stop()
            env = os.environ.copy()
            norm_root = os.path.normpath(root_dir)
            norm_dev = os.path.normpath(dev_tools_dir)
            env["PYTHONPATH"] = os.pathsep.join(filter(None, [norm_dev, norm_root, env.get("PYTHONPATH", "")]))
            cmd = [sys.executable, "-B", script_path]
            if self.input_file_path:
                cmd.append(self.input_file_path)
            creation_flags = 0
            if sys.platform == "win32":
                creation_flags = 0x00000008 | 0x00000200
            proc = subprocess.Popen(cmd, cwd=norm_dev, env=env, creationflags=creation_flags, close_fds=True, shell=False)
            time.sleep(1.0)
            if proc.poll() is not None:
                raise RuntimeError(f"Crop Tool failed to start (Exit Code: {proc.returncode})")
            self._switching_app = True
            self.logger.info("Crop Tool launched successfully. Closing parent.")
            self.close()
        except Exception as e:
            self.setEnabled(True)
            self.logger.critical(f"ERROR: Failed to launch Crop Tool. Error: {e}")
            QMessageBox.critical(self, "Launch Failed", f"Could not launch Crop Tool.\n\nVerify that the 'developer_tools' folder and 'crop_tools.py' exist.\n\nError: {e}")

    def launch_advanced_editor(self):
        try:
            from system.state_transfer import StateTransfer
            state = {"input_file": self.input_file_path, "hardware_mode": getattr(self, "hardware_strategy", "CPU")}
            StateTransfer.save_state(state)
            self.logger.info("ACTION: Launching Advanced Video Editor via F11...")
            command = [sys.executable, os.path.join(self.base_dir, 'advanced', 'advanced_video_editor.py')]
            if self.input_file_path:
                command.append(self.input_file_path)
            if self.player:
                self.player.stop()
            subprocess.Popen(command, cwd=self.base_dir)
            self.logger.info("Advanced Editor process started. Closing main app.")
            self.close()
        except Exception as e:
            self.logger.critical(f"ERROR: Failed to launch Advanced Editor. Error: {e}")
            QMessageBox.critical(self, "Launch Failed", f"Could not launch Advanced Editor. Error: {e}")

































