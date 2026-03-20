import cv2
import numpy as np
import os
import logging
import tempfile
import subprocess
import shutil
import sys
from PyQt5.QtCore import QRect, QObject, pyqtSignal, QThread
from config import CV_HEURISTICS

class HUDExtractor:
    def __init__(self, logger=None, params=None):
        self.logger = logger or logging.getLogger(__name__)
        self.params = params or {}

    def _extract_temporal_frames(self, input_file, total_ms, cancel_check):
        frames = []
        if not input_file or not os.path.exists(input_file): return []
        num_frames = 15
        interval_ms = 2000
        ffmpeg_exe = shutil.which("ffmpeg") or "ffmpeg"
        for i in range(num_frames):
            if cancel_check(): break
            time_s = (i * interval_ms) / 1000.0
            if total_ms > 0 and (time_s * 1000) > total_ms: break
            with tempfile.NamedTemporaryFile(suffix=".png", delete=False) as tf:
                tmp_name = tf.name
            try:
                cmd = [ffmpeg_exe, '-ss', str(time_s), '-i', input_file, '-frames:v', '1', '-q:v', '2', '-y', tmp_name]
                subprocess.run(cmd, capture_output=True, timeout=10, creationflags=0x08000000)
                img = cv2.imread(tmp_name)
                if img is not None:
                    if not hasattr(self, 'original_w'): self.original_h, self.original_w = img.shape[:2]
                    img = cv2.normalize(img, None, 0, 255, cv2.NORM_MINMAX)
                    frames.append(cv2.resize(img, (0,0), fx=0.5, fy=0.5))
            except: pass
            finally:
                if os.path.exists(tmp_name):
                    try: os.unlink(tmp_name)
                    except: pass
        return frames

    def extract_all(self, snapshot_path, cancel_check=None):
        input_file = self.params.get("input_file")
        total_ms = self.params.get("total_ms", 0)
        if not input_file: return []
        frames = self._extract_temporal_frames(input_file, total_ms, cancel_check or (lambda: False))
        if len(frames) < 3: return []
        h, w = frames[0].shape[:2]
        intersection_mask = np.full((h, w), 255, dtype=np.uint8)
        active_count = 0
        for i in range(len(frames)-1):
            if cancel_check and cancel_check(): break
            diff = cv2.absdiff(frames[i], frames[i+1])
            gray_diff = cv2.cvtColor(diff, cv2.COLOR_BGR2GRAY)
            _, m = cv2.threshold(gray_diff, 25, 255, cv2.THRESH_BINARY_INV)
            if (cv2.countNonZero(m) / (h*w)) < 0.85:
                intersection_mask = cv2.bitwise_and(intersection_mask, m)
                active_count += 1
        if active_count < 2: return []
        cx1, cx2 = int(w * 0.375), int(w * 0.625)
        intersection_mask[:, cx1:cx2] = 0
        res_multiplier = h / 540.0
        kw, kh = max(3, int(20 * res_multiplier)), max(3, int(10 * res_multiplier))
        kernel = cv2.getStructuringElement(cv2.MORPH_RECT, (kw, kh))
        grouped = cv2.morphologyEx(intersection_mask, cv2.MORPH_CLOSE, kernel)
        cnts, _ = cv2.findContours(grouped, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)
        raw_rois = []
        scale_x, scale_y = self.original_w / float(w), self.original_h / float(h)
        for c in cnts:
            area = cv2.contourArea(c)
            x, y, ww, hh = cv2.boundingRect(c)
            if (area / float(ww * hh) if ww*hh > 0 else 0) < 0.35: continue
            if area < (30 * res_multiplier**2) or area > (h*w*0.20): continue
            raw_rois.append(QRect(int(x*scale_x), int(y*scale_y), int(ww*scale_x), int(hh*scale_y)))
        return raw_rois

class MagicWand:
    def __init__(self, logger=None, params=None):
        self.extractor = HUDExtractor(logger, params)

    def detect_static_hud_regions(self, snapshot_path: str, cancel_check=None):
        return self.extractor.extract_all(snapshot_path, cancel_check)

class MagicWandWorker(QObject):
    finished = pyqtSignal(list)
    error = pyqtSignal(str)

    def __init__(self, magic_wand_instance, snapshot_path, params):
        super().__init__()
        self.magic_wand, self.snapshot_path = magic_wand_instance, snapshot_path
        self._is_cancelled = False

    def cancel(self): self._is_cancelled = True

    def run(self):
        try:
            regions = self.magic_wand.detect_static_hud_regions(self.snapshot_path, lambda: self._is_cancelled)
            if not self._is_cancelled: self.finished.emit(regions or [])
        except Exception as e:
            if not self._is_cancelled: self.error.emit(str(e))
