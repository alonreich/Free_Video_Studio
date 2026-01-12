import re
from PyQt5.QtGui import QFont, QFontMetrics

def fix_hebrew_text(text: str) -> str:
    if not text:
        return ""
    has_hebrew = any("\u0590" <= c <= "\u05ff" for c in text)
    if has_hebrew:
        rev_full = text[::-1]
        
        def _flip_back_match(match):
            return match.group(0)[::-1]
        final_text = re.sub(r'[^\u0590-\u05ff]+', _flip_back_match, rev_full)
    else:
        final_text = text
    final_text = final_text.replace(":", "\\:")
    final_text = final_text.replace("'", "'\\\\''")
    return final_text

def apply_bidi_formatting(text: str) -> str:
    def replacer(m):
        return "\u2066" + m.group(1) + "\u2069"
    txt_with_ltr_numbers = re.sub(
        r'([0-9]+(?:[.,:/\-][0-9]+)*)',
        replacer,
        text
    )
    has_hebrew = any("\u0590" <= c <= "\u05ff" for c in text)
    if has_hebrew:
        return "\u2067" + txt_with_ltr_numbers + "\u200F" + "\u2069"
    else:
        return "\u2066" + txt_with_ltr_numbers + "\u2069"

class TextWrapper:
    def __init__(self, config):
        self.cfg = config
        self.MAX_LINE_W = config.wrap_at_px
        self.SAFE_MAX_W = config.safe_max_px

    def _measure_px(self, s: str, px_size: int) -> int:
        f = QFont("Arial")
        f.setPixelSize(int(px_size))
        fm = QFontMetrics(f)
        try:
            w = int(fm.horizontalAdvance(s))
        except Exception:
            w = int(fm.width(s))
        return int(w * self.cfg.measure_fudge) + self.cfg.shadow_pad_px

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