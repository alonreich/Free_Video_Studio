from PyQt5.QtWidgets import (
    QHBoxLayout, QLabel, QPushButton, QSizePolicy, QWidget
)

from PyQt5.QtCore import Qt
from utilities.merger_unified_music_widget import UnifiedMusicWidget

class MergerUIWidgetsMixin:
    def create_center_buttons(self):
        row = QHBoxLayout()
        row.setContentsMargins(0, 0, 0, 0)
        row.setSpacing(10)
        self.parent.btn_undo = QPushButton("UNDO")
        self.parent.btn_undo.setObjectName("aux-btn")
        self.parent.btn_undo.setFixedSize(85, 32)
        self.parent.btn_undo.setCursor(Qt.PointingHandCursor)
        self.parent.btn_undo.setToolTip("Undo last action (Ctrl+Z)")
        self.parent.btn_redo = QPushButton("REDO")
        self.parent.btn_redo.setObjectName("aux-btn")
        self.parent.btn_redo.setFixedSize(85, 32)
        self.parent.btn_redo.setCursor(Qt.PointingHandCursor)
        self.parent.btn_redo.setToolTip("Redo last action (Ctrl+Y)")
        row.addWidget(self.parent.btn_undo)
        row.addWidget(self.parent.btn_redo)
        return row

    def create_merge_row(self):
        self.parent.btn_back = QPushButton("RETURN TO MAIN APP")
        self.parent.btn_back.setFixedSize(135, 40)
        self.parent.btn_back.setObjectName("returnButton")
        self.parent.btn_back.clicked.connect(self.parent.return_to_main_app)
        self.parent.btn_back.setCursor(Qt.PointingHandCursor)
        self.parent.btn_back.setToolTip("Exit merger and return to main application")
        self.parent.merge_row = QHBoxLayout()
        merge_wrap = QWidget()
        merge_wrap.setLayout(self.parent.merge_row)
        merge_wrap.setSizePolicy(QSizePolicy.Preferred, QSizePolicy.Fixed)
        self.parent.merge_row.addStretch(1)
        self.parent.btn_merge = QPushButton("MERGE VIDEOS")
        self.parent.btn_merge.setObjectName("mergeButton")
        self.parent.btn_merge.setFixedSize(210, 45)
        self.parent.btn_merge.setCursor(Qt.PointingHandCursor)
        self.parent.btn_merge.clicked.connect(self.parent.on_merge_clicked)
        self.parent.btn_merge.setToolTip("Start merging the video list (Ctrl+Enter)")
        self.parent.merge_row.addWidget(self.parent.btn_merge)
        self.parent.merge_row.addStretch(1)
        self.parent.merge_row.addWidget(self.parent.btn_back)
        self.parent.merge_row.addSpacing(60)
        return merge_wrap

    def create_music_layout(self):
        music_layout = QHBoxLayout()
        music_layout.setSpacing(15)
        self.parent.unified_music_widget = UnifiedMusicWidget(self.parent)
        music_layout.addWidget(self.parent.unified_music_widget)
        self.parent.add_music_checkbox = self.parent.unified_music_widget.toggle_button
        return music_layout
