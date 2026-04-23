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

def process_video(input_file):
    print(f"Processing: {input_file}")
    num_frames = 30
    interval_ms = 1000
    ffmpeg_path = r"C:\Fortnite_Video_Software\developer_tools\binaries\ffmpeg.exe"
    if not os.path.exists(ffmpeg_path): ffmpeg_path = "ffmpeg"
    frames = []
    original_first_frame = None
    for i in range(num_frames):
        time_s = i * (interval_ms / 1000.0)
        with tempfile.NamedTemporaryFile(suffix=".png", delete=False) as tf:
            tmp_name = tf.name
        try:
            cmd = [ffmpeg_path, '-ss', str(time_s), '-i', input_file, '-frames:v', '1', '-q:v', '2', '-y', tmp_name]
            subprocess.run(cmd, capture_output=True, timeout=15, creationflags=0x08000000)
            img = cv2.imread(tmp_name)
            if img is not None:
                if i == 0: original_first_frame = img.copy()
                small = cv2.resize(img, (0,0), fx=0.5, fy=0.5)
                frames.append(small)
        finally:
            if os.path.exists(tmp_name): os.unlink(tmp_name)
    if len(frames) < 3: return None
    h, w = frames[0].shape[:2]
    persistence_map = np.zeros((h, w), dtype=np.float32)
    active_count = 0
    for i in range(len(frames)-1):
        diff = cv2.absdiff(frames[i], frames[i+1])
        gray_diff = cv2.cvtColor(diff, cv2.COLOR_BGR2GRAY)
        _, m = cv2.threshold(gray_diff, 30, 255, cv2.THRESH_BINARY_INV)
        if (cv2.countNonZero(m) / (h*w)) > 0.92: continue
        persistence_map += (m / 255.0)
        active_count += 1
    if active_count == 0: return None
    persistence_map /= active_count
    zones = [
        (0, 0.5, 0.5, 1.0),
        (0, 0.3, 0.1, 0.5),
        (0.6, 1.0, 0.6, 1.0),
        (0.7, 1.0, 0, 0.4),
        (0.5, 0.8, 0, 0.3),
        (0.8, 1.0, 0, 0.3)
    ]
    for (y1, y2, x1, x2) in zones:
        persistence_map[int(y1*h):int(y2*h), int(x1*w):int(x2*w)] *= 1.5
    _, final_mask = cv2.threshold(persistence_map, 0.3, 255, cv2.THRESH_BINARY)
    final_mask = final_mask.astype(np.uint8)
    res_multiplier = h / 540.0
    kw = max(3, int(30 * res_multiplier))
    kh = max(3, int(15 * res_multiplier))
    kernel = cv2.getStructuringElement(cv2.MORPH_RECT, (kw, kh))
    grouped = cv2.morphologyEx(final_mask, cv2.MORPH_CLOSE, kernel)
    cnts, _ = cv2.findContours(grouped, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)
    orig_h, orig_w = original_first_frame.shape[:2]
    scale_x, scale_y = orig_w / float(w), orig_h / float(h)
    min_area = max(10, 60 * (res_multiplier ** 2))
    result_canvas = original_first_frame.copy()
    for c in cnts:
        area = cv2.contourArea(c)
        if area < min_area or area > (h*w*0.25): continue
        x, y, ww, hh = cv2.boundingRect(c)
        rx, ry, rw, rh = int(x*scale_x), int(y*scale_y), int(ww*scale_x), int(hh*scale_y)
        cv2.rectangle(result_canvas, (rx, ry), (rx+rw, ry+rh), (147, 20, 255), 4)
        cv2.putText(result_canvas, "STATIC HUD", (rx, ry-10), cv2.FONT_HERSHEY_SIMPLEX, 0.8, (147, 20, 255), 2)
    return cv2.resize(result_canvas, (960, 540))

def main():
    example_dir = r"C:\Fortnite_Video_Software\developer_tools\examples"
    videos = [f for f in os.listdir(example_dir) if f.lower().endswith('.mp4')]
    results = []
    for v in videos:
        res = process_video(os.path.join(example_dir, v))
        if res is not None: results.append(res)
    if results:
        final_pic = np.vstack(results)
        output_path = os.path.join(example_dir, "picture.jpg")
        cv2.imwrite(output_path, final_pic)
        print(f"Success! Verification image saved to: {output_path}")
if __name__ == "__main__":
    main()
