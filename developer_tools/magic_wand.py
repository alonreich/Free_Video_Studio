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
        """Loads anchor template images from the 'anchors' directory."""
        anchor_dir = 'anchors'
        if not os.path.isdir(anchor_dir):
            self.logger.error(f"Anchor directory '{anchor_dir}' not found. HUDExtractor cannot function.")
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
                    self.logger.error(f"Failed to load anchor image '{path}'. Check file integrity.")
                else:
                    self.logger.info(f"Successfully loaded anchor: '{key}' from '{path}'")
            else:
                self.logger.warning(f"Anchor image not found for '{key}' at path '{path}'. This module will be disabled.")
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
        Uses keybinds for 'slot 1' and 'slot 5' as anchors.
        """
        p1 = self.find_anchor(frame_gray, 'loot_start')
        p5 = self.find_anchor(frame_gray, 'loot_end')
        if p1 is None or p5 is None:
            self.logger.warning("Could not find loot anchors. Skipping loot module extraction.")
            return None
        slot1_anchor_h, slot1_anchor_w = self.anchors['loot_start'].shape
        slot5_anchor_h, slot5_anchor_w = self.anchors['loot_end'].shape
        single_slot_width = slot5_anchor_w + 20
        y_offset = -100
        crop_height = 120
        x1 = p1[0]
        y1 = p1[1] + y_offset
        width = (p5[0] - p1[0]) + single_slot_width
        return QRect(x1, y1, width, crop_height)

    def _extract_hp_module(self, frame_gray, frame_color):
        """
        Extracts the Health and Shield bars from the bottom-left.
        Uses the HP or Shield icon as an anchor.
        """
        hp_anchor_pos = self.find_anchor(frame_gray, 'hp_icon')
        if hp_anchor_pos is None:
            self.logger.warning("Could not find HP anchor. Skipping HP module extraction.")
            return None
        anchor_h, anchor_w = self.anchors['hp_icon'].shape
        x_offset = anchor_w // 2
        y_offset = -20
        crop_width = 400
        crop_height = 100
        x1 = hp_anchor_pos[0] + x_offset
        y1 = hp_anchor_pos[1] + y_offset
        return QRect(x1, y1, crop_width, crop_height)

    def _extract_map_stats_module(self, frame_gray, frame_color):
        """
        Extracts the Minimap and adjacent stats from the top-right.
        Uses the minimap border as an anchor.
        """
        map_anchor_pos = self.find_anchor(frame_gray, 'map_edge', threshold=0.7)
        if map_anchor_pos is None:
            self.logger.warning("Could not find Map anchor. Skipping Map/Stats module extraction.")
            return None
        anchor_h, anchor_w = self.anchors['map_edge'].shape
        x_offset = -20
        y_offset = -10
        crop_width = 350
        crop_height = 450
        x1 = map_anchor_pos[0] + x_offset
        y1 = map_anchor_pos[1] + y_offset
        return QRect(x1, y1, crop_width, crop_height)

    def extract_all(self, snapshot_path):
        """
        Main extraction method. Takes a single frame (snapshot) and runs all modules.
        Returns a list of QRects for each successfully found element.
        """
        if not os.path.exists(snapshot_path):
            self.logger.error(f"Snapshot path does not exist: {snapshot_path}")
            return []
        frame_color = cv2.imread(snapshot_path)
        if frame_color is None:
            self.logger.error(f"Failed to read image from snapshot path: {snapshot_path}")
            return []
        frame_gray = cv2.cvtColor(frame_color, cv2.COLOR_BGR2GRAY)
        rois = []
        loot_roi = self._extract_loot_module(frame_gray, frame_color)
        if loot_roi:
            rois.append(loot_roi)
        hp_roi = self._extract_hp_module(frame_gray, frame_color)
        if hp_roi:
            rois.append(hp_roi)
        map_roi = self._extract_map_stats_module(frame_gray, frame_color)
        if map_roi:
            rois.append(map_roi)
        self.logger.info(f"HUDExtractor found {len(rois)} regions of interest.")
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
            self.finished.emit(regions)
        except Exception as e:
            if self.magic_wand.logger:
                self.magic_wand.logger.error(f"Magic Wand thread (HUDExtractor) crashed: {e}", exc_info=True)
            self.error.emit(f"Magic Wand analysis failed: {e}")
