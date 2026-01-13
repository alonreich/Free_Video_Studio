from PyQt5.QtCore import QEvent, Qt, QRect, QTimer
from PyQt5.QtGui import QPainter, QColor, QFont, QPen

class EventsMixin:
    def mousePressEvent(self, event):
        """Force keyboard focus back to the main window to enable shortcuts."""
        try:
            if event.button() == Qt.LeftButton:
                self.setFocus(Qt.MouseFocusReason)
        except Exception as e:
            if hasattr(self, "logger"): self.logger.error(f"MousePress error: {e}")
        super().mousePressEvent(event)

    def eventFilter(self, obj, event):
        if obj is self and event.type() == QEvent.KeyPress and event.key() == Qt.Key_Space:
            self.toggle_play()
            return True
        if obj is getattr(self, "_overlay", None) and event.type() == QEvent.Paint:
            p = QPainter(self._overlay)
            try:
                p.setRenderHint(QPainter.Antialiasing)
                p.fillRect(self._overlay.rect(), QColor(0, 0, 0, 180))
                panel = self._graph.geometry()
                f = QFont("Consolas", 10, QFont.Bold); p.setFont(f)
                left, top = panel.left() + 110, panel.top() + 10
                right, bottom = panel.right() - 20, panel.bottom() - 10
                w, h = max(1, right - left), max(1, bottom - top)
                gap_y, bands = 20, 4
                band_h = (h - gap_y * (bands - 1)) // bands
                total_seconds = len(getattr(self, "_cpu_hist", [])) * 2
                time_str = f"{total_seconds//60:02d}:{total_seconds%60:02d}"

                def plot_band(hist, label, color, row):
                    y0 = top + row * (band_h + gap_y)
                    r_lane = QRect(left, y0, w, band_h)
                    p.setPen(QPen(QColor(255, 255, 255, 60), 2))
                    p.drawLine(left - 15, y0, left - 15, y0 + band_h + 10)
                    text_block_h = 50 
                    text_start_y = y0 + (band_h - text_block_h) // 2
                    last_val = int(list(hist)[-1]) if hist else 0
                    p.setPen(QColor(200, 200, 200))
                    p.drawText(panel.left() + 5, text_start_y + 10, label)
                    f_big = QFont("Consolas", 14, QFont.Bold); p.setFont(f_big)
                    p.setPen(color)
                    p.drawText(panel.left() + 5, text_start_y + 32, f"{last_val}%")
                    p.setFont(f)
                    p.setPen(QColor(100, 100, 100))
                    p.drawText(panel.left() + 5, text_start_y + 50, f"T: {time_str}")
                    p.setPen(QPen(QColor(255, 255, 255, 20), 1))
                    p.drawLine(left, y0 + band_h, right, y0 + band_h)
                    if row < 3:
                        sep_y = y0 + band_h + (gap_y // 2)
                        p.setPen(QPen(QColor(60, 70, 80), 1))
                        p.drawLine(panel.left(), sep_y, panel.right(), sep_y)
                    vals = list(hist) if hist else []
                    if not vals: return
                    bar_gap = 9 
                    max_bars = w // (15 + bar_gap) 
                    visible_vals = vals[-max_bars:]
                    actual_bar_w = max(15, min(25, (w // len(visible_vals)) - bar_gap)) if visible_vals else 15
                    for i, v in enumerate(visible_vals):
                        bx = left + i * (actual_bar_w + bar_gap)
                        bh = int((max(2, v) / 100.0) * band_h)
                        p.setPen(QPen(color.darker(150), 1))
                        p.setBrush(color)
                        p.drawRect(bx, r_lane.bottom() - bh, actual_bar_w, bh)
                plot_band(self._cpu_hist,  "SYSTEM CPU", QColor(0, 230, 255), 0)
                plot_band(self._gpu_hist,  "NVIDIA GPU", QColor(0, 255, 130), 1)
                plot_band(self._mem_hist,  "MEMORY USE", QColor(255, 180, 0),  2)
                plot_band(self._iops_hist, "DISK I/O",   QColor(255, 80, 80),  3)
            finally:
                p.end()
            return True
        if obj in (getattr(self, "video_frame", None), getattr(self, "video_surface", None)):
            if event.type() in (QEvent.Resize, QEvent.Move):
                try:
                    self._update_volume_badge()
                    if hasattr(self, "portrait_mask_overlay"):
                        r = self.video_surface.rect()
                        top_left = self.video_surface.mapToGlobal(r.topLeft())
                        self.portrait_mask_overlay.setGeometry(QRect(top_left, r.size()))
                        self._update_portrait_mask_overlay_state()
                except Exception as e:
                    if hasattr(self, "logger"): self.logger.error(f"EventFilter resize error: {e}")
                return False
        return super().eventFilter(obj, event)

    def resizeEvent(self, event):
        self._update_window_size_in_title()
        try:
            self._update_volume_badge()
        except Exception as e:
            if hasattr(self, "logger"): self.logger.error(f"ResizeEvent error (volume): {e}")
        try:
            if getattr(self, "_overlay", None) and self._overlay.isVisible():
                gw, gh = self._graph.width(), self._graph.height()
                x = max(10, (self._overlay.width()  - gw) // 2)
                y = max(10, int(self._overlay.height() * 0.06))
                self._graph.move(x, y)
                self._update_overlay_mask()
        except Exception as e:
            if hasattr(self, "logger"): self.logger.error(f"ResizeEvent error (overlay): {e}")
        try:
            player_container = getattr(self, 'player_col_container', None)
            trim_container = getattr(self, 'trim_container', None)
            if player_container is not None and trim_container is not None:
                player_w = player_container.width()
                video_w = self.video_frame.width()
                pad = max(0, (player_w - video_w) // 2)
                trim_container.setContentsMargins(pad, 0, pad, 0)
        except Exception as e:
            if hasattr(self, "logger"): self.logger.error(f"ResizeEvent error (layout): {e}")
        return super().resizeEvent(event)

    def _on_mobile_format_toggled(self, checked: bool):
        if hasattr(self, "logger"):
            self.logger.info("OPTION: Mobile Format -> %s", checked)
        if hasattr(self, "teammates_checkbox"):
            self.teammates_checkbox.setVisible(checked)
            self.teammates_checkbox.setEnabled(checked)
            if not checked:
                self.teammates_checkbox.setChecked(False)
        if hasattr(self, "portrait_text_input"):
            self.portrait_text_input.setVisible(checked)
            if not checked:
                self.portrait_text_input.clear()
        if hasattr(self, "_recenter_process_controls"):
            QTimer.singleShot(0, self._recenter_process_controls)
        if hasattr(self, "_update_portrait_mask_overlay_state"):
            self._update_portrait_mask_overlay_state()