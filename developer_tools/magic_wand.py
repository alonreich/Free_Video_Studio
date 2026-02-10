import cv2
import numpy as np
import os
import logging
from PyQt5.QtCore import QRect, QObject, pyqtSignal, QThread

class HUDExtractor:
    def __init__(self, logger=None, params=None):
        self.logger = logger or logging.getLogger(__name__)
        self.params = params or {}
        self.anchors = {}
        self._load_anchors()

    def _load_anchors(self):
        script_dir = os.path.dirname(os.path.abspath(__file__))
        anchor_dir = os.path.join(script_dir, 'anchors')
        if not os.path.isdir(anchor_dir):
            return
        anchor_files = {
            'loot_start': 'ref_keybind_1.png',
            'loot_end': 'ref_keybind_5.png',
            'map_edge': 'ref_minimap_border.png',
            'hp_icon': 'ref_hp_icon.png'
        }
        for key, filename in anchor_files.items():
            path = os.path.join(anchor_dir, filename)
            if os.path.exists(path):
                try:
                    data = np.fromfile(path, dtype=np.uint8)
                    img = cv2.imdecode(data, cv2.IMREAD_GRAYSCALE)
                    self.anchors[key] = img if not self._looks_like_placeholder_anchor(img) else None
                except:
                    self.anchors[key] = None

    def _looks_like_placeholder_anchor(self, img_gray):
        if img_gray is None or img_gray.size == 0:
            return True
        return len(np.unique(img_gray)) <= 8 or float(np.std(img_gray)) < 10.0

    def _preprocess_for_matching(self, frame_gray):
        clahe = cv2.createCLAHE(clipLimit=2.0, tileGridSize=(8, 8))
        return clahe.apply(frame_gray)

    def _get_res_scale(self, frame_h):
        """Calculates scale factor relative to standard 1080p reference."""
        return float(frame_h) / 1080.0

    def _find_anchor_multiscale(self, frame_gray, anchor_key, threshold, search_rect=None):
        anchor_img = self.anchors.get(anchor_key)
        if anchor_img is None:
            return None
        img_h, img_w = frame_gray.shape[:2]
        res_scale = self._get_res_scale(img_h)
        x0, y0, rw, rh = 0, 0, img_w, img_h
        if search_rect is not None:
            x0, y0, rw, rh = search_rect
            x0, y0 = max(0, min(int(x0), img_w - 1)), max(0, min(int(y0), img_h - 1))
            rw, rh = max(1, min(int(rw), img_w - x0)), max(1, min(int(rh), img_h - y0))
        roi = frame_gray[y0:y0 + rh, x0:x0 + rw]
        roi = self._preprocess_for_matching(roi)
        anchor_base = self._preprocess_for_matching(anchor_img)
        best = None
        scales = [s * res_scale for s in (0.4, 0.5, 0.6, 0.75, 0.85, 1.0, 1.15, 1.3, 1.5)]
        for s in scales:
            tw, th = int(anchor_base.shape[1] * s), int(anchor_base.shape[0] * s)
            if tw < 6 or th < 6 or tw >= roi.shape[1] or th >= roi.shape[0]:
                continue
            templ = cv2.resize(anchor_base, (tw, th), interpolation=cv2.INTER_AREA)
            res = cv2.matchTemplate(roi, templ, cv2.TM_CCOEFF_NORMED)
            _, max_val, _, max_loc = cv2.minMaxLoc(res)
            if best is None or max_val > best[2]:
                best = (x0 + max_loc[0], y0 + max_loc[1], float(max_val), tw, th, s)
        if best and best[2] >= threshold:
            return best
        return None

    def _tighten_rect(self, frame_gray, rect, padding=8):
        """[FIX #29] High-precision shrink-wrap with safety padding."""
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
        except:
            return rect

    def _rect_from_norm(self, frame_gray, nx, ny, nw, nh):
        """[FIX #14] Resolution-aware normalization logic."""
        h, w = frame_gray.shape[:2]
        x, y = int(round(nx * w)), int(round(ny * h))
        ww, hh = int(round(nw * w)), int(round(nh * h))
        x, y = max(0, min(x, w - 10)), max(0, min(y, h - 10))
        ww, hh = max(10, min(ww, w - x)), max(10, min(hh, h - y))
        return QRect(x, y, ww, hh)

    def _heuristic_map_rect(self, frame_gray):
        h, w = frame_gray.shape[:2]
        aspect = w / h
        nx = 0.85 if aspect > 2.0 else 0.78
        raw = self._rect_from_norm(frame_gray, nx, 0.02, 0.20, 0.25)
        return self._tighten_rect(frame_gray, raw, padding=8)

    def _heuristic_hp_rect(self, frame_gray):
        raw = self._rect_from_norm(frame_gray, 0.02, 0.85, 0.25, 0.10)
        return self._tighten_rect(frame_gray, raw, padding=8)

    def _heuristic_loot_rect(self, frame_gray):
        h, w = frame_gray.shape[:2]
        aspect = w / h
        nx = 0.85 if aspect > 2.0 else 0.78
        raw = self._rect_from_norm(frame_gray, nx, 0.88, 0.12, 0.08)
        return self._tighten_rect(frame_gray, raw, padding=8)

    def _heuristic_teammates_rect(self, frame_gray):
        raw = self._rect_from_norm(frame_gray, 0.01, 0.20, 0.15, 0.30)
        return self._tighten_rect(frame_gray, raw, padding=8)

    def _heuristic_spectating_rect(self, frame_gray):
        raw = self._rect_from_norm(frame_gray, 0.02, 0.70, 0.10, 0.05)
        return self._tighten_rect(frame_gray, raw, padding=8)

    def _clamp_rect(self, x, y, w, h, frame_w, frame_h):
        x, y = max(0, min(int(x), frame_w - 10)), max(0, min(int(y), frame_h - 10))
        w, h = max(10, min(int(w), frame_w - x)), max(10, min(int(h), frame_h - y))
        return QRect(x, y, w, h)

    def _detect_minimap_by_circle(self, frame_gray):
        """[FIX #28] Hybrid circle/corner detection for stylized minimaps."""
        h, w = frame_gray.shape[:2]
        x0, y0 = int(0.70 * w), 0
        rw, rh = int(0.30 * w), int(0.40 * h)
        roi = frame_gray[y0:y0 + rh, x0:x0 + rw]
        if roi.size == 0: return None
        blur = cv2.GaussianBlur(roi, (7, 7), 1.5)
        circles = cv2.HoughCircles(blur, cv2.HOUGH_GRADIENT, dp=1.2, minDist=int(40 * self._get_res_scale(h)),
                                   param1=90, param2=22, minRadius=max(18, int(0.05 * h)), maxRadius=max(28, int(0.18 * h)))
        if circles is not None:
            circles = np.round(circles[0]).astype(int)
            best = max(circles, key=lambda c: (c[2], c[0]))
            cx, cy, r = int(best[0]) + x0, int(best[1]) + y0, int(best[2])
            return self._clamp_rect(cx - 1.2 * r, cy - 1.1 * r, 2.5 * r, 3.0 * r, w, h)
        edges = cv2.Canny(blur, 50, 150)
        cnts, _ = cv2.findContours(edges, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)
        if cnts:
            c = max(cnts, key=cv2.contourArea)
            if cv2.contourArea(c) > (roi.size * 0.05):
                x, y, ww, hh = cv2.boundingRect(c)
                return self._clamp_rect(x0 + x - 10, y0 + y - 10, ww + 20, hh + 20, w, h)
        return None

    def _detect_hp_by_color(self, frame_color):
        h, w = frame_color.shape[:2]
        x0, y0 = 0, int(0.60 * h)
        rw, rh = int(0.55 * w), int(0.40 * h)
        roi = frame_color[y0:y0 + rh, x0:x0 + rw]
        if roi.size == 0: return None
        hsv = cv2.cvtColor(roi, cv2.COLOR_BGR2HSV)
        green = cv2.inRange(hsv, (30, 40, 40), (100, 255, 255))
        blue = cv2.inRange(hsv, (75, 30, 30), (145, 255, 255))
        mask = cv2.bitwise_or(green, blue)
        mask = cv2.morphologyEx(mask, cv2.MORPH_CLOSE, np.ones((3, 9), np.uint8), iterations=2)
        cnts, _ = cv2.findContours(mask, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)
        rects = []
        min_area = max(80, int(0.0002 * w * h))
        for c in cnts:
            x, y, ww, hh = cv2.boundingRect(c)
            if ww * hh < min_area or ww < 1.5 * hh: continue
            rects.append((x, y, ww, hh))
        if not rects: return None
        x1, y1 = min(r[0] for r in rects), min(r[1] for r in rects)
        x2, y2 = max(r[0] + r[2] for r in rects), max(r[1] + r[3] for r in rects)
        px, py = int(0.02 * rw), int(0.05 * rh)
        return self._clamp_rect(x0 + x1 - px, y0 + y1 - py, (x2 - x1) + 2 * px, (y2 - y1) + 2 * py, w, h)

    def _detect_loot_by_boxes(self, frame_gray):
        h, w = frame_gray.shape[:2]
        x0, y0 = int(0.40 * w), int(0.60 * h)
        rw, rh = int(0.60 * w), int(0.40 * h)
        roi = frame_gray[y0:y0 + rh, x0:x0 + rw]
        if roi.size == 0: return None
        edges = cv2.dilate(cv2.Canny(cv2.GaussianBlur(roi, (5, 5), 0), 70, 180), np.ones((3, 3), np.uint8))
        cnts, _ = cv2.findContours(edges, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)
        slots = []
        for c in cnts:
            x, y, ww, hh = cv2.boundingRect(c)
            if ww < 16 or hh < 16 or ww > int(0.25 * rw) or hh > int(0.40 * rh): continue
            if 0.70 < ww / float(hh) < 1.40: slots.append((x, y, ww, hh))
        if len(slots) < 2: return None
        slots.sort(key=lambda r: r[0])
        best_g = []
        for i in range(len(slots)):
            g = [slots[i]]
            for j in range(i + 1, len(slots)):
                if abs((slots[j][1] + slots[j][3] // 2) - (slots[i][1] + slots[i][3] // 2)) <= int(0.15 * rh): g.append(slots[j])
            if len(g) > len(best_g): best_g = g
        use = best_g if len(best_g) >= 2 else slots
        x1, y1 = min(r[0] for r in use), min(r[1] for r in use)
        x2, y2 = max(r[0] + r[2] for r in use), max(r[1] + r[3] for r in use)
        px, py = int(0.03 * rw), int(0.05 * rh)
        return self._clamp_rect(x0 + x1 - px, y0 + y1 - py, (x2 - x1) + 2 * px, (y2 - y1) + 2 * py, w, h)

    def _extract_loot_module(self, frame_gray, frame_color):
        if self.anchors.get('loot_start') is None or self.anchors.get('loot_end') is None: return None
        h, w = frame_gray.shape[:2]
        search = (int(0.70 * w), int(0.80 * h), int(0.30 * w), int(0.20 * h))
        p1 = self._find_anchor_multiscale(frame_gray, 'loot_start', 0.52, search)
        p5 = self._find_anchor_multiscale(frame_gray, 'loot_end', 0.52, search)
        if p1 is None and p5 is None: return None
        local_s = p1[5] if p1 else p5[5]
        if p1 and p5:
            x1, y1 = p1[0], min(p1[1], p5[1]) - int(60 * local_s)
            width, height = (p5[0] - p1[0]) + p5[3] + int(10 * local_s), int(100 * local_s)
        elif p1:
            x1, y1 = p1[0] - int(5 * local_s), p1[1] - int(60 * local_s)
            width, height = int(280 * local_s), int(100 * local_s)
        else:
            x1, y1 = p5[0] - int(260 * local_s), p5[1] - int(60 * local_s)
            width, height = int(280 * local_s), int(100 * local_s)
        raw_rect = self._clamp_rect(x1, y1, width, height, w, h)
        return self._tighten_rect(frame_gray, raw_rect, padding=8)

    def _extract_hp_module(self, frame_gray, frame_color):
        hp_anchor = self.anchors.get('hp_icon')
        if hp_anchor is None: return None
        h, w = frame_gray.shape[:2]
        search = (0, int(0.75 * h), int(0.4 * w), int(0.25 * h))
        found = self._find_anchor_multiscale(frame_gray, 'hp_icon', 0.50, search)
        if not found: return None
        ax, ay, _, tw, th, local_s = found
        x1, y1 = ax + (tw // 2), ay - int(20 * local_s)
        detected_w = int(tw * 8)
        scan_y = ay + (th // 2)
        if scan_y < h:
            row = frame_gray[scan_y, x1:min(x1 + int(tw * 10), w)]
            brights = np.where(row > 120)[0]
            if len(brights) > 0: detected_w = int(brights[-1]) + int(30 * local_s)
        width, height = max(int(tw * 6), min(detected_w, int(tw * 10))), int(90 * local_s)
        raw_rect = self._clamp_rect(x1, y1, width, height, w, h)
        return self._tighten_rect(frame_gray, raw_rect, padding=8)

    def _extract_hp_module_boss(self, frame_gray, frame_color):
        """[NEW] Boss HP detection heuristic."""
        h, w = frame_gray.shape[:2]
        raw = self._rect_from_norm(frame_gray, 0.35, 0.05, 0.30, 0.08)
        return self._tighten_rect(frame_gray, raw, padding=8)

    def _extract_map_stats_module(self, frame_gray, frame_color):
        if self.anchors.get('map_edge') is None: return None
        h, w = frame_gray.shape[:2]
        search = (int(0.65 * w), 0, int(0.35 * w), int(0.4 * h))
        found = self._find_anchor_multiscale(frame_gray, 'map_edge', 0.50, search)
        if not found: return None
        local_s = found[5]
        x1, y1 = found[0] - int(15 * local_s), found[1] - int(10 * local_s)
        raw_rect = self._clamp_rect(x1, y1, int(300 * local_s), int(380 * local_s), w, h)
        return self._tighten_rect(frame_gray, raw_rect, padding=8)

    def _rect_iou(self, a, b):
        ax1, ay1, ax2, ay2 = a.x(), a.y(), a.x() + a.width(), a.y() + a.height()
        bx1, by1, bx2, by2 = b.x(), b.y(), b.x() + b.width(), b.y() + b.height()
        ix1, iy1, ix2, iy2 = max(ax1, bx1), max(ay1, by1), min(ax2, bx2), min(ay2, by2)
        inter = max(0, ix2 - ix1) * max(0, iy2 - iy1)
        union = a.width() * a.height() + b.width() * b.height() - inter
        return 0.0 if union <= 0 else inter / union

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
            rois.append(self._extract_loot_module(frame_gray, frame_color))
            check()
            rois.append(self._extract_hp_module(frame_gray, frame_color))
            check()
            rois.append(self._extract_map_stats_module(frame_gray, frame_color))
            check()
            circle_map = self._detect_minimap_by_circle(frame_gray)
            if circle_map: rois.append(circle_map)
            check()
            color_hp = self._detect_hp_by_color(frame_color)
            if color_hp: rois.append(color_hp)
            check()
            box_loot = self._detect_loot_by_boxes(frame_gray)
            if box_loot: rois.append(box_loot)
            check()
        except InterruptedError:
            return []
        except: pass
        rois = [r for r in rois if r is not None]
        if not rois:
            rois.extend([self._heuristic_loot_rect(frame_gray), self._heuristic_map_rect(frame_gray),
                         self._heuristic_hp_rect(frame_gray), self._heuristic_teammates_rect(frame_gray),
                         self._heuristic_spectating_rect(frame_gray)])
        out = []
        for r in rois:
            if r and all(self._rect_iou(r, e) < 0.40 for e in out): 
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
            if not regions: self.error.emit("No HUD elements detected.")
            else: self.finished.emit(regions)
        except Exception as e:
            if not cancel_check():
                self.error.emit(str(e))

    