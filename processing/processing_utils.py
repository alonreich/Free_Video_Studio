import os
from typing import Dict, Any, Optional, List
try:
    from PyQt5.QtCore import Qt, QRect
    from PyQt5.QtGui import QImage, QPainter, QFont, QColor
    HAS_GUI = True
except ImportError:
    HAS_GUI = False

def make_multiple(n, m=8):
    i = int(round(n))
    return (i // m) * m

def make_even(n):
    return make_multiple(n, 2)

def fps_to_float(fps_val):
    from fractions import Fraction
    try:
        return float(Fraction(str(fps_val)))
    except:
        try: return float(fps_val)
        except: return 60.0

def add_drawtext_filter(filter_cmd, textfile_path, font_size, line_spacing):
    safe_path = textfile_path.replace("\\", "/").replace(":", "\\:")
    font_arg = ""
    if os.name == 'nt':
        for fpath in ["C:/Windows/Fonts/arial.ttf", "C:/Windows/Fonts/segoeui.ttf"]:
            if os.path.exists(fpath):
                safe_font = fpath.replace("\\", "/").replace(":", "\\:")
                font_arg = f":fontfile='{safe_font}'"
                break
    drawtext = f",drawtext=textfile='{safe_path}':fontcolor=white:fontsize={font_size}:x=(w-tw)/2:y=50:line_spacing={line_spacing}{font_arg}"
    return filter_cmd + drawtext

class ProgressScaler:
    def __init__(self, real_signal, start_pct, range_pct):
        self.real_signal = real_signal
        self.start_pct = start_pct
        self.range_pct = range_pct

    def emit(self, val):
        weighted_val = int(self.start_pct + (val / 100.0) * self.range_pct)
        out_val = min(100, weighted_val)
        if hasattr(self.real_signal, "emit"):
            self.real_signal.emit(out_val)
        elif callable(self.real_signal):
            self.real_signal(out_val)

def generate_text_overlay_png(text, width, height, font_size, line_spacing, output_path, config, logger):
    try:
        from .text_ops import is_pure_rtl, TextWrapper
        img = QImage(width, height, QImage.Format_ARGB32)
        img.fill(Qt.transparent)
        painter = QPainter(img)
        painter.setRenderHint(QPainter.Antialiasing)
        painter.setRenderHint(QPainter.TextAntialiasing)
        wrapper = TextWrapper(config)
        best_font_size, wrapped_lines = wrapper.fit_and_wrap(text, target_width=width-100, logger=logger)
        if logger:
            logger.info(f"TEXT_RESULT: FinalFont={best_font_size} | Rows={len(wrapped_lines)} | Content='{'/'.join(wrapped_lines)}'")
        pure_rtl = is_pure_rtl(text)
        layout_dir = Qt.RightToLeft if pure_rtl else Qt.LeftToRight
        if len(wrapped_lines) == 1:
            align_flag = Qt.AlignHCenter
        else:
            align_flag = Qt.AlignRight if pure_rtl else Qt.AlignLeft
        painter.setLayoutDirection(layout_dir)
        font = QFont("Arial", best_font_size)
        font.setBold(True)
        painter.setFont(font)
        fm = painter.fontMetrics()
        line_h = fm.height()
        block_h = (len(wrapped_lines) * line_h) + ((len(wrapped_lines) - 1) * line_spacing)
        if width > height:
            start_y = 50
        else:
            start_y = (150 - block_h) // 2
        current_y = max(10, start_y)
        for line in wrapped_lines:
            text_rect = QRect(50, current_y, width - 100, line_h)
            painter.setPen(QColor(0, 0, 0, 180))
            painter.drawText(text_rect.adjusted(3, 3, 3, 3), align_flag | Qt.AlignVCenter, line)
            painter.setPen(Qt.white)
            painter.drawText(text_rect, align_flag | Qt.AlignVCenter, line)
            current_y += (line_h + line_spacing)
        painter.end()
        img.save(output_path, "PNG")
        return True
    except Exception as e:
        if logger:
            logger.error(f"TEXT_PNG_FAIL: {e}")
        return False
