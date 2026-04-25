import os, sys, time, threading, logging, subprocess, traceback
from PyQt5.QtCore import *
from PyQt5.QtGui import *
from PyQt5.QtWidgets import *
from system import diagnostic_runtime

class MainWindowUiHelpersBMixin:
    def _set_upload_hint_active(self, active):
        target = getattr(self, 'hint_overlay_widget', None)
        if not target: return
        hint_group_container = getattr(self, 'hint_group_container', None)
        if not hasattr(self, '_hint_group') and hasattr(self, '_init_upload_hint_blink'):
            try:
                self._init_upload_hint_blink()
            except Exception:
                pass
        preview_notice_active = self._update_diagnostic_preview_notice()
        if active or preview_notice_active:
            self._update_upload_hint_responsive()
        if active:
            if hint_group_container is not None:
                hint_group_container.show()
            target.show(); target.raise_()
            if hasattr(self, '_hint_group'):
                self._hint_group.start()
        else:
            if hasattr(self, '_hint_group'):
                self._hint_group.stop()
            if hint_group_container is not None:
                hint_group_container.setVisible(False)
            if preview_notice_active:
                target.show(); target.raise_()
            else:
                target.hide()

    def _update_diagnostic_preview_notice(self):
        container = getattr(self, 'preview_notice_container', None)
        if container is None:
            return False
        container.hide()
        return False

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
            notice = getattr(self, 'preview_notice_container', None)
            if notice is not None:
                notice_w = min(max(260, int(round(540.0 * scale))), max(220, overlay_w - max(24, int(round(120.0 * scale)))))
                notice.setFixedWidth(notice_w)
                notice.setStyleSheet(
                    f"#previewIsolationContainer {{"
                    f"background-color: rgba(0, 0, 0, 215);"
                    f"border: {max(2, int(round(2 * scale)))}px solid #f39c12;"
                    f"border-radius: {max(10, int(round(14 * scale)))}px;"
                    f"}}"
                )
                title = getattr(self, 'preview_notice_title', None)
                detail = getattr(self, 'preview_notice_detail', None)
                if title is not None:
                    title.setStyleSheet(
                        f"color:#f8c471;font-size:{max(13, int(round(22.0 * scale)))}px;"
                        f"font-weight:bold;background:transparent;border:none;"
                    )
                if detail is not None:
                    detail.setStyleSheet(
                        f"color:#ecf0f1;font-size:{max(10, int(round(14.0 * scale)))}px;"
                        f"background:transparent;border:none;"
                    )
                notice.setVisible(self._update_diagnostic_preview_notice())
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
            t_c = getattr(self, "_top_badge_container", None)
            b_c = getattr(self, "_bottom_badge_container", None)
            t, b = getattr(self, "_main_time_badge_top", None), getattr(self, "_main_time_badge_bottom", None)
            if not slider or not t_c or not b_c or not t or not b: return
            if not hasattr(self, "_badge_fade_timer"):
                slider.sliderPressed.connect(self._sync_main_timeline_badges)
                self._badge_fade_timer = QTimer(self)
                self._badge_fade_timer.setSingleShot(True)
                self._badge_fade_timer.timeout.connect(self._start_badge_fade_out)
                self._top_fade_anim = QPropertyAnimation(self._top_badge_opacity, b"opacity")
                self._top_fade_anim.setDuration(1000) 
                self._top_fade_anim.setStartValue(0.75)
                self._top_fade_anim.setEndValue(0.0)
                self._top_fade_anim.finished.connect(lambda: t_c.hide() if self._top_badge_opacity.opacity() < 0.05 else None)
                self._bottom_fade_anim = QPropertyAnimation(self._bottom_badge_opacity, b"opacity")
                self._bottom_fade_anim.setDuration(1000)
                self._bottom_fade_anim.setStartValue(0.75)
                self._bottom_fade_anim.setEndValue(0.0)
                self._bottom_fade_anim.finished.connect(lambda: b_c.hide() if self._bottom_badge_opacity.opacity() < 0.05 else None)
            is_seeking = getattr(self, "_is_seeking_active", False)
            is_interacting = (getattr(slider, "_hovering_handle", None) == 'playhead' or 
                             getattr(slider, "_dragging_handle", None) == 'playhead' or 
                             slider.isSliderDown())
            has_video = getattr(self, "input_file_path", None) is not None
            if not has_video or not slider.isVisible() or not self.isVisible():
                t_c.hide(); b_c.hide(); return
            if is_interacting or is_seeking:
                self._top_fade_anim.stop(); self._bottom_fade_anim.stop()
                self._top_badge_opacity.setOpacity(0.75)
                self._bottom_badge_opacity.setOpacity(0.75)
                if not t_c.isVisible():
                    t_c.show(); b_c.show()
                t_c.raise_(); b_c.raise_()
                self._badge_fade_timer.start(1000)
            if t_c.isVisible():
                try:
                    val_x = slider._map_value_to_pos(slider.value())
                    groove_y_center = slider.height() // 2
                    g_pos = slider.mapToGlobal(QPoint(val_x, groove_y_center))
                    pos = self.mapFromGlobal(g_pos)
                    ts = slider._fmt(slider.value())
                    t.setText(ts); b.setText(ts); t.adjustSize(); b.adjustSize()
                    t_c.adjustSize(); b_c.adjustSize()
                    v_offset = 26 
                    t_c.move(int(pos.x() - t_c.width() // 2), int(pos.y() - v_offset - t_c.height()))
                    b_c.move(int(pos.x() - b.width() // 2), int(pos.y() + v_offset))
                except: pass
        except Exception: pass

    def _start_badge_fade_out(self):
        try:
            if hasattr(self, "_top_fade_anim"):
                self._top_fade_anim.start()
                self._bottom_fade_anim.start()
        except: pass
