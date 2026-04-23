import os, sys, time, threading, logging, subprocess, traceback
from PyQt5.QtCore import *
from PyQt5.QtGui import *
from PyQt5.QtWidgets import *

class MainWindowUiHelpersBMixin:
    def _set_upload_hint_active(self, active):
        target = getattr(self, 'hint_overlay_widget', None)
        if not target or not hasattr(self, '_hint_group'): return
        if active:
            self._update_upload_hint_responsive()
            target.show(); target.raise_()
            self._hint_group.start()
        else:
            self._hint_group.stop(); target.hide()

    def _update_upload_hint_responsive(self):
        try:
            if not hasattr(self, "upload_hint_container") or self.upload_hint_container is None: return
            if not hasattr(self, "_hint_debounce_timer"):
                self._hint_debounce_timer = QTimer(self)
                self._hint_debounce_timer.setSingleShot(True)
                self._hint_debounce_timer.timeout.connect(self._do_update_upload_hint_responsive)
            self._hint_debounce_timer.start(50)
        except: pass

    def _do_update_upload_hint_responsive(self):
        if not hasattr(self, 'upload_hint_container') or self.upload_hint_container is None: return
        if getattr(self, "_updating_hint_responsive", False): return
        self._updating_hint_responsive = True
        try:
            if not self.isVisible(): return
            curr_w, curr_h = max(1, int(self.width())), max(1, int(self.height()))
            overlay = getattr(self, 'hint_overlay_widget', None)
            if overlay is None: return
            overlay_w = int(overlay.width()); overlay_h = int(overlay.height())
            if overlay_w <= 1 and hasattr(self, 'video_frame') and self.video_frame is not None:
                overlay_w = int(self.video_frame.width())
            if overlay_h <= 1 and hasattr(self, 'video_frame') and self.video_frame is not None:
                overlay_h = int(self.video_frame.height())
            overlay_w = max(1, overlay_w or curr_w); overlay_h = max(1, overlay_h or curr_h)
            ref_w = 1574.0; ref_h = 912.0; sx = curr_w / ref_w; sy = curr_h / ref_h
            scale = max(0.45, min(1.85, (sx + sy) / 2.0))
            right_safety = max(24, int(round(72.0 * scale)))
            box_w = max(180, int(round(705.0 * scale)))
            max_box_w_for_overlay = max(180, overlay_w - right_safety - max(16, int(round(34 * scale))))
            box_w = min(box_w, max_box_w_for_overlay); box_h = max(48, int(round(121.0 * scale)))
            font_size = max(12, int(round(29.0 * scale)))
            self.upload_hint_container.setFixedSize(box_w, box_h)
            self.upload_hint_container.setStyleSheet(f"#uploadHintContainer {{ background-color:#000; border:{max(2, int(round(3 * scale)))}px solid #7DD3FC; border-radius:{max(8, int(round(12 * scale)))}px;}} ")
            self.upload_hint_label.setStyleSheet(f"color:#7DD3FC;font-family:Arial;font-size:{font_size}px;font-weight:bold;background:transparent;border:none;")
            gap = max(6, int(round(18.0 * scale))); base_offset_x = int(round(182.0 * sx))
            min_arrow_block = max(16, int(round(28 * scale))); max_offset_x = max(0, overlay_w - box_w - gap - min_arrow_block - right_safety)
            offset_x = max(0, min(base_offset_x, max_offset_x))
            arrow_l_base = max(14, int(round(425.0 * scale))); arrow_s = max(14, int(round(42.0 * scale)))
            available_space = max(0, overlay_w - offset_x - box_w - gap - right_safety); arrow_l = min(arrow_l_base, available_space)
            c_h = 0; min_arrow_l = max(12, int(round(18 * scale)))
            if arrow_l < min_arrow_l:
                self.hint_group_layout.setSpacing(0); self.upload_hint_arrow.clear(); self.upload_hint_arrow.setFixedSize(0, 0); self.upload_hint_arrow.hide()
            else:
                self.hint_group_layout.setSpacing(gap)
                c_w = arrow_l + max(10, int(round(20 * scale))); c_h = arrow_s + max(16, int(round(40 * scale)))
                self.upload_hint_arrow.setFixedSize(c_w, c_h)
                pix = QPixmap(c_w, c_h); pix.fill(Qt.transparent)
                p = QPainter(pix); p.setRenderHint(QPainter.Antialiasing); p.setBrush(QColor("#7DD3FC")); p.setPen(Qt.NoPen)
                cy = c_h // 2; bh = max(8, int(round(16 * scale))); hw = max(10, int(round(min(45 * scale, arrow_l * 0.4)))); body_len = max(4, arrow_l - hw)
                p.drawRect(5, cy - (bh // 2), body_len, bh); tx = 5 + arrow_l; bx = tx - hw
                p.drawPolygon(QPolygon([QPoint(bx, cy - (arrow_s // 2)), QPoint(tx, cy), QPoint(bx, cy + (arrow_s // 2))])); p.end()
                self.upload_hint_arrow.setPixmap(pix); self.upload_hint_arrow.show()
            visual_h = max(box_h, c_h if c_h > 0 else box_h)
            try:
                if hasattr(self, 'upload_button') and self.upload_button is not None:
                    btn_pos = self.upload_button.mapToGlobal(self.upload_button.rect().center())
                    btn_local = self.hint_overlay_widget.mapFromGlobal(btn_pos)
                    target_y = int(round(btn_local.y() - (visual_h / 2.0)))
                    self.hint_centering_layout.setContentsMargins(offset_x, max(0, target_y), 0, 0)
            except:
                fallback_y = int(round((overlay_h * 0.35) - (visual_h / 2.0)))
                self.hint_centering_layout.setContentsMargins(offset_x, max(0, fallback_y), 0, 0)
        finally:
            self._updating_hint_responsive = False

    def _update_window_size_in_title(self):
        self.setWindowTitle(f"{self._base_title}  —  {self.width()}x{self.height()}")

    def _adjust_trim_margins(self):
        try:
            pc, tc, cc = getattr(self, 'player_col_container', None), getattr(self, 'trim_container', None), getattr(self, 'center_btn_container', None)
            if pc is not None and self.video_frame is not None:
                pad = max(0, (pc.width() - self.video_frame.width()) // 2)
                if tc is not None: tc.setContentsMargins(pad, 0, pad, 0)
                if cc is not None: cc.setContentsMargins(pad, 0, pad, 0)
        except Exception: pass

    def _sync_main_timeline_badges(self):
        try:
            slider = getattr(self, "positionSlider", None)
            t, b = getattr(self, "_main_time_badge_top", None), getattr(self, "_main_time_badge_bottom", None)
            if not slider or not t or not b: return
            if not slider.isVisible() or not self.isVisible(): return
            now = time.time()
            if not hasattr(self, "_last_badge_sync"): self._last_badge_sync = 0
            if now - self._last_badge_sync < 0.03: return
            self._last_badge_sync = now
            active = (getattr(slider, "_hovering_handle", None) == 'playhead' or getattr(slider, "_dragging_handle", None) == 'playhead' or slider.isSliderDown())
            if not active or not slider.isEnabled():
                t.hide(); b.hide(); return
            try:
                g_pos = slider.mapToGlobal(QPoint(slider._map_value_to_pos(slider.value()), 0))
                pos = self.mapFromGlobal(g_pos)
            except: return
            ts = slider._fmt(slider.value())
            t.setText(ts); b.setText(ts); t.adjustSize(); b.adjustSize()
            t.move(pos.x() - t.width() // 2, pos.y() - t.height() - 12)
            b.move(pos.x() - b.width() // 2, pos.y() + slider.height() + 12)
            t.show(); b.show(); t.raise_(); b.raise_()
        except Exception: pass
