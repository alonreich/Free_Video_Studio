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
        if not hasattr(self, '_hint_pulse_timer') and hasattr(self, '_init_upload_hint_blink'):
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
            if hasattr(self, '_hint_pulse_timer'):
                if not self._hint_pulse_timer.isActive():
                    self._hint_pulse_start_time = time.time()
                    self._hint_pulse_timer.start()
        else:
            if hasattr(self, '_hint_pulse_timer'):
                self._hint_pulse_timer.stop()
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
            self._hint_debounce_timer.start(5)
        except: pass

    def _do_update_upload_hint_responsive(self):
        if not hasattr(self, 'hint_group_container') or self.hint_group_container is None: return
        try:
            self.hint_group_container.hide()
            if hasattr(self, 'upload_hint_arrow'):
                self.upload_hint_arrow.hide()
            return
        except: pass

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
