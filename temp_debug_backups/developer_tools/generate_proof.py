import sys
import os
sys.dont_write_bytecode = True
os.environ['PYTHONDONTWRITEBYTECODE'] = '1'
os.environ['PYTHONPYCACHEPREFIX'] = os.path.join(os.path.expanduser('~'), '.null_cache_dir')

import cv2
import numpy as np
import os
import subprocess
import tempfile
from magic_wand import HUDExtractor

def generate_proof():
    example_dir = r"C:\Fortnite_Video_Software\developer_tools\examples"
    videos = [
        "2K.mp4",
        "Fortnite 2026.03.13 - 18.59.33.02.Down.DVR.mp4",
        "Fortnite 2026.03.13 - 18.59.38.03.Elimination.DVR.mp4",
        "HD.mp4"
    ]
    for idx, v_name in enumerate(videos):
        input_file = os.path.join(example_dir, v_name)
        if not os.path.exists(input_file):
            print(f"Skipping {v_name}, not found.")
            continue
        print(f"Processing {v_name} for proof...")
        params = {
            "input_file": input_file,
            "total_ms": 30000,
            "resolution": "1920x1080"
        }
        extractor = HUDExtractor(params=params)
        rois = extractor.extract_all("")
        ffmpeg_exe = "ffmpeg"
        with tempfile.NamedTemporaryFile(suffix=".png", delete=False) as tf:
            tmp_name = tf.name
        try:
            cmd = [ffmpeg_exe, '-ss', '0', '-i', input_file, '-frames:v', '1', '-y', tmp_name]
            subprocess.run(cmd, capture_output=True, creationflags=0x08000000)
            img = cv2.imread(tmp_name)
        finally:
            if os.path.exists(tmp_name): os.unlink(tmp_name)
        if img is not None:
            for r in rois:
                x, y, w, h = r.x(), r.y(), r.width(), r.height()
                cv2.rectangle(img, (x, y), (x+w, y+h), (147, 20, 255), 4)
                cv2.putText(img, "HUD", (x, y-10), cv2.FONT_HERSHEY_SIMPLEX, 1.2, (147, 20, 255), 3)
            output_name = f"{idx+1}.jpg"
            output_path = os.path.join(example_dir, output_name)
            cv2.imwrite(output_path, img)
            print(f"Saved proof: {output_name} with {len(rois)} elements.")
if __name__ == "__main__":
    generate_proof()
