from PyQt5.QtCore import QProcess, Qt
from PyQt5.QtWidgets import QMessageBox
import tempfile
from pathlib import Path
import time
from utilities.merger_utils import _human, _get_logger
from utilities.merger_ffmpeg_process import FFMpegProcessMixin

class FFMpegHandler(FFMpegProcessMixin):
    def __init__(self, parent):
        self.parent = parent
        self.logger = _get_logger()
        self.process = None
        self._temp_dir = None
        self._output_path = ""
        self._cmd = []

    def merge_now(self):
        n = self.parent.listw.count()
        if n < 2:
            QMessageBox.information(self.parent, "Need more videos", "Please add at least 2 videos to merge.")
            return
        last_out_dir = self.parent._last_out_dir if self.parent._last_out_dir and Path(self.parent._last_out_dir).exists() else str(Path.home() / "Downloads")
        default_path = str(Path(last_out_dir) / "merged_video.mp4")
        out_path, _ = self.parent.open_save_dialog(default_path)
        if not out_path:
            return
        self.parent._last_out_dir = str(Path(out_path).parent)
        self.parent.logic_handler.save_config()
        self._temp_dir = tempfile.TemporaryDirectory()
        concat_txt = Path(self._temp_dir.name, "concat_list.txt")
        with concat_txt.open("w", encoding="utf-8") as f:
            for i in range(n):
                it = self.parent.listw.item(i)
                escaped_path = it.data(Qt.UserRole).replace("'", "'\\''")
                f.write(f"file '{escaped_path}'\n")
        self._output_path = out_path
        music_path, music_vol = self.parent.music_handler.get_selected_music()
        music_offset = self.parent.music_offset_input.value()
        base_cmd = [self.parent.ffmpeg, "-y", "-f", "concat", "-safe", "0", "-i", str(concat_txt)]
        if music_path:
            self.logger.info("MUSIC: Adding background music. Audio will be re-encoded.")
            base_cmd.extend(["-i", music_path])
            self._cmd = base_cmd + [
                "-filter_complex", f"[0:a]volume=1.0[a0]; [1:a]atrim=start={music_offset:.3f},volume={music_vol:.3f}[a1]; [a0][a1]amix=inputs=2:duration=first[a_out]",
                "-map", "0:v",
                "-map", "[a_out]",
                "-c:v", "copy",
                "-c:a", "aac", "-b:a", "192k",
                "-shortest",
                str(out_path)
            ]
        else:
            self.logger.info("MUSIC: No background music. Using fast stream copy.")
            self._cmd = base_cmd + [
                "-c", "copy",
                str(out_path)
            ]
        inputs = []
        total_in = 0
        for i in range(n):
            it = self.parent.listw.item(i)
            f = it.data(Qt.UserRole)
            try:
                sz = Path(f).stat().st_size
                total_in += sz
                inputs.append({"path": f, "size": _human(sz)})
            except Exception:
                inputs.append({"path": f, "size": "?"})
        self.logger.info("MERGE_START: outputs='%s'", self._output_path)
        self.logger.info("MERGE_INPUTS: %s", inputs)
        self.logger.info("MERGE_CMD: %s", " ".join(self._cmd))
        self.logger.info("MERGE_TOTAL_INPUT_SIZE: %s", _human(total_in))
        self.process = QProcess(self.parent)
        self.process.finished.connect(self._merge_finished)
        self.process.readyReadStandardError.connect(self._process_ffmpeg_output)
        self._merge_started_at = time.time()
        self.parent.set_ui_busy(True)
        self.logger.info("MERGE_EXEC: %s", " ".join(self._cmd))
        self.process.start(self.parent.ffmpeg, self._cmd[1:])