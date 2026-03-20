import cv2
import numpy as np
import os
import subprocess
import tempfile

def test_magic_wand_logic(input_file):
    print(f"DEBUG: Analyzing {input_file}")
    total_ms = 30000
    num_frames = 30
    interval_ms = 1000
    ffmpeg_path = r"C:\Fortnite_Video_Software\developer_tools\binaries\ffmpeg.exe"
    if not os.path.exists(ffmpeg_path): ffmpeg_path = "ffmpeg"
    frames = []
    first_frame_size = None
    for i in range(num_frames):
        time_s = (i * interval_ms) / 1000.0
        with tempfile.NamedTemporaryFile(suffix=".png", delete=False) as tf:
            tmp_name = tf.name
        try:
            cmd = [ffmpeg_path, '-ss', str(time_s), '-i', input_file, '-frames:v', '1', '-q:v', '2', '-y', tmp_name]
            subprocess.run(cmd, capture_output=True, timeout=10, creationflags=0x08000000)
            img = cv2.imread(tmp_name)
            if img is not None:
                if i == 0: first_frame_size = img.shape[:2]
                small = cv2.resize(img, (0,0), fx=0.5, fy=0.5)
                frames.append(small)
        finally:
            if os.path.exists(tmp_name): os.unlink(tmp_name)
    print(f"DEBUG: Extracted {len(frames)} frames.")
    if len(frames) < 3: return
    h, w = frames[0].shape[:2]
    persistence_map = np.zeros((h, w), dtype=np.float32)
    active_count = 0
    for i in range(len(frames)-1):
        diff = cv2.absdiff(frames[i], frames[i+1])
        gray_diff = cv2.cvtColor(diff, cv2.COLOR_BGR2GRAY)
        _, m = cv2.threshold(gray_diff, 30, 255, cv2.THRESH_BINARY_INV)
        static_ratio = cv2.countNonZero(m) / float(h*w)
        if static_ratio > 0.92:
            continue
        persistence_map += (m / 255.0)
        active_count += 1
    print(f"DEBUG: Active transitions found: {active_count}")
    if active_count == 0:
        print("DEBUG: FAILURE - No movement detected in video. Background is too static.")
        return
    persistence_map /= active_count
    zones = [(0, 0.5, 0.5, 1.0), (0, 0.3, 0.1, 0.5), (0.6, 1.0, 0.6, 1.0), (0.7, 1.0, 0, 0.4), (0.5, 0.8, 0, 0.3), (0.8, 1.0, 0, 0.3)]
    for (zy1, zy2, zx1, zx2) in zones:
        persistence_map[int(zy1*h):int(zy2*h), int(zx1*w):int(zx2*w)] *= 1.5
    cx1, cx2 = int(w * 0.35), int(w * 0.65)
    cy1, cy2 = int(h * 0.35), int(h * 0.65)
    persistence_map[cy1:cy2, cx1:cx2] = 0
    _, final_mask = cv2.threshold(persistence_map, 0.3, 255, cv2.THRESH_BINARY)
    final_mask = final_mask.astype(np.uint8)
    res_multiplier = h / 540.0
    kw, kh = max(3, int(30 * res_multiplier)), max(3, int(15 * res_multiplier))
    kernel = cv2.getStructuringElement(cv2.MORPH_RECT, (kw, kh))
    grouped = cv2.morphologyEx(final_mask, cv2.MORPH_CLOSE, kernel)
    cnts, _ = cv2.findContours(grouped, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)
    print(f"DEBUG: Found {len(cnts)} raw contours.")
    min_area = max(10, 60 * (res_multiplier ** 2))
    detected = 0
    for c in cnts:
        area = cv2.contourArea(c)
        x, y, ww, hh = cv2.boundingRect(c)
        extent = area / float(ww * hh) if ww*hh > 0 else 0
        if extent < 0.70:
            print(f"  Skipped (JAGGY): Extent={extent:.2f} at {ww}x{hh}")
            continue
        if area < min_area:
            print(f"  Skipped (SMALL): Area={area:.1f} (min={min_area:.1f})")
            continue
        if area > (h*w*0.25):
            print(f"  Skipped (LARGE): Area={area:.1f}")
            continue
        print(f"  SUCCESS: Found {ww*2}x{hh*2} at ({x*2},{y*2}), Extent={extent:.2f}")
        detected += 1
    print(f"DEBUG: Total successful detections: {detected}")
if __name__ == "__main__":
    test_magic_wand_logic(r"examples\HD.mp4")
