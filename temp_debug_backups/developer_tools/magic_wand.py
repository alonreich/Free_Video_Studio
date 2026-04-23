import sys
import os
sys.dont_write_bytecode = True
os.environ['PYTHONDONTWRITEBYTECODE'] = '1'
os.environ['PYTHONPYCACHEPREFIX'] = os.path.join(os.path.expanduser('~'), '.null_cache_dir')

import cv2
import numpy as np
import os
import logging
import tempfile
import subprocess
import shutil
from PyQt5.QtCore import QRect, QObject, pyqtSignal

class HUDExtractor:
    def __init__(self, logger=None, params=None):
        self.logger = logger or logging.getLogger(__name__)
        self.params = params or {}
        self.original_w, self.original_h = 0, 0
        self.scale_w, self.scale_h = 960, 540

    def _extract_frames_at(self, input_file, start_ms, num_frames, interval_ms, cancel_check):
        frames = []
        cap = cv2.VideoCapture(input_file)
        if not cap.isOpened():
            return frames
        try:
            fps = cap.get(cv2.CAP_PROP_FPS) or 30.0
            start_frame = int((start_ms / 1000.0) * fps)
            cap.set(cv2.CAP_PROP_POS_FRAMES, start_frame)
            for i in range(num_frames):
                if cancel_check(): break
                ok, img = cap.read()
                if not ok or img is None: break
                if self.original_w == 0 or self.original_h == 0:
                    self.original_h, self.original_w = img.shape[:2]
                    target_h = 540
                    self.scale_h = target_h
                    self.scale_w = int(round(target_h * (self.original_w / float(max(1, self.original_h)))))
                    if self.scale_w % 2 != 0: self.scale_w += 1
                frames.append(cv2.resize(img, (self.scale_w, self.scale_h)))
                if interval_ms > 0:
                    skip = int((interval_ms / 1000.0) * fps)
                    if skip > 1:
                        curr = cap.get(cv2.CAP_PROP_POS_FRAMES)
                        cap.set(cv2.CAP_PROP_POS_FRAMES, curr + skip - 1)
        finally:
            cap.release()
        return frames

    def _estimate_video_duration_ms(self, input_file):
        try:
            cap = cv2.VideoCapture(input_file)
            if not cap.isOpened():
                return 0
            fps = float(cap.get(cv2.CAP_PROP_FPS) or 0.0)
            frame_count = float(cap.get(cv2.CAP_PROP_FRAME_COUNT) or 0.0)
            if fps > 0 and frame_count > 0:
                return int((frame_count / fps) * 1000)
            return 0
        except Exception:
            return 0
        finally:
            try:
                cap.release()
            except Exception:
                pass

    def _extract_uniform_frames(self, input_file, target_frames, cancel_check):
        """Uniform temporal sampling using OpenCV (fast and short-video safe)."""
        frames = []
        cap = cv2.VideoCapture(input_file)
        if not cap.isOpened():
            self.logger.error("OpenCV failed to open video for uniform sampling.")
            return frames
        try:
            frame_count = int(cap.get(cv2.CAP_PROP_FRAME_COUNT) or 0)
            if frame_count <= 0:
                self.logger.warning("Uniform sampler: unknown frame_count, falling back to sequential reads.")
                idx = 0
                while idx < target_frames and not cancel_check():
                    ok, img = cap.read()
                    if not ok or img is None:
                        break
                    if self.original_w == 0 or self.original_h == 0:
                        self.original_h, self.original_w = img.shape[:2]
                        target_h = 540
                        self.scale_h = target_h
                        self.scale_w = int(round(target_h * (self.original_w / float(max(1, self.original_h)))))
                        if self.scale_w % 2 != 0: self.scale_w += 1
                    frames.append(cv2.resize(img, (self.scale_w, self.scale_h)))
                    idx += 1
                return frames
            sample_count = max(1, min(int(target_frames), frame_count))
            indices = np.linspace(0, frame_count - 1, num=sample_count, dtype=np.int32)
            last_idx = -1
            for idx in indices:
                if cancel_check():
                    break
                idx = int(idx)
                if idx == last_idx:
                    continue
                last_idx = idx
                cap.set(cv2.CAP_PROP_POS_FRAMES, idx)
                ok, img = cap.read()
                if not ok or img is None:
                    continue
                if self.original_w == 0 or self.original_h == 0:
                    self.original_h, self.original_w = img.shape[:2]
                    target_h = 540
                    self.scale_h = target_h
                    self.scale_w = int(round(target_h * (self.original_w / float(max(1, self.original_h)))))
                    if self.scale_w % 2 != 0: self.scale_w += 1
                frames.append(cv2.resize(img, (self.scale_w, self.scale_h)))
        except Exception as e:
            self.logger.error(f"Uniform frame extraction failed: {e}")
        finally:
            cap.release()
        return frames

    def _extract_temporal_frames(self, input_file, total_ms, cancel_check):
        """Backward-compatible helper for legacy debug scripts."""
        target = 60
        if total_ms and total_ms <= 3500:
            target = 45
        elif total_ms and total_ms <= 15000:
            target = 50
        return self._extract_uniform_frames(input_file, target, cancel_check)

    def _expand_and_clamp_scaled_rect(self, x, y, w, h, pad):
        x0 = max(0, x - pad)
        y0 = max(0, y - pad)
        x1 = min(self.scale_w, x + w + pad)
        y1 = min(self.scale_h, y + h + pad)
        return int(x0), int(y0), int(max(1, x1 - x0)), int(max(1, y1 - y0))

    def _scaled_to_original_rect(self, x, y, w, h):
        sx = self.original_w / float(self.scale_w)
        sy = self.original_h / float(self.scale_h)
        rx = int(round(x * sx))
        ry = int(round(y * sy))
        rw = int(round(w * sx))
        rh = int(round(h * sy))
        rx = max(0, min(rx, self.original_w - 1))
        ry = max(0, min(ry, self.original_h - 1))
        rw = max(1, min(rw, self.original_w - rx))
        rh = max(1, min(rh, self.original_h - ry))
        return QRect(rx, ry, rw, rh)

    def _rect_iou(self, a: QRect, b: QRect):
        ix = max(a.x(), b.x())
        iy = max(a.y(), b.y())
        ax2, ay2 = a.x() + a.width(), a.y() + a.height()
        bx2, by2 = b.x() + b.width(), b.y() + b.height()
        ix2, iy2 = min(ax2, bx2), min(ay2, by2)
        iw, ih = max(0, ix2 - ix), max(0, iy2 - iy)
        inter = iw * ih
        if inter <= 0:
            return 0.0
        union = (a.width() * a.height()) + (b.width() * b.height()) - inter
        return float(inter) / float(max(1, union))

    def _nms_candidates(self, scored_rects, iou_threshold=0.45, max_keep=2):
        """scored_rects: List[(score, QRect)] -> List[(score, QRect)]"""
        kept = []
        for score, rect in sorted(scored_rects, key=lambda t: t[0], reverse=True):
            if all(self._rect_iou(rect, existing_rect) < iou_threshold for _, existing_rect in kept):
                kept.append((score, rect))
                if len(kept) >= max_keep:
                    break
        return kept

    def _dedupe_rects_by_iou(self, rects, iou_threshold=0.5):
        if not rects:
            return []
        kept = []
        for r in rects:
            if all(self._rect_iou(r, k) < iou_threshold for k in kept):
                kept.append(r)
        return kept

    def _compute_temporal_stability_mask(self, frames):
        """Return mask where brighter means more stable over time."""
        if not frames or len(frames) < 3:
            return np.full((self.scale_h, self.scale_w), 255, dtype=np.uint8)
        arr = np.stack(frames).astype(np.float32)
        std_map = np.std(arr, axis=0).mean(axis=2)
        std_norm = cv2.normalize(std_map, None, 0, 255, cv2.NORM_MINMAX).astype(np.uint8)
        stability = 255 - std_norm
        _, st = cv2.threshold(stability, 165, 255, cv2.THRESH_BINARY)
        st = cv2.morphologyEx(st, cv2.MORPH_OPEN, cv2.getStructuringElement(cv2.MORPH_RECT, (3, 3)))
        st = cv2.morphologyEx(st, cv2.MORPH_CLOSE, cv2.getStructuringElement(cv2.MORPH_RECT, (7, 7)))
        return st

    def _get_role_specs(self):
        return {
            "Mini Map + Stats": {
                "zone": (0.60, 0.00, 1.00, 0.42),
                "w_range": (180, 760),
                "h_range": (90, 560),
                "aspect_range": (0.60, 3.40),
                "ideal_aspect": 1.85,
                "ideal_center": (0.84, 0.18),
                "pad": 2,
                "max_keep": 2,
                "expand_l_frac": 0.02,
                "expand_t_frac": 0.02,
                "expand_r_frac": 0.02,
                "expand_b_frac": 0.08,
                "min_anchor_ratio": 0.03,
                "min_stability_ratio": 0.04,
                "min_pos_score": 0.50,
                "secondary_min_ratio": 0.94,
            },
            "Own Health Bar (HP)": {
                "zone": (0.00, 0.58, 0.52, 1.00),
                "w_range": (120, 960),
                "h_range": (20, 300),
                "aspect_range": (1.80, 22.00),
                "ideal_aspect": 5.60,
                "ideal_center": (0.18, 0.86),
                "pad": 2,
                "max_keep": 2,
                "expand_l_frac": 0.01,
                "expand_t_frac": 0.01,
                "expand_r_frac": 0.01,
                "expand_b_frac": 0.01,
                "min_anchor_ratio": 0.008,
                "min_stability_ratio": 0.12,
                "min_pos_score": 0.38,
                "secondary_min_ratio": 0.90,
            },
            "Loot Area": {
                "zone": (0.46, 0.56, 1.00, 1.00),
                "w_range": (160, 1200),
                "h_range": (30, 430),
                "aspect_range": (1.60, 28.00),
                "ideal_aspect": 4.80,
                "ideal_center": (0.83, 0.88),
                "pad": 2,
                "max_keep": 2,
                "expand_l_frac": 0.01,
                "expand_t_frac": 0.01,
                "expand_r_frac": 0.01,
                "expand_b_frac": 0.01,
                "min_anchor_ratio": 0.015,
                "min_stability_ratio": 0.10,
                "min_pos_score": 0.36,
                "secondary_min_ratio": 0.92,
            },
            "Teammates health Bars (HP)": {
                "zone": (0.00, 0.00, 0.35, 0.45),
                "w_range": (80, 500),
                "h_range": (40, 400),
                "aspect_range": (0.40, 4.00),
                "ideal_aspect": 1.20,
                "ideal_center": (0.12, 0.18),
                "pad": 2,
                "max_keep": 3,
                "expand_l_frac": 0.01,
                "expand_t_frac": 0.01,
                "expand_r_frac": 0.01,
                "expand_b_frac": 0.01,
                "min_anchor_ratio": 0.005,
                "min_stability_ratio": 0.10,
                "min_pos_score": 0.30,
            },
            "Spectating Eye": {
                "zone": (0.35, 0.00, 0.65, 0.30),
                "w_range": (40, 200),
                "h_range": (30, 150),
                "aspect_range": (0.80, 2.50),
                "ideal_aspect": 1.40,
                "ideal_center": (0.50, 0.12),
                "pad": 2,
                "max_keep": 1,
                "expand_l_frac": 0.02,
                "expand_t_frac": 0.02,
                "expand_r_frac": 0.02,
                "expand_b_frac": 0.02,
                "min_anchor_ratio": 0.005,
                "min_stability_ratio": 0.05,
                "min_pos_score": 0.40,
            },
            "Boss HP (For When You Are The Boss Character)": {
                "zone": (0.25, 0.00, 0.75, 0.25),
                "w_range": (300, 1200),
                "h_range": (40, 200),
                "aspect_range": (3.00, 15.00),
                "ideal_aspect": 7.00,
                "ideal_center": (0.50, 0.08),
                "pad": 2,
                "max_keep": 1,
                "expand_l_frac": 0.01,
                "expand_t_frac": 0.01,
                "expand_r_frac": 0.01,
                "expand_b_frac": 0.01,
                "min_anchor_ratio": 0.01,
                "min_stability_ratio": 0.15,
                "min_pos_score": 0.50,
            }
        }

    def _extract_role_candidates(self, role_name, spec, base_mask, anchor_mask, edge_mask, stability_mask):
        zx1, zy1, zx2, zy2 = spec["zone"]
        sx1, sy1 = int(self.scale_w * zx1), int(self.scale_h * zy1)
        sx2, sy2 = int(self.scale_w * zx2), int(self.scale_h * zy2)
        if sx2 <= sx1 or sy2 <= sy1:
            return []
        sub_base = base_mask[sy1:sy2, sx1:sx2]
        sub_anchor = anchor_mask[sy1:sy2, sx1:sx2]
        sub_stability = stability_mask[sy1:sy2, sx1:sx2]
        role_mask = cv2.bitwise_or(sub_base, cv2.morphologyEx(
            sub_anchor,
            cv2.MORPH_CLOSE,
            cv2.getStructuringElement(cv2.MORPH_RECT, (3, 3))
        ))
        role_mask = cv2.bitwise_and(
            role_mask,
            cv2.dilate(sub_stability, cv2.getStructuringElement(cv2.MORPH_RECT, (5, 5)))
        )
        role_mask = cv2.morphologyEx(
            role_mask,
            cv2.MORPH_CLOSE,
            cv2.getStructuringElement(cv2.MORPH_RECT, (5, 5))
        )
        contours, _ = cv2.findContours(role_mask, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)
        min_w, max_w = spec["w_range"]
        min_h, max_h = spec["h_range"]
        min_ar, max_ar = spec["aspect_range"]
        ideal_ar = float(spec["ideal_aspect"])
        ideal_cx = self.original_w * float(spec["ideal_center"][0])
        ideal_cy = self.original_h * float(spec["ideal_center"][1])
        pad = int(spec.get("pad", 4))
        min_anchor_ratio = float(spec.get("min_anchor_ratio", 0.0))
        min_stability_ratio = float(spec.get("min_stability_ratio", 0.0))
        min_pos_score = float(spec.get("min_pos_score", 0.0))
        scored = []
        for c in contours:
            x, y, w, h = cv2.boundingRect(c)
            x += sx1
            y += sy1
            if w < 3 or h < 3:
                continue
            expand_l = int(round(w * float(spec.get("expand_l_frac", 0.0))))
            expand_t = int(round(h * float(spec.get("expand_t_frac", 0.0))))
            expand_r = int(round(w * float(spec.get("expand_r_frac", 0.0))))
            expand_b = int(round(h * float(spec.get("expand_b_frac", 0.0))))
            x0 = max(0, x - pad - expand_l)
            y0 = max(0, y - pad - expand_t)
            x1 = min(self.scale_w, x + w + pad + expand_r)
            y1 = min(self.scale_h, y + h + pad + expand_b)
            xs, ys, ws, hs = int(x0), int(y0), int(max(1, x1 - x0)), int(max(1, y1 - y0))
            rect = self._scaled_to_original_rect(xs, ys, ws, hs)
            rw, rh = rect.width(), rect.height()
            if not (min_w <= rw <= max_w and min_h <= rh <= max_h):
                continue
            aspect = rw / float(max(1, rh))
            if not (min_ar <= aspect <= max_ar):
                continue
            roi_anchor = anchor_mask[ys:ys+hs, xs:xs+ws]
            roi_edge = edge_mask[ys:ys+hs, xs:xs+ws]
            roi_stable = stability_mask[ys:ys+hs, xs:xs+ws]
            anchor_ratio = float(cv2.countNonZero(roi_anchor)) / float(max(1, roi_anchor.size))
            edge_ratio = float(cv2.countNonZero(roi_edge)) / float(max(1, roi_edge.size))
            stable_ratio = float(cv2.countNonZero(roi_stable)) / float(max(1, roi_stable.size))
            if anchor_ratio < min_anchor_ratio:
                continue
            if stable_ratio < min_stability_ratio:
                continue
            contour_area = float(cv2.contourArea(c))
            fill_ratio = contour_area / float(max(1, w * h))
            cx = rect.x() + (rw / 2.0)
            cy = rect.y() + (rh / 2.0)
            dx = (cx - ideal_cx) / float(self.original_w)
            dy = (cy - ideal_cy) / float(self.original_h)
            dist = (dx * dx + dy * dy) ** 0.5
            pos_score = max(0.0, 1.0 - (dist * 1.8))
            if pos_score < min_pos_score:
                continue
            aspect_dev = abs(np.log(max(1e-6, aspect / max(1e-6, ideal_ar))))
            aspect_score = max(0.0, 1.0 - (aspect_dev / np.log(2.0)))
            area_norm = min(1.0, (rw * rh) / float(max_w * max_h))
            top_band_bonus = 0.0
            if role_name == "Mini Map + Stats":
                band_h = max(4, int(hs * 0.22))
                band_edge = edge_mask[ys:ys+band_h, xs:xs+ws]
                band_anchor = anchor_mask[ys:ys+band_h, xs:xs+ws]
                top_edge_ratio = float(cv2.countNonZero(band_edge)) / float(max(1, band_edge.size))
                top_anchor_ratio = float(cv2.countNonZero(band_anchor)) / float(max(1, band_anchor.size))
                top_band_bonus = (top_edge_ratio * 7.0) + (top_anchor_ratio * 6.0)
            score = (
                (anchor_ratio * 44.0) +
                (edge_ratio * 20.0) +
                (stable_ratio * 16.0) +
                (fill_ratio * 9.0) +
                (pos_score * 16.0) +
                (aspect_score * 16.0) +
                (area_norm * 6.0) +
                top_band_bonus
            )
            scored.append((score, rect))
        if not scored:
            return []
        return self._nms_candidates(scored, iou_threshold=0.40, max_keep=spec.get("max_keep", 2))

    def _extract_generic_candidates(self, base_mask, anchor_mask, edge_mask, stability_mask=None):
        if stability_mask is None:
            stability_mask = np.full(base_mask.shape[:2], 255, dtype=np.uint8)
        contours, _ = cv2.findContours(base_mask, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)
        scored = []
        for c in contours:
            x, y, w, h = cv2.boundingRect(c)
            if w < 4 or h < 4:
                continue
            xs, ys, ws, hs = self._expand_and_clamp_scaled_rect(x, y, w, h, 4)
            rect = self._scaled_to_original_rect(xs, ys, ws, hs)
            rw, rh = rect.width(), rect.height()
            if rw < 20 or rh < 16:
                continue
            if rw > 620 or rh > 420:
                continue
            area = rw * rh
            if area < 1000 or area > 240000:
                continue
            roi_anchor = anchor_mask[ys:ys+hs, xs:xs+ws]
            roi_edge = edge_mask[ys:ys+hs, xs:xs+ws]
            roi_stable = stability_mask[ys:ys+hs, xs:xs+ws]
            anchor_ratio = float(cv2.countNonZero(roi_anchor)) / float(max(1, roi_anchor.size))
            edge_ratio = float(cv2.countNonZero(roi_edge)) / float(max(1, roi_edge.size))
            stable_ratio = float(cv2.countNonZero(roi_stable)) / float(max(1, roi_stable.size))
            fill_ratio = float(cv2.contourArea(c)) / float(max(1, w * h))
            area_score = min(1.0, area / 70000.0)
            cxn = (rect.x() + (rw / 2.0)) / float(max(1, self.original_w))
            cyn = (rect.y() + (rh / 2.0)) / float(max(1, self.original_h))
            if stable_ratio < 0.44:
                continue
            if anchor_ratio < 0.04 and edge_ratio < 0.03:
                continue
            in_mid_right_band = (0.32 < cyn < 0.72) and (0.36 < cxn < 0.94)
            if in_mid_right_band and (anchor_ratio < 0.18 and stable_ratio < 0.68):
                continue
            corner_pref = 1.0 if (cyn > 0.58 or cyn < 0.24 or cxn < 0.20 or cxn > 0.80) else 0.0
            center_dist = ((cxn - 0.5) ** 2 + (cyn - 0.5) ** 2) ** 0.5
            center_penalty = max(0.0, (0.30 - center_dist) / 0.30)
            score = (
                (anchor_ratio * 30.0) +
                (edge_ratio * 16.0) +
                (stable_ratio * 13.0) +
                (fill_ratio * 7.0) +
                (area_score * 6.0) +
                (corner_pref * 8.0) -
                (center_penalty * 10.0)
            )
            scored.append((score, rect))
        if not scored:
            return []
        return self._nms_candidates(scored, iou_threshold=0.42, max_keep=6)

    def _get_binary_mask_from_median(self, median_frame):
        gray = cv2.cvtColor(median_frame, cv2.COLOR_BGR2GRAY)
        thresh = cv2.adaptiveThreshold(gray, 255, cv2.ADAPTIVE_THRESH_GAUSSIAN_C, cv2.THRESH_BINARY_INV, 21, 5)
        kernel = cv2.getStructuringElement(cv2.MORPH_RECT, (5, 5))
        closed = cv2.morphologyEx(thresh, cv2.MORPH_CLOSE, kernel)
        return closed

    def _merge_overlapping_rects(self, rects):
        if not rects:
            return []
        merged = []
        rects.sort(key=lambda r: r.x())
        while rects:
            current = rects.pop(0)
            i = 0
            while i < len(rects):
                if current.intersects(rects[i].adjusted(-25, -25, 25, 25)):
                    current = current.united(rects.pop(i))
                    i = 0
                else:
                    i += 1
            merged.append(current)
        return merged

    def _hunt_for_circles(self, median_frame):
        if median_frame is None: return []
        scale_x = self.original_w / float(self.scale_w)
        scale_y = self.original_h / float(self.scale_h)
        map_zone_gray = cv2.cvtColor(median_frame[0:int(self.scale_h*0.45), int(self.scale_w*0.55):self.scale_w], cv2.COLOR_BGR2GRAY)
        map_zone_gray = cv2.GaussianBlur(map_zone_gray, (9, 9), 2)
        circles = cv2.HoughCircles(map_zone_gray, cv2.HOUGH_GRADIENT, 1, 120, param1=50, param2=30, minRadius=int(30/scale_x), maxRadius=int(180/scale_x))
        found_circles = []
        if circles is not None:
            self.logger.info(f"Circle Hunter found {len(circles[0])} potential circles for mini-map.")
            for circ in circles[0, :]:
                cx, cy, r = circ
                fx_scaled = (cx - r + int(self.scale_w*0.55)) * scale_x
                fy_scaled = (cy - r) * scale_y
                fw_scaled = 2 * r * scale_x
                fh_scaled = 2 * r * scale_y
                found_circles.append(QRect(int(fx_scaled) - 10, int(fy_scaled) - 10, int(fw_scaled) + 20, int(fh_scaled) + 20))
        return found_circles

    def extract_all(self, snapshot_path, cancel_check=None):
        self.logger.info("--- Starting Smart HUD Detection ---")
        input_file = self.params.get("input_file")
        total_ms = int(self.params.get("total_ms", 0) or 0)
        if not input_file:
            self.logger.error("No input file parameter found.")
            return []
        cancel_check = cancel_check or (lambda: False)
        estimated_ms = self._estimate_video_duration_ms(input_file)
        if total_ms <= 0:
            total_ms = estimated_ms
        if total_ms <= 0:
            total_ms = 30000
            self.logger.warning("Video duration unavailable; using conservative fallback duration for sampling logic.")
        if total_ms <= 3500:
            target_frames = 45
        elif total_ms <= 15000:
            target_frames = 50
        else:
            target_frames = 60
        self.logger.info(f"Sampling ~{target_frames} frames uniformly across {total_ms}ms.")
        all_frames = self._extract_uniform_frames(input_file, target_frames, cancel_check)
        if cancel_check():
            return []
        min_required = 8 if total_ms < 5000 else 16
        if len(all_frames) < min_required:
            self.logger.error(f"Failed to extract enough frames. Got {len(all_frames)}, need at least {min_required}.")
            return []
        self.logger.info(f"Computing temporal median from {len(all_frames)} frames...")
        median_frame = np.median(all_frames, axis=0).astype(np.uint8)
        base_mask = self._get_binary_mask_from_median(median_frame)
        hsv = cv2.cvtColor(median_frame, cv2.COLOR_BGR2HSV)
        anchor_mask = self.get_hud_color_anchors(hsv)
        anchor_mask = cv2.morphologyEx(
            anchor_mask,
            cv2.MORPH_OPEN,
            cv2.getStructuringElement(cv2.MORPH_RECT, (3, 3))
        )
        anchor_mask = cv2.morphologyEx(
            anchor_mask,
            cv2.MORPH_CLOSE,
            cv2.getStructuringElement(cv2.MORPH_RECT, (5, 5))
        )
        gray = cv2.cvtColor(median_frame, cv2.COLOR_BGR2GRAY)
        edge_mask = cv2.Canny(gray, 60, 150)
        stability_mask = self._compute_temporal_stability_mask(all_frames)
        role_specs = self._get_role_specs()
        role_order = [
            "Mini Map + Stats", 
            "Own Health Bar (HP)", 
            "Loot Area",
            "Teammates health Bars (HP)",
            "Spectating Eye",
            "Boss HP (For When You Are The Boss Character)"
        ]
        role_candidates_map = {}
        for role_name, spec in role_specs.items():
            candidates = self._extract_role_candidates(
                role_name,
                spec,
                base_mask,
                anchor_mask,
                edge_mask,
                stability_mask,
            )
            role_candidates_map[role_name] = candidates
            self.logger.info(f"Role detector [{role_name}] candidates: {len(candidates)}")
        selected = []
        for role_name in role_order:
            cands = role_candidates_map.get(role_name, [])
            if cands:
                selected.append(cands[0][1])
        for role_name in role_order:
            cands = role_candidates_map.get(role_name, [])
            if len(cands) >= 2:
                best_score, best_rect = cands[0]
                second_score, second_rect = cands[1]
                min_ratio = float(role_specs.get(role_name, {}).get("secondary_min_ratio", 0.88))
                best_area = max(1, best_rect.width() * best_rect.height())
                second_area = max(1, second_rect.width() * second_rect.height())
                area_ratio = second_area / float(best_area)
                if second_score >= (best_score * min_ratio) and (0.45 <= area_ratio <= 1.85):
                    selected.append(second_rect)
        primary_found = sum(1 for rn in role_order if role_candidates_map.get(rn))
        if primary_found < 3:
            generic_scored = self._extract_generic_candidates(base_mask, anchor_mask, edge_mask, stability_mask)
            self.logger.info(f"Generic detector candidates: {len(generic_scored)}")
            for score, rect in generic_scored:
                if len(selected) >= 6:
                    break
                if score < 26.0:
                    continue
                if any(self._rect_iou(rect, s) >= 0.35 for s in selected):
                    continue
                cxn = (rect.x() + (rect.width() / 2.0)) / float(max(1, self.original_w))
                cyn = (rect.y() + (rect.height() / 2.0)) / float(max(1, self.original_h))
                if (0.32 < cyn < 0.72) and (0.36 < cxn < 0.94) and score < 32.0:
                    continue
                selected.append(rect)
        else:
            self.logger.info("Skipping generic fallback because primary role detections are complete.")
        if not role_candidates_map.get("Mini Map + Stats"):
            circle_rois = self._hunt_for_circles(median_frame)
            if circle_rois:
                self.logger.info(f"Circle detector candidates: {len(circle_rois)}")
            for rect in circle_rois[:2]:
                if any(self._rect_iou(rect, s) >= 0.40 for s in selected):
                    continue
                selected.append(rect)
        if not selected:
            self.logger.warning("No candidates found by role/generic detectors.")
            return []
        final = self._dedupe_rects_by_iou(selected, iou_threshold=0.45)
        self.logger.info(f"--- Detection Finished: Found {len(final)} final HUD elements. ---")
        return final

    def grab_cut_with_rect(self, img_path, rect):
        img = cv2.imread(img_path)
        if img is None:
            self.logger.error("GrabCut failed: Image could not be read.")
            return None
        mask = np.zeros(img.shape[:2], np.uint8)
        bgdModel = np.zeros((1, 65), np.float64)
        fgdModel = np.zeros((1, 65), np.float64)
        try:
            cv2.grabCut(img, mask, rect, bgdModel, fgdModel, 5, cv2.GC_INIT_WITH_RECT)
        except Exception as e:
            self.logger.error(f"GrabCut algorithm failed: {e}")
            return None
        final_mask = np.where((mask == cv2.GC_FGD) | (mask == cv2.GC_PR_FGD), 1, 0).astype('uint8')
        if np.sum(final_mask) == 0:
            self.logger.warning("GrabCut completed but detected no foreground.")
            return QRect(*rect)
        cnts, _ = cv2.findContours(final_mask, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)
        if not cnts:
            return QRect(*rect)
        best_c = max(cnts, key=cv2.contourArea)
        x, y, ww, hh = cv2.boundingRect(best_c)
        return QRect(x, y, ww, hh)

    def get_hud_color_anchors(self, hsv):
        mask_hp = cv2.inRange(hsv, (35, 80, 80), (95, 255, 255))
        mask_shield = cv2.inRange(hsv, (100, 80, 80), (140, 255, 255))
        mask_loot = cv2.inRange(hsv, (15, 100, 100), (40, 255, 255))
        mask_rarity = cv2.inRange(hsv, (120, 50, 50), (175, 255, 255))
        return cv2.bitwise_or(cv2.bitwise_or(mask_hp, mask_shield), cv2.bitwise_or(mask_loot, mask_rarity))

    def auto_tag_roi(self, rect, w, h):
        x, y, rw, rh = rect.x(), rect.y(), rect.width(), rect.height()
        cx, cy = x + rw/2.0, y + rh/2.0
        if cy < h * 0.4 and cx > w * 0.6: return "Mini Map + Stats"
        if cy > h * 0.6 and cx < w * 0.4: return "Own Health Bar (HP)"
        if cy > h * 0.6 and cx > w * 0.6: return "Loot Area"
        if cy < h * 0.4 and cx < w * 0.4: return "Teammates health Bars (HP)"
        if cy < h * 0.3 and cx > w * 0.4 and cx < w * 0.6: return "Spectating Eye"
        return "Manual Crop"

class MagicWand:
    def __init__(self, logger=None, params=None):
        self.extractor = HUDExtractor(logger, params)

    def detect_static_hud_regions(self, snapshot_path: str, cancel_check=None):
        return self.extractor.extract_all(snapshot_path, cancel_check)

    def grab_element_in_rect(self, img_path, rect):
        return self.extractor.grab_cut_with_rect(img_path, rect)

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
