from PyQt5.QtWidgets import QListWidget, QWidget, QHBoxLayout, QLabel
from PyQt5.QtCore import Qt, QTimer

class SearchableListWidget(QListWidget):
    """A QListWidget that ignores decorative elements during keyboard search and supports multi-char type-ahead."""

    def __init__(self, parent=None):
        super().__init__(parent)
        self._search_buffer = ""
        self._buffer_timer = QTimer(self)
        self._buffer_timer.setSingleShot(True)
        self._buffer_timer.setInterval(1500)
        self._buffer_timer.timeout.connect(self._clear_buffer)

    def _clear_buffer(self):
        self._search_buffer = ""

    def keyPressEvent(self, event):
        text = event.text()
        if text and len(text) == 1 and event.modifiers() == Qt.NoModifier:
            self._buffer_timer.stop()
            self._search_buffer += text.lower()
            self._buffer_timer.start()
            for i in range(self.count()):
                item = self.item(i)
                if not item.isHidden():
                    w = self.itemWidget(item)
                    if w and hasattr(w, 'name_lbl'):
                        clean_text = w.name_lbl.text().lower()
                        if clean_text.startswith(self._search_buffer):
                            self.setCurrentItem(item)
                            self.scrollToItem(item)
                            return
            if len(self._search_buffer) > 1:
                self._search_buffer = text.lower()
                for i in range(self.count()):
                    item = self.item(i)
                    if not item.isHidden():
                        w = self.itemWidget(item)
                        if w and hasattr(w, 'name_lbl'):
                            clean_text = w.name_lbl.text().lower()
                            if clean_text.startswith(self._search_buffer):
                                self.setCurrentItem(item)
                                self.scrollToItem(item)
                                return
            return
        super().keyPressEvent(event)

class MusicItemWidget(QWidget):
    """Custom widget for song list items with specific font requirements."""

    def __init__(self, filename, parent=None):
        super().__init__(parent)
        self.setAttribute(Qt.WA_NoSystemBackground)
        self.setStyleSheet("background: transparent; border: none;")
        layout = QHBoxLayout(self)
        layout.setContentsMargins(15, 2, 10, 2)
        layout.setSpacing(10)
        self.note_lbl = QLabel("♪")
        self.note_lbl.setStyleSheet("font-size: 18px; color: #7DD3FC; font-weight: bold; background: transparent;")
        self.name_lbl = QLabel(filename)
        self.name_lbl.setStyleSheet("font-size: 14px; color: #ecf0f1; background: transparent;")
        layout.addWidget(self.note_lbl)
        layout.addWidget(self.name_lbl, 1)
        self.setLayout(layout)
