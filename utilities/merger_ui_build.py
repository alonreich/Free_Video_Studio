from PyQt5.QtWidgets import (
    QWidget, QVBoxLayout, QHBoxLayout, QLabel, QPushButton, QSizePolicy
)

from PyQt5.QtCore import Qt
from utilities.merger_ui_widgets import MergerUIWidgetsMixin

class MergerUIBuildMixin(MergerUIWidgetsMixin):
    def setup_ui(self):
        root = QWidget(self.parent)
        self.parent.setCentralWidget(root)
        outer = QVBoxLayout(root)
        outer.setContentsMargins(30, 24, 30, 24)
        outer.setSpacing(20)
        title = QLabel('SORT THE VIDEOS IN THE CORRECT DESIRED ORDER')
        title.setObjectName("titleLabel")
        title.setAlignment(Qt.AlignHCenter)
        title.setSizePolicy(QSizePolicy.Preferred, QSizePolicy.Fixed)
        outer.addWidget(title)
        list_container = QHBoxLayout()
        list_container.setSpacing(12)
        outer.addLayout(list_container, 1)
        self.parent.listw = self.parent.create_draggable_list_widget()
        self.parent.listw.setSizePolicy(QSizePolicy.Expanding, QSizePolicy.Expanding)
        list_container.addWidget(self.parent.listw, 1)
        move_buttons_layout = self.create_move_buttons()
        list_container.addLayout(move_buttons_layout, 0)
        
        # New combined action row for ADD and REMOVE/CLEAR buttons
        action_buttons_row = QHBoxLayout()
        action_buttons_row.setContentsMargins(0, 0, 0, 0)
        
        # Left/Center part: ADD buttons
        action_buttons_row.addStretch(1)
        action_buttons_row.addWidget(self.parent.btn_add)
        action_buttons_row.addSpacing(20)
        action_buttons_row.addWidget(self.parent.btn_add_folder)
        action_buttons_row.addStretch(1)
        
        # Right aligned part: REMOVE and CLEAR buttons
        action_buttons_row.addWidget(self.parent.btn_remove)
        action_buttons_row.addSpacing(14)
        action_buttons_row.addWidget(self.parent.btn_clear)
        
        outer.addLayout(action_buttons_row, 0)

        bottom_band = self.create_bottom_band()
        outer.addWidget(bottom_band, 0)
        self.parent.status_label = QLabel("READY. ADD 2+ VIDEOS TO BEGIN.")
        self.parent.status_label.setStyleSheet("color: #7289da; font-weight: bold; font-size: 14px;")
        self.parent.status_label.setAlignment(Qt.AlignCenter)
        outer.addWidget(self.parent.status_label, 0)
        merge_row = self.create_merge_row()
        outer.addWidget(merge_row, 0)

    def create_move_buttons(self):
        col = QVBoxLayout()
        col.setContentsMargins(0, 0, 0, 0)
        col.setSpacing(15)
        
        # Initialize ADD buttons first (previously they were created here)
        self.parent.btn_add = QPushButton("ADD VIDEOS")
        self.parent.btn_add.setObjectName("aux-btn")
        self.parent.btn_add.setFixedSize(140, 40)
        self.parent.btn_add.setCursor(Qt.PointingHandCursor)
        self.parent.btn_add.clicked.connect(self.parent.event_handler.add_videos)
        
        self.parent.btn_add_folder = QPushButton("ADD FOLDER")
        self.parent.btn_add_folder.setObjectName("aux-btn")
        self.parent.btn_add_folder.setFixedSize(140, 40)
        self.parent.btn_add_folder.setCursor(Qt.PointingHandCursor)
        self.parent.btn_add_folder.clicked.connect(self.parent.event_handler.add_folder)

        # Initialize REMOVE and CLEAR buttons
        self.parent.btn_remove = QPushButton("REMOVE SELECTED")
        self.parent.btn_remove.setObjectName("danger-btn")
        self.parent.btn_remove.setFixedSize(140, 40)
        self.parent.btn_remove.setCursor(Qt.PointingHandCursor)
        self.parent.btn_remove.clicked.connect(self.parent.remove_selected)
        
        self.parent.btn_clear = QPushButton("CLEAR ALL")
        self.parent.btn_clear.setObjectName("danger-btn")
        self.parent.btn_clear.setFixedSize(140, 40)
        self.parent.btn_clear.setCursor(Qt.PointingHandCursor)
        self.parent.btn_clear.clicked.connect(self.parent.confirm_clear_list)

        self.parent.btn_up = QPushButton("▲")
        self.parent.btn_up.setObjectName("moveUpBtn")
        self.parent.btn_up.setToolTip("Move selected video up (Ctrl+Up)")
        self.parent.btn_up.setFixedSize(65, 60)
        self.parent.btn_up.setCursor(Qt.PointingHandCursor)
        self.parent.btn_up.clicked.connect(lambda: self.parent.move_item(-1))
        self.parent.btn_down = QPushButton("▼")
        self.parent.btn_down.setObjectName("moveDownBtn")
        self.parent.btn_down.setToolTip("Move selected video down (Ctrl+Down)")
        self.parent.btn_down.setFixedSize(65, 60)
        self.parent.btn_down.setCursor(Qt.PointingHandCursor)
        self.parent.btn_down.clicked.connect(lambda: self.parent.move_item(1))
        
        col.addStretch(1)
        col.addWidget(self.parent.btn_up, 0, Qt.AlignCenter)
        col.addWidget(self.parent.btn_down, 0, Qt.AlignCenter)
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