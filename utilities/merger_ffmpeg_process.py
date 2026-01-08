from PyQt5.QtCore import QProcess
from PyQt5.QtWidgets import QMessageBox
import time
import os
from pathlib import Path

class FFMpegProcessMixin:

    def _process_ffmpeg_output(self):
        """Extracts and displays the current progress from FFmpeg's stderr output."""
        try:
            output = self.process.readAllStandardError().data().decode().strip()
            last_line = output.split('\r')[-1].split('\n')[-1].strip()
            if last_line and (last_line.startswith("frame=") or last_line.startswith("size=")):
                self.parent.status_updated.emit(last_line)
            self.logger.debug("FFMPEG_OUTPUT: %s", output)
        except Exception:
            pass

    def _merge_finished(self, exit_code, exit_status):
        self.parent.ui_handler._hide_processing_overlay()
        try:
            self.parent.btn_merge.setEnabled(True)
            self.parent.btn_back.setEnabled(True)
            self.parent.listw.setEnabled(True)
            self.parent.btn_merge.setText("Merge Videos")
        except Exception:
            pass
        """Handle the result of the QProcess merge."""
        try:
            stdout = self.process.readAllStandardOutput().data().decode()
        except Exception:
            stdout = ""
        try:
            stderr = self.process.readAllStandardError().data().decode()
        except Exception:
            stderr = ""
        elapsed = None
        try:
            if hasattr(self, "_merge_started_at"):
                elapsed = max(0, time.time() - self._merge_started_at)
        except Exception:
            pass
        concat_txt_path = None
        if self._temp_dir and hasattr(self._temp_dir, 'name'):
            concat_txt_path = Path(self._temp_dir.name) / "concat_list.txt"
        if self._temp_dir:
            try:
                self._temp_dir.cleanup()
                self.logger.info("Cleaned up temporary directory: %s", self._temp_dir.name)
            except Exception as e:
                self.logger.error("Error cleaning up temporary directory: %s", e)
            finally:
                self._temp_dir = None 
        if concat_txt_path and concat_txt_path.exists():
            try:
                os.remove(concat_txt_path)
                self.logger.info("Removed temporary concat list: %s", concat_txt_path)
            except Exception as e:
                self.logger.error("Error removing concat list %s: %s", concat_txt_path, e)
        self.parent.set_ui_busy(False)
        self.process = None
        if exit_status == QProcess.CrashExit:
            QMessageBox.critical(self.parent, "Merge Failed", "FFmpeg process crashed unexpectedly.")
            return
        if exit_code != 0:
            QMessageBox.critical(
                self.parent,
                "Merge failed",
                "ffmpeg reported an error. This usually means inputs have mismatched codecs.\n"
                "Ensure all videos share the same codec, resolution, and audio format for lossless concatenation.\n\n"
                f"Output:\n{(stdout + stderr)[:4000]}"
            )
            return
        self.parent.show_success_dialog(self._output_path)