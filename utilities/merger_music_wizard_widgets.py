from PyQt5.QtWidgets import QListWidget, QWidget, QHBoxLayout, QLabel
from PyQt5.QtCore import Qt

class SearchableListWidget(QListWidget):
    """A QListWidget that ignores decorative elements during keyboard search."""

    def keyPressEvent(self, event):
        if event.text() and len(event.text()) == 1 and event.modifiers() == Qt.NoModifier:
            search_char = event.text().lower()
            for i in range(self.count()):
                item = self.item(i)
                if not item.isHidden():
                    w = self.itemWidget(item)
                    if w and hasattr(w, 'name_lbl'):
                        clean_text = w.name_lbl.text().lower()
                        if clean_text.startswith(search_char):
                            self.setCurrentItem(item)
                            self.scrollToItem(item)
                            return
        super().keyPressEvent(event)

class MusicItemWidget(QWidget):
    """Custom widget for song list items with specific font requirements."""

    def __init__(self, filename, parent=None):
        super().__init__(parent)
        layout = QHBoxLayout(self)
        layout.setContentsMargins(15, 2, 10, 2)
        layout.setSpacing(10)
        self.note_lbl = QLabel("♪")
        self.note_lbl.setStyleSheet("font-size: 18px; color: #7DD3FC; font-weight: bold;")
        self.name_lbl = QLabel(filename)
        self.name_lbl.setStyleSheet("font-size: 14px; color: #ecf0f1;")
        layout.addWidget(self.note_lbl)
        layout.addWidget(self.name_lbl, 1)
        self.setLayout(layout)
