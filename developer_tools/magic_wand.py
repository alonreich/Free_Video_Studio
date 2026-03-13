import cv2
import numpy as np
import os
import logging
from PyQt5.QtCore import QRect, QObject, pyqtSignal, QThread
from config import CV_HEURISTICS

class HUDExtractor:
    def __init__(self, logger=None, params=None):
        self.logger = logger or logging.getLogger(__name__)
        self.params = params or {}

    def _get_res_scale(self, frame_h):
        """Calculates scale factor relative to standard 1080p reference."""
        return float(frame_h) / 1080.0

    def _clamp_rect(self, x, y, w, h, frame_w, frame_h):
        x, y = max(0, min(int(x), frame_w - 10)), max(0, min(int(y), frame_h - 10))
        w, h = max(10, min(int(w), frame_w - x)), max(10, min(int(h), frame_h - y))
        return QRect(x, y, w, h)

    def _tighten_rect(self, frame_gray, rect, padding=CV_HEURISTICS.SHRINK_WRAP_PADDING):
        """High-precision shrink-wrap with safety padding using Otsu thresholding."""
        try:
            x, y, w, h = rect.x(), rect.y(), rect.width(), rect.height()
            img_h, img_w = frame_gray.shape[:2]
            x1, y1 = max(0, x), max(0, y)
            x2, y2 = min(img_w, x + w), min(img_h, y + h)
            roi = frame_gray[y1:y2, x1:x2]
            if roi.size == 0: return rect
            blur = cv2.GaussianBlur(roi, (3,3), 0)
            _, thresh = cv2.threshold(blur, 0, 255, cv2.THRESH_BINARY + cv2.THRESH_OTSU)
            coords = np.column_stack(np.where(thresh > 0))
            if coords.size == 0: return rect
            y_min, x_min = coords.min(axis=0)
            y_max, x_max = coords.max(axis=0)
            new_x = x1 + x_min - padding
            new_y = y1 + y_min - padding
            new_w = (x_max - x_min) + (padding * 2)
            new_h = (y_max - y_min) + (padding * 2)
            return self._clamp_rect(new_x, new_y, new_w, new_h, img_w, img_h)
        except Exception as e:
            self.logger.error(f"Tighten rect failed: {e}")
            return rect

    def _rect_from_norm(self, frame_gray, nx, ny, nw, nh):
        h, w = frame_gray.shape[:2]
        x, y = int(round(nx * w)), int(round(ny * h))
        ww, hh = int(round(nw * w)), int(round(nh * h))
        return self._clamp_rect(x, y, ww, hh, w, h)

    def _detect_map_stats_module(self, frame_gray, frame_color):
        """Top-tier detection for Minimap using Otsu thresholding and adaptive contours."""
        h, w = frame_gray.shape[:2]
        x0 = int(0.65 * w)
        y0 = 0
        rw = int(0.35 * w)
        rh = int(0.40 * h)
        roi = frame_gray[y0:y0+rh, x0:x0+rw]
        if roi.size == 0: return None
        blur = cv2.GaussianBlur(roi, (5, 5), 0)
        thresh = cv2.adaptiveThreshold(blur, 255, cv2.ADAPTIVE_THRESH_GAUSSIAN_C, cv2.THRESH_BINARY_INV, 11, 2)
        kernel = cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (15, 15))
        closed = cv2.morphologyEx(thresh, cv2.MORPH_CLOSE, kernel, iterations=2)
        cnts, _ = cv2.findContours(closed, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)
        if not cnts:
            return None
        c = max(cnts, key=cv2.contourArea)
        if cv2.contourArea(c) > (roi.size * 0.02):
            x, y, ww, hh = cv2.boundingRect(c)
            px = int(0.05 * rw)
            py = int(0.15 * rh)
            raw = self._clamp_rect(x0 + x - px, y0 + y - py, ww + 2*px, hh + 2*py + int(100 * self._get_res_scale(h)), w, h)
            return self._tighten_rect(frame_gray, raw)
        return None

    def _detect_hp_module(self, frame_gray, frame_color):
        """Top-tier detection for HP using HSV color masking combined with Otsu thresholding on V channel."""
        h, w = frame_color.shape[:2]
        x0 = 0
        y0 = int(0.70 * h)
        rw = int(0.50 * w)
        rh = int(0.30 * h)
        roi = frame_color[y0:y0+rh, x0:x0+rw]
        if roi.size == 0: return None
        hsv = cv2.cvtColor(roi, cv2.COLOR_BGR2HSV)
        green = cv2.inRange(hsv, (30, 40, 40), (90, 255, 255))
        blue = cv2.inRange(hsv, (90, 40, 40), (145, 255, 255))
        white = cv2.inRange(hsv, (0, 0, 200), (180, 30, 255))
        mask = cv2.bitwise_or(green, blue)
        mask = cv2.bitwise_or(mask, white)
        v_channel = hsv[:,:,2]
        _, otsu_thresh = cv2.threshold(v_channel, 0, 255, cv2.THRESH_BINARY + cv2.THRESH_OTSU)
        combined = cv2.bitwise_and(mask, otsu_thresh)
        kernel = cv2.getStructuringElement(cv2.MORPH_RECT, (25, 5))
        closed = cv2.morphologyEx(combined, cv2.MORPH_CLOSE, kernel, iterations=3)
        cnts, _ = cv2.findContours(closed, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)
        rects = []
        for c in cnts:
            x, y, ww, hh = cv2.boundingRect(c)
            if ww > 3 * hh and ww > int(0.10 * w):
                rects.append((x, y, ww, hh))
        if not rects:
            return None
        x1 = min(r[0] for r in rects)
        y1 = min(r[1] for r in rects)
        x2 = max(r[0] + r[2] for r in rects)
        y2 = max(r[1] + r[3] for r in rects)
        px, py = int(0.02 * rw), int(0.10 * rh)
        raw = self._clamp_rect(x0 + x1 - px, y0 + y1 - py, (x2 - x1) + 2*px, (y2 - y1) + 2*py, w, h)
        return self._tighten_rect(frame_gray, raw)

    def _detect_loot_module(self, frame_gray, frame_color):
        """Top-tier detection for Loot boxes using morphological edge detection and contours."""
        h, w = frame_gray.shape[:2]
        x0 = int(0.50 * w)
        y0 = int(0.70 * h)
        rw = int(0.50 * w)
        rh = int(0.30 * h)
        roi = frame_gray[y0:y0+rh, x0:x0+rw]
        if roi.size == 0: return None
        clahe = cv2.createCLAHE(clipLimit=2.0, tileGridSize=(8, 8))
        enhanced = clahe.apply(roi)
        _, thresh = cv2.threshold(enhanced, 0, 255, cv2.THRESH_BINARY + cv2.THRESH_OTSU)
        edges = cv2.Canny(thresh, 50, 150)
        kernel = cv2.getStructuringElement(cv2.MORPH_RECT, (15, 15))
        closed = cv2.morphologyEx(edges, cv2.MORPH_CLOSE, kernel, iterations=2)
        cnts, _ = cv2.findContours(closed, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)
        slots = []
        for c in cnts:
            x, y, ww, hh = cv2.boundingRect(c)
            if ww > 20 and hh > 20 and 0.5 < ww/float(hh) < 2.0:
                slots.append((x, y, ww, hh))
        if len(slots) < 2:
            return None
        slots.sort(key=lambda r: r[0])
        best_group = []
        for i in range(len(slots)):
            group = [slots[i]]
            for j in range(i+1, len(slots)):
                if abs((slots[j][1] + slots[j][3]//2) - (slots[i][1] + slots[i][3]//2)) < int(0.10 * rh):
                    group.append(slots[j])
            if len(group) > len(best_group):
                best_group = group
        if len(best_group) < 2:
            return None
        x1 = min(r[0] for r in best_group)
        y1 = min(r[1] for r in best_group)
        x2 = max(r[0] + r[2] for r in best_group)
        y2 = max(r[1] + r[3] for r in best_group)
        px, py = int(0.02 * rw), int(0.05 * rh)
        raw = self._clamp_rect(x0 + x1 - px, y0 + y1 - py, (x2 - x1) + 2*px, (y2 - y1) + 2*py, w, h)
        return self._tighten_rect(frame_gray, raw)

    def _heuristic_hp_rect(self, frame_gray):
        raw = self._rect_from_norm(frame_gray, 0.01, 0.80, 0.35, 0.18)
        return self._tighten_rect(frame_gray, raw)
        
    def _heuristic_map_rect(self, frame_gray):
        raw = self._rect_from_norm(frame_gray, 0.75, 0.01, 0.24, 0.30)
        return self._tighten_rect(frame_gray, raw)
        
    def _heuristic_loot_rect(self, frame_gray):
        raw = self._rect_from_norm(frame_gray, 0.70, 0.75, 0.28, 0.22)
        return self._tighten_rect(frame_gray, raw)

    def _heuristic_teammates_rect(self, frame_gray):
        raw = self._rect_from_norm(frame_gray, 0.0, 0.15, 0.20, 0.45)
        return self._tighten_rect(frame_gray, raw)

    def _heuristic_spectating_rect(self, frame_gray):
        raw = self._rect_from_norm(frame_gray, 0.01, 0.65, 0.15, 0.10)
        return self._tighten_rect(frame_gray, raw)

    def _rect_iou(self, a, b):
        ax1, ay1, ax2, ay2 = a.x(), a.y(), a.x() + a.width(), a.y() + a.height()
        bx1, by1, bx2, by2 = b.x(), b.y(), b.x() + b.width(), b.y() + b.height()
        ix1, iy1, ix2, iy2 = max(ax1, bx1), max(ay1, by1), min(ax2, bx2), min(ay2, by2)
        inter = max(0, ix2 - ix1) * max(0, iy2 - iy1)
        union = a.width() * a.height() + b.width() * b.height() - inter
        return 0.0 if union <= 0 else inter / union

    def _detect_from_config(self, frame_gray):
        try:
            from config_manager import get_config_manager
            from coordinate_math import inverse_transform_from_content_area_int
            script_dir = os.path.dirname(os.path.abspath(__file__))
            conf_path = os.path.join(script_dir, '..', 'processing', 'crops_coordinations.conf')
            if not os.path.exists(conf_path): return []
            config = get_config_manager(conf_path).load_config()
            crops = config.get("crops_1080p", {})
            res_str = self.params.get("resolution", "1920x1080")
            h, w = frame_gray.shape[:2]
            trained_rois = []
            for role, rect in crops.items():
                if not rect or len(rect) < 4: continue
                source_rect = inverse_transform_from_content_area_int((rect[2], rect[3], rect[0], rect[1]), res_str)
                sx, sy, sw, sh = source_rect
                raw_qrect = self._clamp_rect(sx, sy, sw, sh, w, h)
                tightened = self._tighten_rect(frame_gray, raw_qrect)
                trained_rois.append(tightened)
            return trained_rois
        except Exception as e:
            self.logger.error(f"Magic Wand training failed: {e}")
            return []

    def extract_all(self, snapshot_path, cancel_check=None):
        if not os.path.exists(snapshot_path): return []
        frame_color = cv2.imread(snapshot_path)
        if frame_color is None: return []
        h, w = frame_color.shape[:2]
        frame_gray = cv2.cvtColor(frame_color, cv2.COLOR_BGR2GRAY)
        res_scale = self._get_res_scale(h)
        self.logger.info(f"Magic Wand scale: {res_scale}")
        rois = []

        def check():
            if cancel_check and cancel_check():
                raise InterruptedError("Magic Wand canceled")
        try:
            check()
            rois.extend(self._detect_from_config(frame_gray))
            check()
            rois.append(self._detect_loot_module(frame_gray, frame_color))
            check()
            rois.append(self._detect_hp_module(frame_gray, frame_color))
            check()
            rois.append(self._detect_map_stats_module(frame_gray, frame_color))
            check()
        except InterruptedError:
            return []
        except Exception as e:
            self.logger.error(f"Error during extraction: {e}")
            pass
        rois = [r for r in rois if r is not None]
        if not rois:
            rois.extend([self._heuristic_loot_rect(frame_gray), self._heuristic_map_rect(frame_gray),
                         self._heuristic_hp_rect(frame_gray), self._heuristic_teammates_rect(frame_gray),
                         self._heuristic_spectating_rect(frame_gray)])
        out = []
        for r in rois:
            if r and all(self._rect_iou(r, e) < 0.35 for e in out): 
                out.append(r)
        return out

class MagicWand:
    def __init__(self, logger=None, params=None):
        self.logger = logger or logging.getLogger(__name__)
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

    def cancel(self):
        self._is_cancelled = True

    def run(self):
        def cancel_check(): 
            return self._is_cancelled or QThread.currentThread().isInterruptionRequested()
        try:
            if self._is_cancelled: return
            regions = self.magic_wand.detect_static_hud_regions(self.snapshot_path, cancel_check)
            if cancel_check(): return
            self.finished.emit(regions or [])
        except Exception as e:
            if not cancel_check():
                self.error.emit(str(e))
