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

def debug():
    input_file = r"examples\HD.mp4"
    params = {"input_file": input_file, "total_ms": 30000}
    extractor = HUDExtractor(params=params)
    frames = extractor._extract_temporal_frames(input_file, 30000, lambda: False)
    print(f"Frames extracted: {len(frames)}")
    if not frames: return
    h, w = frames[0].shape[:2]
    persistence_map = np.zeros((h, w), dtype=np.float32)
    active_count = 0
    for i in range(len(frames)-1):
        diff = cv2.absdiff(frames[i], frames[i+1])
        gray_diff = cv2.cvtColor(diff, cv2.COLOR_BGR2GRAY)
        _, m = cv2.threshold(gray_diff, 20, 255, cv2.THRESH_BINARY_INV)
        static_ratio = cv2.countNonZero(m) / float(h*w)
        print(f"  Frame {i} static_ratio: {static_ratio:.4f}")
        if static_ratio > 0.85:
            print(f"    -> Skipping frame {i} (Too static)")
            continue
        persistence_map += (m / 255.0)
        active_count += 1
    print(f"Final active_count: {active_count}")
    if active_count > 0:
        persistence_map /= active_count
        _, final_mask = cv2.threshold(persistence_map, 0.10, 255, cv2.THRESH_BINARY)
        print(f"Final mask pixels: {cv2.countNonZero(final_mask)}")
        rois = extractor.extract_all("")
        print(f"Extractor returned {len(rois)} ROIs.")
if __name__ == "__main__":
    debug()
