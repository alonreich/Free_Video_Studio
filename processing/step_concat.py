import os
from .system_utils import create_subprocess

class ConcatProcessor:
    def __init__(self, ffmpeg_path, logger, base_dir, temp_dir):
        self.ffmpeg_path = ffmpeg_path
        self.logger = logger
        self.base_dir = base_dir
        self.temp_dir = temp_dir
        self.current_process = None

    def run_concat(self, intro_path, core_path, progress_signal, cancellation_check=None):
        files_to_concat = []
        if intro_path and os.path.exists(intro_path): 
            files_to_concat.append(intro_path)
        if core_path and os.path.exists(core_path): 
            files_to_concat.append(core_path)
        if not files_to_concat:
            self.logger.error("ConcatProcessor: No valid input files (Intro/Core missing). Aborting.")
            progress_signal.emit(100)
            return None
        output_dir = os.path.join(self.base_dir, '!!!_Output_Video_Files_!!!')
        os.makedirs(output_dir, exist_ok=True)
        if not os.access(output_dir, os.W_OK):
            self.logger.error(f"Permission denied: Cannot write to {output_dir}")
            raise PermissionError(f"Write permission denied for output directory: {output_dir}")
        i = 1
        while True:
            out_name = f"Fortnite-Video-{i}.mp4"
            output_path = os.path.join(output_dir, out_name)
            if not os.path.exists(output_path): 
                break
            i += 1
        concat_list = os.path.join(self.temp_dir, f"concat-{os.getpid()}.txt")
        with open(concat_list, "w", encoding="utf-8") as f:
            for fc in files_to_concat:
                safe_path = fc.replace('\\', '/')
                f.write(f"file '{safe_path}'\n")
        concat_cmd = [
            self.ffmpeg_path, "-y", 
            "-f", "concat", 
            "-safe", "0",
            "-i", concat_list, 
            "-c", "copy", 
            "-movflags", "+faststart",
            output_path
        ]
        self.logger.info("STEP 3/3 CONCAT")
        self.current_process = create_subprocess(concat_cmd, self.logger)
        while True:
            if cancellation_check and cancellation_check():
                self.logger.info("Concat cancelled by user.")
                try:
                    self.current_process.terminate()
                except:
                    pass
                return None
            line = self.current_process.stdout.readline()
            if not line:
                if self.current_process.poll() is not None:
                    break
                continue
            s = line.strip()
            if s:
                self.logger.info(s)
            progress_signal.emit(99)
        self.current_process.wait()
        if self.current_process.returncode == 0:
            progress_signal.emit(100)
            return output_path
        return None