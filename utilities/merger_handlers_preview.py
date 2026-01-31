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
        from PyQt5.QtWidgets import QWidget, QHBoxLayout, QLabel, QPushButton, QSizePolicy, QFrame
        import os
        w = QWidget()
        w.setStyleSheet("background-color:transparent;")
        h = QHBoxLayout(w)
        h.setContentsMargins(0, 0, 0, 0)
        h.setSpacing(0)
        frame = QFrame()
        frame.setObjectName("videoItemFrame")
        frame.setFixedWidth(700)
        frame.setStyleSheet("QFrame#videoItemFrame { background-color:#4a667a; border-radius:6px; }")
        frame_layout = QHBoxLayout(frame)
        frame_layout.setContentsMargins(12, 2, 4, 2)
        frame_layout.setSpacing(2)
        lbl = QLabel(os.path.basename(path))
        lbl.setObjectName("fileLabel")
        lbl.setStyleSheet("font-size:13px; color: white; background-color: transparent;")
        lbl.setToolTip(path)
        lbl.setWordWrap(False)
        lbl.setMinimumWidth(120)
        lbl.setSizePolicy(QSizePolicy.Expanding, QSizePolicy.Fixed)
        lbl.setFixedHeight(24)
        btn = QPushButton("▶  Preview  ▶")
        btn.setObjectName("playButton")
        btn.setFixedSize(110, 36)
        btn.setStyleSheet("background-color:#2c687e; color:white; border-radius:6px; font-size:12px; margin-right: 2px; padding: 1px;")
        btn.setProperty("path", path)
        btn.clicked.connect(self.preview_clicked)
        frame_layout.addWidget(lbl, 1)
        frame_layout.addWidget(btn, 0, Qt.AlignRight | Qt.AlignVCenter)
        h.addWidget(frame)
        w.setFixedHeight(40)
        w.video_frame = frame
        return w
