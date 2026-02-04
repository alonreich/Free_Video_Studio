import cv2
import numpy as np
import os
import logging
from PyQt5.QtCore import QRect, QPoint, QObject, pyqtSignal

class HUDExtractor:
    def __init__(self, logger=None):
        self.logger = logger or logging.getLogger(__name__)
        self.anchors = {}
        self._load_anchors()

    def _load_anchors(self):
        script_dir = os.path.dirname(os.path.abspath(__file__))
        anchor_dir = os.path.join(script_dir, 'anchors')
        if not os.path.isdir(anchor_dir):
            self.logger.error(f"Anchor directory not found: {anchor_dir}")
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
                self.anchors[key] = cv2.imread(path, 0)
                if self.anchors[key] is None:
                    self.logger.error(f"Failed to load anchor: {filename}")
                else:
                    self.logger.info(f"Loaded anchor: {key}")
            else:
                self.logger.warning(f"Missing anchor file: {filename}")
                self.anchors[key] = None
    
    def find_anchor(self, frame_gray, anchor_key, threshold=0.8):
        """Finds a single anchor in a grayscale frame."""
        anchor_img = self.anchors.get(anchor_key)
        if anchor_img is None:
            return None
        res = cv2.matchTemplate(frame_gray, anchor_img, cv2.TM_CCOEFF_NORMED)
        _, max_val, _, max_loc = cv2.minMaxLoc(res)
        if max_val >= threshold:
            self.logger.info(f"Anchor '{anchor_key}' found at {max_loc} with confidence {max_val:.2f}")
            return max_loc
        self.logger.warning(f"Anchor '{anchor_key}' not found with sufficient confidence (max: {max_val:.2f} < threshold: {threshold})")
        return None

    def _extract_loot_module(self, frame_gray, frame_color):
        """
        Extracts the 5-slot weapon/item bar from the bottom-right.
        """
        if self.anchors.get('loot_start') is None or self.anchors.get('loot_end') is None:
             return None
        p1 = self.find_anchor(frame_gray, 'loot_start', threshold=0.6)
        p5 = self.find_anchor(frame_gray, 'loot_end', threshold=0.6)
        if p1 is None or p5 is None:
            return None
        slot1_anchor_h, slot1_anchor_w = self.anchors['loot_start'].shape
        slot5_anchor_h, slot5_anchor_w = self.anchors['loot_end'].shape
        x1 = p1[0]
        y1 = p1[1] - 100
        width = (p5[0] - p1[0]) + slot5_anchor_w + 20
        height = 120
        img_h, img_w = frame_gray.shape
        x1 = max(0, min(x1, img_w - 10))
        y1 = max(0, min(y1, img_h - 10))
        width = max(10, min(width, img_w - x1))
        height = max(10, min(height, img_h - y1))
        return QRect(int(x1), int(y1), int(width), int(height))

    def _extract_hp_module(self, frame_gray, frame_color):
        """
        Extracts the Health and Shield bars from the bottom-left.
        """
        hp_anchor_pos = self.find_anchor(frame_gray, 'hp_icon', threshold=0.6)
        if hp_anchor_pos is None:
            return None
        anchor_h, anchor_w = self.anchors['hp_icon'].shape
        x1 = hp_anchor_pos[0] + (anchor_w // 2)
        y1 = hp_anchor_pos[1] - 20
        width = 400
        height = 100
        img_h, img_w = frame_gray.shape
        x1 = max(0, min(x1, img_w - 10))
        y1 = max(0, min(y1, img_h - 10))
        width = max(10, min(width, img_w - x1))
        height = max(10, min(height, img_h - y1))
        return QRect(int(x1), int(y1), int(width), int(height))

    def _extract_map_stats_module(self, frame_gray, frame_color):
        """
        Extracts the Minimap and adjacent stats from the top-right.
        """
        map_anchor_pos = self.find_anchor(frame_gray, 'map_edge', threshold=0.6)
        if map_anchor_pos is None:
            return None
        x1 = map_anchor_pos[0] - 20
        y1 = map_anchor_pos[1] - 10
        width = 350
        height = 450
        img_h, img_w = frame_gray.shape
        x1 = max(0, min(x1, img_w - 10))
        y1 = max(0, min(y1, img_h - 10))
        width = max(10, min(width, img_w - x1))
        height = max(10, min(height, img_h - y1))
        return QRect(int(x1), int(y1), int(width), int(height))

    def extract_all(self, snapshot_path):
        valid_anchors = [k for k, v in self.anchors.items() if v is not None]
        if not valid_anchors:
            raise FileNotFoundError("CRITICAL: No reference images found in 'anchors' folder. Magic Wand cannot function.")
        if not os.path.exists(snapshot_path):
            self.logger.error(f"Snapshot path does not exist: {snapshot_path}")
            return []
        frame_color = cv2.imread(snapshot_path, cv2.IMREAD_COLOR)
        if frame_color is None:
            self.logger.error(f"Failed to read image: {snapshot_path}")
            return []
        frame_gray = cv2.cvtColor(frame_color, cv2.COLOR_BGR2GRAY)
        rois = []
        try:
            loot_roi = self._extract_loot_module(frame_gray, frame_color)
            if loot_roi:
                rois.append(loot_roi)
        except Exception as e:
            self.logger.warning(f"Loot extraction failed: {e}")
        try:
            hp_roi = self._extract_hp_module(frame_gray, frame_color)
            if hp_roi:
                rois.append(hp_roi)
        except Exception as e:
            self.logger.warning(f"HP extraction failed: {e}")
        try:
            map_roi = self._extract_map_stats_module(frame_gray, frame_color)
            if map_roi:
                rois.append(map_roi)
        except Exception as e:
            self.logger.warning(f"Map extraction failed: {e}")
        self.logger.info(f"HUDExtractor found {len(rois)} regions.")
        return rois

class MagicWand:
    """
    Compatibility wrapper to integrate the new HUDExtractor
    with the existing MagicWandWorker structure.
    """

    def __init__(self, logger=None):
        self.logger = logger
        self.extractor = HUDExtractor(logger)
        self.cv2 = cv2

    def detect_static_hud_regions(self, snapshot_path: str, **kwargs):
        """
        This method now takes a snapshot path instead of a video path.
        It calls the new HUDExtractor.
        The **kwargs are ignored as they were for the old variance-based method.
        """
        self.logger.info("Using new HUDExtractor via compatibility wrapper.")
        return self.extractor.extract_all(snapshot_path)

class MagicWandWorker(QObject):
    """
    Modified Worker to run HUDExtractor in a separate thread.
    It now accepts a snapshot_path.
    """
    finished = pyqtSignal(list)
    error = pyqtSignal(str)

    def __init__(self, magic_wand_instance, snapshot_path, params):
        super().__init__()
        self.magic_wand = magic_wand_instance
        self.snapshot_path = snapshot_path
        self.params = params

    def run(self):
        """Execute the detection and emit signals."""
        try:
            regions = self.magic_wand.detect_static_hud_regions(self.snapshot_path)
            if not regions:
                self.error.emit("No HUD elements detected. Please try a frame with clearer HUD visibility or use Manual Drawing.")
            else:
                self.finished.emit(regions)
        except Exception as e:
            if self.magic_wand.logger:
                self.magic_wand.logger.error(f"Magic Wand thread (HUDExtractor) crashed: {e}", exc_info=True)
            self.error.emit(f"Magic Wand analysis failed. Please try Manual Drawing. Error: {e}")
