"""
Enhanced logging system for Fortnite Video Software crop operations.
Captures detailed information about user interactions with crop tools.
"""

import os
import json
import logging
import time
from datetime import datetime
from PyQt5.QtCore import QRect, QPoint

class EnhancedCropLogger:
    """Enhanced logger for tracking crop operations with detailed user interactions."""
    
    def __init__(self, base_logger, log_dir=None):
        """
        Initialize enhanced logger.
        Args:
            base_logger: Existing logger instance (will be used for all logging)
            log_dir: Directory for storing operation JSON files (defaults to logs/crop_operations/)
        """
        self.base_logger = base_logger
        self.script_dir = os.path.dirname(os.path.abspath(__file__))
        self.log_dir = None
        self.crop_logger = base_logger
        self.current_operation = None
        self.operation_start_time = None
        self.crop_history = []
        
    def log_crop_operation_start(self, operation_type, initial_rect=None, hud_element=None):
        """Log the start of a crop operation."""
        self.current_operation = {
            'type': operation_type,
            'start_time': time.time(),
            'initial_rect': self._rect_to_dict(initial_rect) if initial_rect else None,
            'hud_element': hud_element,
            'movements': [],
            'resizes': [],
            'final_position': None,
            'final_size': None
        }
        self.crop_logger.info("=" * 60)
        self.crop_logger.info(f"CROP OPERATION STARTED: {operation_type}")
        self.crop_logger.info("-" * 40)
        self.crop_logger.info(f"HUD Element: {hud_element or 'Not selected'}")
        self.crop_logger.info(f"Initial Rectangle: {self._rect_to_str(initial_rect)}")
        self.crop_logger.info("=" * 60)
        
    def log_rubberband_selection(self, rect, mouse_position):
        """Log when user selects an area with rubberband."""
        if self.current_operation and self.current_operation['type'] == 'rubberband_select':
            self.current_operation['initial_rect'] = self._rect_to_dict(rect)
        self.crop_logger.info(f"RUBBERBAND_SELECT | Rect: {self._rect_to_str(rect)} | "
                             f"Mouse: ({mouse_position.x()}, {mouse_position.y()})")
        
    def log_movement(self, rect, movement_delta, from_position, to_position):
        """Log movement of crop rectangle."""
        movement_record = {
            'timestamp': time.time(),
            'delta': {'dx': movement_delta.x(), 'dy': movement_delta.y()},
            'from': {'x': from_position.x(), 'y': from_position.y()},
            'to': {'x': to_position.x(), 'y': to_position.y()},
            'rect': self._rect_to_dict(rect)
        }
        if self.current_operation:
            self.current_operation['movements'].append(movement_record)
        self.crop_logger.info(f"MOVEMENT | Delta: ({movement_delta.x()}, {movement_delta.y()}) | "
                             f"From: ({from_position.x()}, {from_position.y()}) → "
                             f"To: ({to_position.x()}, {to_position.y()}) | "
                             f"Rect: {self._rect_to_str(rect)}")
        
    def log_resize(self, rect, resize_edge, from_size, to_size):
        """Log resize operation on crop rectangle."""
        resize_record = {
            'timestamp': time.time(),
            'edge': resize_edge,
            'from_size': {'width': from_size.width(), 'height': from_size.height()},
            'to_size': {'width': to_size.width(), 'height': to_size.height()},
            'rect': self._rect_to_dict(rect)
        }
        if self.current_operation:
            self.current_operation['resizes'].append(resize_record)
        self.crop_logger.info(f"RESIZE | Edge: {resize_edge} | "
                             f"From: {from_size.width()}x{from_size.height()} → "
                             f"To: {to_size.width()}x{to_size.height()} | "
                             f"Rect: {self._rect_to_str(rect)}")
        
    def log_hud_element_selection(self, hud_element, rect):
        """Log which HUD element was selected by the user."""
        if self.current_operation:
            self.current_operation['hud_element'] = hud_element
        self.crop_logger.info("=" * 50)
        self.crop_logger.info(f"HUD ELEMENT SELECTED: {hud_element}")
        self.crop_logger.info("-" * 30)
        self.crop_logger.info(f"Rectangle: {self._rect_to_str(rect)}")
        self.crop_logger.info("=" * 50)

    def log_button_click(self, button_name, context=None):
        """Log a generic button click with optional context."""
        msg = f"BUTTON CLICKED | '{button_name}'"
        if context:
            msg += f" | Context: {context}"
        self.crop_logger.info(msg)

    def log_user_action(self, action_name, details=None):
        """Log a general user action with optional details."""
        msg = f"USER ACTION | {action_name}"
        if details:
            msg += f" | Details: {details}"
        self.crop_logger.info(msg)

    def log_snapshot_taken(self, timestamp_sec, file_path):
        """Log when a snapshot is successfully taken."""
        self.crop_logger.info(
            f"SNAPSHOT CAPTURED | Source Video Time: {timestamp_sec:.2f}s | "
            f"Saved to: {file_path}"
        )

    def log_hud_crop_details(self, role, landscape_rect, portrait_pos, portrait_size):
        """Log the full transformation of a HUD element from landscape to portrait."""
        self.crop_logger.info("-" * 60)
        self.crop_logger.info(f"HUD CROP COMPLETED: {role}")
        self.crop_logger.info(f"  Landscape Rect: {self._rect_to_str(landscape_rect)}")
        self.crop_logger.info(f"  Portrait Pos:   ({int(portrait_pos.x())}, {int(portrait_pos.y())})")
        self.crop_logger.info(f"  Portrait Size:  {int(portrait_size[0])}x{int(portrait_size[1])}")
        self.crop_logger.info("-" * 60)

    def log_video_loaded(self, file_path: str, resolution: str):
        """Logs when a new video file is loaded into the application."""
        self.crop_logger.info(f"VIDEO LOADED | Path: '{file_path}' | Resolution: {resolution}")

    def log_config_changed(self, config_path, role, state):
        """Log config changes before/after saving a crop."""
        try:
            if os.path.exists(config_path):
                with open(config_path, 'r') as f:
                    config = json.load(f)
                self.crop_logger.info(f"CONFIG {state.upper()} CHANGE for {role}")
                self.crop_logger.info(f"Config path: {config_path}")
                self.crop_logger.info(json.dumps(config, indent=2))
            else:
                self.crop_logger.info(f"CONFIG {state.upper()} CHANGE for {role} - file not found")
        except Exception as e:
            self.crop_logger.error(f"Error logging config {state}: {e}")

    def log_item_added(self, role: str, position: QPoint, size: tuple, original_res: str, scale_factor: float):
        """Logs when a new item is added to the portrait scene, with full context."""
        self.crop_logger.info(
            f"ITEM ADDED to Portrait | Role: '{role}' | "
            f"Initial Position: ({int(position.x())}, {int(position.y())}) | "
            f"Initial Size: {int(size[0])}x{int(size[1])} | "
            f"Based on Source Video: {original_res} | "
            f"Applied Scale Factor: {scale_factor:.3f}"
        )
        
    def log_portrait_placement(self, role: str, position: QPoint, size: tuple, corner_placement: str):
        """
        Log where a crop piece is placed or moved to in the portrait window.
        Args:
            role: The display name of the HUD element (e.g., "Loot Area").
            position: QPoint of the top-left corner.
            size: Tuple of (width, height).
            corner_placement: String describing placement (e.g., "top_left").
        """
        pos_dict = {'x': position.x(), 'y': position.y()}
        size_dict = {'width': size[0], 'height': size[1]}
        self.crop_logger.info(
            f"PORTRAIT PLACEMENT for '{role}' | "
            f"Position: ({pos_dict['x']}, {pos_dict['y']}) | "
            f"Size: {size_dict['width']}x{size_dict['height']} | "
            f"Area: {corner_placement}"
        )
        
    def log_finished_button_click(self, config_path_before, config_path_after):
        """Log when user clicks the Finished button and capture config file changes."""
        try:
            with open(config_path_before, 'r') as f:
                config_before = json.load(f)
            with open(config_path_after, 'r') as f:
                config_after = json.load(f)
            self.crop_logger.info("=" * 80)
            self.crop_logger.info("FINISHED BUTTON CLICKED - CONFIGURATION UPDATE")
            self.crop_logger.info("=" * 80)
            self.crop_logger.info("PROBING THE PREVIOUS EXISTING CONFIG JSON FILE FOR CROPS COORDINATION AND OVERLAYS:")
            self.crop_logger.info(f"File: {config_path_before}")
            self.crop_logger.info("-" * 40)
            self.crop_logger.info(json.dumps(config_before, indent=2))
            self.crop_logger.info("-" * 40)
            self.crop_logger.info("OVERWRITING THE JSON CONFIG FILE WITH THIS NEW VERSION:")
            self.crop_logger.info(f"File: {config_path_after}")
            self.crop_logger.info("-" * 40)
            self.crop_logger.info(json.dumps(config_after, indent=2))
            self.crop_logger.info("-" * 40)
            differences = self._find_config_differences(config_before, config_after)
            self.crop_logger.info("CONFIGURATION DIFFERENCES (BEFORE vs AFTER):")
            self.crop_logger.info("-" * 40)
            self.crop_logger.info(json.dumps(differences, indent=2))
            self.crop_logger.info("-" * 40)
            self.crop_logger.info("COMPLETE CONFIGURATION SUMMARY:")
            self.crop_logger.info("-" * 40)
            configured_elements = set()
            if 'crops_1080p' in config_after:
                configured_elements.update(config_after['crops_1080p'].keys())
            if 'crops' in config_after:
                configured_elements.update(config_after['crops'].keys())
            try:
                from config import HUD_ELEMENT_MAPPINGS
                all_possible_elements = set(HUD_ELEMENT_MAPPINGS.keys())
                self.crop_logger.info(f"Total configured elements: {len(configured_elements)}/{len(all_possible_elements)}")
                self.crop_logger.info("Configured elements:")
                for element in sorted(configured_elements):
                    display_name = HUD_ELEMENT_MAPPINGS.get(element, element)
                    self.crop_logger.info(f"  [X] {display_name} ({element})")
                not_configured = all_possible_elements - configured_elements
                if not_configured:
                    self.crop_logger.info("Not configured yet:")
                    for element in sorted(not_configured):
                        display_name = HUD_ELEMENT_MAPPINGS.get(element, element)
                        self.crop_logger.info(f"  [ ] {display_name} ({element})")
            except ImportError:
                self.crop_logger.info(f"Configured elements ({len(configured_elements)} total):")
                for element in sorted(configured_elements):
                    self.crop_logger.info(f"  [X] {element}")
            self.crop_logger.info("-" * 40)
            self.crop_logger.info("=" * 80)
            if self.current_operation:
                self.current_operation['end_time'] = time.time()
                self.current_operation['duration'] = self.current_operation['end_time'] - self.current_operation['start_time']
                self.current_operation['config_before'] = config_before
                self.current_operation['config_after'] = config_after
                self.current_operation['differences'] = differences
                self.crop_history.append(self.current_operation)
        except Exception as e:
            self.crop_logger.error(f"ERROR logging finished button: {str(e)}")
        finally:
            self.current_operation = None
            
    def log_finished_button_click_memory(self, config_before_json, config_after_json):
        """[FIX #22] Log configuration changes directly from memory strings."""
        try:
            config_before = json.loads(config_before_json)
            config_after = json.loads(config_after_json)
            self.crop_logger.info("=" * 80)
            self.crop_logger.info("FINISHED BUTTON CLICKED - CONFIGURATION UPDATE (MEMORY LOG)")
            self.crop_logger.info("=" * 80)
            self.crop_logger.info("PREVIOUS EXISTING CONFIGURATION:")
            self.crop_logger.info("-" * 40)
            self.crop_logger.info(config_before_json)
            self.crop_logger.info("-" * 40)
            self.crop_logger.info("NEW JSON CONFIGURATION:")
            self.crop_logger.info("-" * 40)
            self.crop_logger.info(config_after_json)
            self.crop_logger.info("-" * 40)
            differences = self._find_config_differences(config_before, config_after)
            self.crop_logger.info("CONFIGURATION DIFFERENCES:")
            self.crop_logger.info("-" * 40)
            self.crop_logger.info(json.dumps(differences, indent=2))
            self.crop_logger.info("-" * 40)
            configured_elements = set(config_after.get('crops_1080p', {}).keys())
            self.crop_logger.info(f"Total elements now configured: {len(configured_elements)}")
            for element in sorted(configured_elements):
                self.crop_logger.info(f"  [X] {element}")
            self.crop_logger.info("-" * 40)
            self.crop_logger.info("=" * 80)
        except Exception as e:
            self.crop_logger.error(f"ERROR logging finished button from memory: {str(e)}")

    def log_error(self, error_message, context=None):
        """Log errors with context."""
        error_data = {
            'timestamp': time.time(),
            'error': error_message,
            'context': context,
            'current_operation': self.current_operation
        }
        self.crop_logger.error("=" * 60)
        self.crop_logger.error(f"ERROR DETECTED: {error_message}")
        self.crop_logger.error("-" * 40)
        self.crop_logger.error(f"Context: {context}")
        if self.current_operation:
            self.crop_logger.error(f"Current Operation: {self.current_operation.get('type', 'Unknown')}")
        self.crop_logger.error("=" * 60)
        self.crop_logger.error(f"Error details: {json.dumps(error_data, indent=2, default=str)}")
            
    def log_inconsistency(self, inconsistency_type, details):
        """Log inconsistencies in the system."""
        self.crop_logger.warning("=" * 60)
        self.crop_logger.warning(f"SYSTEM INCONSISTENCY DETECTED: {inconsistency_type}")
        self.crop_logger.warning("-" * 40)
        self.crop_logger.warning(f"Details: {details}")
        self.crop_logger.warning("=" * 60)
        
    def _rect_to_dict(self, rect):
        """Convert QRect to dictionary."""
        if rect is None:
            return None
        if isinstance(rect, QRect):
            return {
                'x': rect.x(),
                'y': rect.y(),
                'width': rect.width(),
                'height': rect.height(),
                'top_left': {'x': rect.left(), 'y': rect.top()},
                'bottom_right': {'x': rect.right(), 'y': rect.bottom()}
            }
        return rect
        
    def _rect_to_str(self, rect):
        """Convert QRect to string representation."""
        if rect is None:
            return "None"
        if isinstance(rect, QRect):
            return f"({rect.x()}, {rect.y()}) [{rect.width()}x{rect.height()}]"
        return str(rect)
        
    def _find_config_differences(self, before, after, path=""):
        """Recursively find differences between two JSON structures."""
        differences = {}
        all_keys = set(before.keys()) | set(after.keys())
        for key in all_keys:
            current_path = f"{path}.{key}" if path else key
            if key in before and key in after:
                if isinstance(before[key], dict) and isinstance(after[key], dict):
                    nested_diff = self._find_config_differences(before[key], after[key], current_path)
                    if nested_diff:
                        differences[key] = nested_diff
                elif before[key] != after[key]:
                    differences[key] = {
                        'before': before[key],
                        'after': after[key]
                    }
            elif key in before and key not in after:
                differences[key] = {
                    'before': before[key],
                    'after': 'REMOVED'
                }
            elif key not in before and key in after:
                differences[key] = {
                    'before': 'ADDED',
                    'after': after[key]
                }
        return differences
        
    def get_corner_placement(self, rect, scene_rect):
        """Determine which corner/area the rectangle is placed in."""
        if not rect or not scene_rect:
            return "unknown"
        center_x = rect.x() + rect.width() / 2
        center_y = rect.y() + rect.height() / 2
        scene_center_x = scene_rect.width() / 2
        scene_center_y = scene_rect.height() / 2
        if center_x < scene_center_x and center_y < scene_center_y:
            return "top_left"
        elif center_x >= scene_center_x and center_y < scene_center_y:
            return "top_right"
        elif center_x < scene_center_x and center_y >= scene_center_y:
            return "bottom_left"
        elif center_x >= scene_center_x and center_y >= scene_center_y:
            return "bottom_right"
        else:
            return "center"
_enhanced_logger_instance = None

def get_enhanced_logger(base_logger=None, log_dir=None):
    """Get or create enhanced logger instance."""
    global _enhanced_logger_instance
    if _enhanced_logger_instance is None and base_logger is not None:
        _enhanced_logger_instance = EnhancedCropLogger(base_logger, log_dir)
    return _enhanced_logger_instance

def setup_enhanced_logging(base_logger):
    """Setup enhanced logging system."""
    return get_enhanced_logger(base_logger)
