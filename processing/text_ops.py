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

def safe_text(text: str) -> str:
    """
    Sanitizes text for FFmpeg filter chains.
    """
    if not text:
        return ""
    text = str(text)
    text = text.replace("\\", "\\\\") 
    text = text.replace("'", "'\''")
    text = text.replace(":", "\\:")
    return text

def fix_hebrew_text(text: str) -> str:
    """
    [DEPRECATED] Legacy fallback. 
    Use apply_bidi_formatting which handles this correctly.
    """
    if not text:
        return ""
    return text[::-1]

def apply_bidi_formatting(text: str) -> str:
    """
    Applies Bidirectional algorithm to text to ensure correct display of RTL languages.
    [FIX] handles mixed content (Hebrew + Numbers/English) without flipping LTR chunks.
    """
    if not text:
        return ""
    if HAS_BIDI_LIB:
        try:
            return "\n".join([get_display(line) for line in text.split('\n')])
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
            return int(w) + self.cfg.shadow_pad_px
        if self.use_pil:
            try:
                font = ImageFont.truetype("arial.ttf", int(px_size))
                if hasattr(font, 'getlength'):
                    return int(font.getlength(s)) + self.cfg.shadow_pad_px
                return font.getsize(s)[0] + self.cfg.shadow_pad_px
            except Exception:
                pass
        return int(len(s) * (px_size * 0.55)) + self.cfg.shadow_pad_px

    def _split_long_token(self, tok: str, px_size: int):
        chunks = []
        cur = ""
        for ch in tok:
            cand = cur + ch
            width = self._measure_px(cand, px_size)
            if cur and width > self.MAX_LINE_W:
                chunks.append(cur)
                cur = ch
            else:
                cur = cand
        if cur:
            chunks.append(cur)
        return chunks

    def _wrap_text(self, s: str, px_size: int):
        tokens = []
        raw_tokens = (s or "").split()
        for t in raw_tokens:
            if self._measure_px(t, px_size) > self.MAX_LINE_W:
                sub_tokens = self._split_long_token(t, px_size)
                tokens.extend(sub_tokens)
            else:
                tokens.append(t)
        lines = []
        cur = ""
        for t in tokens:
            cand = t if not cur else (cur + " " + t)
            if not cur or self._measure_px(cand, px_size) <= self.MAX_LINE_W:
                cur = cand
            else:
                lines.append(cur)
                cur = t
        if cur:
            lines.append(cur)
        return lines if lines else [""]

    def fit_and_wrap(self, s: str):
        size = self.cfg.base_font_size
        for _ in range(25):
            lines = self._wrap_text(s, size)
            widest = 0
            if lines:
                widest = max(self._measure_px(ln, size) for ln in lines)
            if widest <= self.MAX_LINE_W and len(lines) <= 2:
                return size, lines
            ratio = (widest / float(self.MAX_LINE_W)) if self.MAX_LINE_W else 1.25
            ratio = max(1.08, ratio)
            penalty = max(0, len(lines) - 2) * 5
            new_size = int(max(self.cfg.min_font_size, (size / ratio) - penalty))
            if new_size >= size:
                new_size = size - 2
            if new_size <= self.cfg.min_font_size:
                break
            size = new_size
        size = max(self.cfg.min_font_size, int(size))
        lines = self._wrap_text(s, size)
        return size, lines