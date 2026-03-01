import os
import pytest
import subprocess
import json
import hashlib
from PyQt5.QtWidgets import QApplication
from processing.worker import ProcessThread
from processing.media_utils import MediaProber
from system.logger import setup_logger
import logging

def find_test_video():
    paths = [
        os.path.expandvars(r"%LOCALAPPDATA%\Temp\Highlights\Fortnite"),
        os.path.expandvars(r"%USERPROFILE%\Videos\Fortnite")
    ]
    for p in paths:
        if os.path.exists(p):
            for f in os.listdir(p):
                if f.endswith(".mp4"):
                    return os.path.join(p, f)
    return None

def verify_video_movement(video_path):
    """Verifies that frames in the gameplay core are unique (not frozen)."""
    bin_dir = os.path.join(os.path.dirname(__file__), '..', 'binaries')
    ffmpeg = os.path.join(bin_dir, 'ffmpeg.exe')
    out_sheet = "movement_sheet_max.bmp"
    cmd = [
        ffmpeg, "-y", "-ss", "5", "-i", video_path, 
        "-vf", "select='not(mod(n,60))',tile=5x1", 
        "-vframes", "1", out_sheet
    ]
    subprocess.run(cmd, capture_output=True)
    if not os.path.exists(out_sheet): return False
    with open(out_sheet, "rb") as f: data = f.read()
    os.remove(out_sheet)
    chunk_size = len(data) // 5
    hashes = set()
    for i in range(5):
        chunk = data[i*chunk_size : (i+1)*chunk_size]
        hashes.add(hashlib.md5(chunk).hexdigest())
    return len(hashes) >= 3

class DummySignal:
    def emit(self, *args):
        pass
@pytest.mark.timeout(300)
def test_real_video_max_quality():
    vid = find_test_video()
    if not vid:
        pytest.skip("No real video found in user directories to test.")
    base_dir = os.path.abspath(os.path.join(os.path.dirname(__file__), '..'))
    logger = setup_logger(base_dir, "sanity_max.log", "SanityMAX")
    bin_dir = os.path.join(base_dir, 'binaries')
    prober = MediaProber(bin_dir, vid)
    orig_dur = prober.get_duration()
    orig_size = os.path.getsize(vid)
    trimmed_dur = 10.0 
    total_expected_dur = 13.0
    expected_size_mb = (orig_size / (1024*1024)) * (total_expected_dur / max(1.0, orig_dur))
    script_dir = os.path.abspath(os.path.join(os.path.dirname(__file__), '..', 'processing'))
    app = QApplication.instance() or QApplication([])
    results = []

    class FinishedSignal:
        def emit(self, success, path):
            results.append((success, path))
            app.quit()
    thread = ProcessThread(
        input_path=vid,
        start_time_ms=10000,
        end_time_ms=20000,
        original_resolution="1920x1080",
        is_mobile_format=True,
        speed_factor=1.0,
        script_dir=script_dir,
        progress_update_signal=DummySignal(),
        status_update_signal=DummySignal(),
        finished_signal=FinishedSignal(),
        logger=logger,
        is_boss_hp=False,
        show_teammates_overlay=True,
        quality_level=4,
        portrait_text="test",
        intro_still_sec=3.0,
        hardware_strategy="NVIDIA"
    )
    thread.start()
    app.exec_()
    assert len(results) == 1
    success, out_path = results[0]
    assert success is True, f"Render failed: {out_path}"
    assert os.path.exists(out_path)
    size_mb = os.path.getsize(out_path) / (1024 * 1024)
    logger.info(f"Original file: {orig_size/(1024*1024):.2f} MB | Orig Duration: {orig_dur:.2f}s")
    logger.info(f"Expected trimmed size: {expected_size_mb:.2f} MB")
    logger.info(f"Final output size: {size_mb:.2f} MB")
    diff = abs(size_mb - expected_size_mb)
    assert diff <= 20.0, f"Output size ({size_mb:.2f}MB) is too far from expected trimmed size ({expected_size_mb:.2f}MB)! Diff: {diff:.2f}MB (Max 20MB allowed)"
    assert verify_video_movement(out_path), "Output video appears to be a frozen frame!"
    if os.path.exists(out_path):
        os.remove(out_path)
