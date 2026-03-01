import os
import pytest
import shutil
import tempfile
import subprocess
from pathlib import Path
from utilities.merger_engine import MergerEngine

def create_dummy_video(path, size_mb, bin_dir):
    """Creates a dummy video file using ffmpeg."""
    ffmpeg = os.path.join(bin_dir, "ffmpeg.exe")
    cmd = [
        ffmpeg, "-y", "-f", "lavfi", "-i", "testsrc=duration=5:size=1280x720:rate=30",
        "-f", "lavfi", "-i", "sine=frequency=1000:duration=5",
        "-c:v", "libx264", "-b:v", "8000k", "-c:a", "aac", "-b:a", "192k",
        str(path)
    ]
    subprocess.run(cmd, check=True, capture_output=True, creationflags=0x08000000)
@pytest.mark.skipif(not os.path.exists(r"C:\Fortnite_Video_Software\binaries\ffmpeg.exe"), reason="FFmpeg not found")
def test_merger_quality_file_sizes():
    """
    Sanity test for quality levels.
    Verifies that lower quality levels result in smaller file sizes.
    """
    base_dir = r"C:\Fortnite_Video_Software"
    bin_dir = os.path.join(base_dir, "binaries")
    ffmpeg = os.path.join(bin_dir, "ffmpeg.exe")
    with tempfile.TemporaryDirectory() as tmp_dir:
        v1 = Path(tmp_dir) / "v1.mp4"
        v2 = Path(tmp_dir) / "v2.mp4"
        v3 = Path(tmp_dir) / "v3.mp4"
        print("Creating dummy videos...")
        create_dummy_video(v1, 10, bin_dir)
        create_dummy_video(v2, 10, bin_dir)
        create_dummy_video(v3, 10, bin_dir)
        original_combined_size = v1.stat().st_size + v2.stat().st_size + v3.stat().st_size
        print(f"Original combined size: {original_combined_size / 1024 / 1024:.2f} MB")
        results = {}
        for q_level in [4, 3, 0]:
            out_path = Path(tmp_dir) / f"merged_q{q_level}.mp4"
            concat_txt = Path(tmp_dir) / f"list_q{q_level}.txt"
            with open(concat_txt, "w") as f:
                f.write(f"file '{str(v1).replace(os.sep, '/')}'\n")
                f.write(f"file '{str(v2).replace(os.sep, '/')}'\n")
                f.write(f"file '{str(v3).replace(os.sep, '/')}'\n")
            cmd_base = ["-f", "concat", "-safe", "0", "-i", str(concat_txt)]
            print(f"Running merge at quality {q_level}...")
            engine = MergerEngine(ffmpeg, cmd_base, out_path, total_duration_sec=15, quality_level=q_level)
            engine.target_v_bitrate = 8000000
            engine.start()
            engine.wait(60000)
            if out_path.exists():
                results[q_level] = out_path.stat().st_size
                print(f"Quality {q_level} size: {results[q_level] / 1024 / 1024:.2f} MB")
            else:
                pytest.fail(f"Engine failed to produce output for quality {q_level}")
        assert results[4] > results[3], "100% quality should be larger than 80%"
        assert results[3] > results[0], "80% quality should be larger than 20%"
        ratio_20 = results[0] / results[4]
        print(f"Ratio 20%/100%: {ratio_20:.2%}")
        assert results[0] < results[4], "20% quality should result in smaller file than 100%"
if __name__ == "__main__":
    try:
        test_merger_quality_file_sizes()
        print("\nSUCCESS: Quality levels verified.")
    except Exception as e:
        import traceback
        traceback.print_exc()
        print(f"\nFAILURE: {e}")
