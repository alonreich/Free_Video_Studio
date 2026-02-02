from PyQt5.QtWidgets import (
    QWidget, QVBoxLayout, QHBoxLayout, QLabel, QPushButton, QSizePolicy
)
from PyQt5.QtCore import Qt
from utilities.merger_ui_widgets import MergerUIWidgetsMixin

class MergerUIBuildMixin(MergerUIWidgetsMixin):
    def setup_ui(self):
        root = QWidget(self.parent)
        self.parent.setCentralWidget(root)
        
        # Fix #36: Increased margins
        outer = QVBoxLayout(root)
        outer.setContentsMargins(30, 24, 30, 24)
        outer.setSpacing(20)
        
        title = QLabel('Sort the Videos in the Correct Desired Order')
        title.setObjectName("titleLabel")
        title.setAlignment(Qt.AlignHCenter)
        title.setSizePolicy(QSizePolicy.Preferred, QSizePolicy.Fixed)
        outer.addWidget(title)
        
        list_container = QHBoxLayout()
        list_container.setSpacing(12)
        outer.addLayout(list_container, 1)
        
        self.parent.listw = self.parent.create_draggable_list_widget()
        # Fix #67: Flexible width instead of fixed
        self.parent.listw.setSizePolicy(QSizePolicy.Expanding, QSizePolicy.Expanding)
        list_container.addWidget(self.parent.listw, 1)
        
        move_buttons_layout = self.create_move_buttons()
        list_container.addLayout(move_buttons_layout, 0)
        
        bottom_band = self.create_bottom_band()
        outer.addWidget(bottom_band, 0)
        
        self.parent.status_label = QLabel("Ready. Add 2+ videos to begin.")
        self.parent.status_label.setStyleSheet("color: #7289da; font-weight: bold; font-size: 14px;")
        self.parent.status_label.setAlignment(Qt.AlignCenter)
        outer.addWidget(self.parent.status_label, 0)
        
        merge_row = self.create_merge_row()
        outer.addWidget(merge_row, 0)

    def create_move_buttons(self):
        col = QVBoxLayout()
        col.setContentsMargins(0, 0, 0, 0)
        col.setSpacing(15)
        
        self.parent.btn_up = QPushButton("▲")
        self.parent.btn_up.setObjectName("moveUpBtn")
        self.parent.btn_up.setToolTip("Move selected video up (Ctrl+Up)")
        self.parent.btn_up.setFixedSize(50, 60)
        self.parent.btn_up.setCursor(Qt.PointingHandCursor)
        self.parent.btn_up.clicked.connect(lambda: self.parent.move_item(-1))
        
        self.parent.btn_down = QPushButton("▼")
        self.parent.btn_down.setObjectName("moveDownBtn")
        self.parent.btn_down.setToolTip("Move selected video down (Ctrl+Down)")
        self.parent.btn_down.setFixedSize(50, 60)
        self.parent.btn_down.setCursor(Qt.PointingHandCursor)
        self.parent.btn_down.clicked.connect(lambda: self.parent.move_item(1))
        
        col.addStretch(1)
        col.addWidget(self.parent.btn_up)
        col.addWidget(self.parent.btn_down)
        col.addStretch(1)
        return col

    def create_bottom_band(self):
        band = QHBoxLayout()
        band.setContentsMargins(0, 10, 0, 10)
        
        left_wrap = QWidget()
        left_wrap.setLayout(self.create_music_layout())
        
        center_wrap = QWidget()
        center_wrap.setLayout(self.create_center_buttons())
        
        band.addWidget(left_wrap, 0, Qt.AlignLeft)
        band.addStretch(1)
        band.addWidget(center_wrap, 0, Qt.AlignRight)
        
        wrap = QWidget()
        wrap.setLayout(band)
        return wrap