import re
import os
try:
    from PyQt5.QtGui import QFont, QFontMetrics, QGuiApplication
    HAS_QT = True
except ImportError:
    HAS_QT = False
try:
    from PIL import ImageFont
    HAS_PIL = True
except ImportError:
    HAS_PIL = False
try:
    from bidi.algorithm import get_display
    HAS_BIDI_LIB = True
except ImportError:
    HAS_BIDI_LIB = False

def is_pure_rtl(text: str) -> bool:
    if not text:
        return False
    has_rtl = any("\u0590" <= c <= "\u06ff" for c in text)
    has_latin = any(c.isalpha() and c <= "\u007f" for c in text)
    return has_rtl and not has_latin

def fix_hebrew_text(text: str) -> str:
    if not text:
        return ""
    return text[::-1]

def apply_bidi_formatting(text: str) -> str:
    if not text:
        return ""
    if HAS_BIDI_LIB:
        try:
            pure_rtl = is_pure_rtl(text)
            base_dir = 'R' if pure_rtl else 'L'
            return "\n".join([get_display(line, base_dir=base_dir) for line in text.split('\n')])
        except Exception:
            pass

    def _reverse_line_robust(line: str) -> str:
        if not line: return ""
        if not any("\u0590" <= c <= "\u06ff" for c in line):
            return line
        blocks = re.findall(r'([\u0590-\u06ff]+|[^\u0590-\u06ff]+)', line)
        processed_blocks = []
        for b in blocks:
            if any("\u0590" <= c <= "\u06ff" for c in b):
                processed_blocks.append(b[::-1])
            else:
                processed_blocks.append(b)
        return "".join(processed_blocks[::-1])
    return "\n".join([_reverse_line_robust(line) for line in text.split('\n')])

class TextWrapper:
    def __init__(self, config):
        self.cfg = config
        self.MAX_LINE_W = config.wrap_at_px
        self.SAFE_MAX_W = config.safe_max_px
        self.use_qt = False
        self.use_pil = False
        if HAS_QT and QGuiApplication.instance():
            self.use_qt = True
        elif HAS_PIL:
            self.use_pil = True
            try:
                self._pil_font_path = "arial.ttf" 
            except:
                pass
        
    def _measure_px(self, s: str, px_size: int) -> int:
        if not s: return 0
        if self.use_qt:
            f = QFont("Arial")
            f.setPixelSize(int(px_size))
            fm = QFontMetrics(f)
            try:
                w = int(fm.horizontalAdvance(s))
            except Exception:
                w = int(fm.width(s))
            return int(w * 1.05) + self.cfg.shadow_pad_px
        if self.use_pil:
            try:
                font = ImageFont.truetype("arial.ttf", int(px_size))
                if hasattr(font, 'getlength'):
                    return int(font.getlength(s) * 1.05) + self.cfg.shadow_pad_px
                return int(font.getsize(s)[0] * 1.05) + self.cfg.shadow_pad_px
            except Exception:
                pass
        return int(len(s) * (px_size * 0.6)) + self.cfg.shadow_pad_px

    def _split_long_token(self, tok: str, px_size: int, max_w: int):
        chunks = []
        cur = ""
        for ch in tok:
            cand = cur + ch
            width = self._measure_px(cand, px_size)
            if cur and width > max_w:
                chunks.append(cur)
                cur = ch
            else:
                cur = cand
        if cur:
            chunks.append(cur)
        return chunks

    def _wrap_text(self, s: str, px_size: int, max_w: int):
        tokens = []
        raw_tokens = (s or "").split()
        for t in raw_tokens:
            if self._measure_px(t, px_size) > max_w:
                sub_tokens = self._split_long_token(t, px_size, max_w)
                tokens.extend(sub_tokens)
            else:
                tokens.append(t)
        lines = []
        cur = ""
        for t in tokens:
            cand = t if not cur else (cur + " " + t)
            if not cur or self._measure_px(cand, px_size) <= max_w:
                cur = cand
            else:
                lines.append(cur)
                cur = t
        if cur:
            lines.append(cur)
        return lines if lines else [""]

    def fit_and_wrap(self, s: str, target_width: int = None, logger=None):
        max_w = target_width if target_width else self.MAX_LINE_W
        size = self.cfg.base_font_size
        MAX_TOTAL_H = 135
        if logger: logger.info(f"WRAP_START: text='{s}' target_w={max_w} base_size={size}")
        best_size = size
        best_lines = [s]
        for i in range(40):
            lines = self._wrap_text(s, size, max_w)
            widest = 0
            if lines:
                widest = max(self._measure_px(ln, size) for ln in lines)
            total_h = len(lines) * size * 1.25
            if logger: logger.info(f"WRAP_ITER_{i}: size={size} lines={len(lines)} widest={widest} total_h={total_h:.1f}")
            if widest <= max_w and total_h <= MAX_TOTAL_H and len(lines) <= 3:
                if logger: logger.info(f"WRAP_SUCCESS: final_size={size} rows={len(lines)}")
                return size, lines
            ratio_w = (widest / float(max_w)) if max_w else 1.1
            ratio_h = (total_h / float(MAX_TOTAL_H)) if MAX_TOTAL_H else 1.1
            ratio = max(ratio_w, ratio_h, 1.04)
            new_size = int(size / ratio)
            if new_size >= size:
                new_size = size - 1
            if new_size < self.cfg.min_font_size:
                size = self.cfg.min_font_size
                break
            size = new_size
        final_size = max(self.cfg.min_font_size, int(size))
        final_lines = self._wrap_text(s, final_size, max_w)
        if logger: logger.info(f"WRAP_LIMIT: forced_size={final_size} rows={len(final_lines)}")
        return final_size, final_lines
