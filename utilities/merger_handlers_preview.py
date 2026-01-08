from PyQt5.QtCore import QUrl, Qt
from PyQt5.QtGui import QDesktopServices

class MergerHandlersPreviewMixin:

    def preview_file(self, path: str):
        try:
            QDesktopServices.openUrl(QUrl.fromLocalFile(path))
        except Exception as e:
            self.logger.error("Preview failed: %s", e)

    def preview_clicked(self):
        try:
            btn = self.parent.sender()
            p = btn.property("path")
            if p:
                self.preview_file(str(p))
        except Exception:
            pass

    def make_item_widget(self, path: str):
        from PyQt5.QtWidgets import QWidget, QHBoxLayout, QLabel, QPushButton, QSizePolicy
        import os
        w = QWidget()
        w.setStyleSheet("background-color:#4a667a; border-radius:6px;")
        h = QHBoxLayout(w)
        h.setContentsMargins(4, 2, 4, 2)
        h.setSpacing(2)
        lbl = QLabel(os.path.basename(path))
        lbl.setObjectName("fileLabel")
        lbl.setStyleSheet("font-size:15px;")
        lbl.setToolTip(path)
        lbl.setWordWrap(False)
        lbl.setMinimumWidth(120)
        lbl.setSizePolicy(QSizePolicy.Expanding, QSizePolicy.Fixed)
        lbl.setFixedHeight(15)
        btn = QPushButton("▶  Preview  ▶")
        btn.setObjectName("playButton")
        btn.setFixedSize(120, 52)
        btn.setStyleSheet("background-color:#2c687e; color:white; border-radius:6px; font-size:12px")
        btn.setProperty("path", path)
        btn.clicked.connect(self.preview_clicked)
        h.addWidget(lbl, 1)
        h.addWidget(btn, 0, Qt.AlignRight | Qt.AlignVCenter)
        w.setFixedHeight(46)
        return w