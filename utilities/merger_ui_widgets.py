from PyQt5.QtWidgets import (
    QHBoxLayout, QLabel, QPushButton, QSizePolicy, QWidget
)
from PyQt5.QtCore import Qt
from utilities.merger_unified_music_widget import UnifiedMusicWidget

class MergerUIWidgetsMixin:
    def create_center_buttons(self):
        center = QHBoxLayout()
        center.setContentsMargins(0, 0, 0, 0)
        center.setSpacing(14)
        
        self.parent.btn_add = QPushButton("Add Videos")
        self.parent.btn_add.setObjectName("aux-btn")
        self.parent.btn_add.setToolTip("Add video files to the end of the list")
        
        # Fix #14: Add Folder Button
        self.parent.btn_add_folder = QPushButton("Add Folder")
        self.parent.btn_add_folder.setObjectName("aux-btn")
        self.parent.btn_add_folder.setToolTip("Add all videos from a folder")
        self.parent.btn_add_folder.clicked.connect(self.parent.event_handler.add_folder)

        self.parent.btn_remove = QPushButton("Remove Selected")
        self.parent.btn_remove.setObjectName("danger-btn")
        self.parent.btn_remove.setToolTip("Remove selected videos from the list (Delete key)")
        
        self.parent.btn_clear = QPushButton("Clear All")
        self.parent.btn_clear.setObjectName("danger-btn")
        self.parent.btn_clear.setToolTip("Remove all videos from the list")
        
        # Standardized button sizes
        for btn in [self.parent.btn_add, self.parent.btn_add_folder, self.parent.btn_remove, self.parent.btn_clear]:
            btn.setFixedSize(140, 40) # Slightly smaller to fit 4
            btn.setCursor(Qt.PointingHandCursor)
            
        center.addWidget(self.parent.btn_add)
        center.addWidget(self.parent.btn_add_folder)
        center.addWidget(self.parent.btn_remove)
        center.addWidget(self.parent.btn_clear)
        return center

    def create_merge_row(self):
        self.parent.btn_back = QPushButton("Return to Menu")
        # Fix #24: Standardize return button
        self.parent.btn_back.setFixedSize(160, 40)
        self.parent.btn_back.setObjectName("returnButton")
        self.parent.btn_back.clicked.connect(self.parent.return_to_main_app)
        self.parent.btn_back.setCursor(Qt.PointingHandCursor)
        self.parent.btn_back.setToolTip("Exit merger and return to main application")
        
        self.parent.merge_row = QHBoxLayout()
        merge_wrap = QWidget()
        merge_wrap.setLayout(self.parent.merge_row)
        merge_wrap.setSizePolicy(QSizePolicy.Preferred, QSizePolicy.Fixed)
        
        self.parent.merge_row.addStretch(1)
        
        self.parent.btn_merge = QPushButton("Merge Videos")
        self.parent.btn_merge.setObjectName("mergeButton")
        self.parent.btn_merge.setFixedSize(240, 50) # Larger hero button
        self.parent.btn_merge.setCursor(Qt.PointingHandCursor)
        self.parent.btn_merge.clicked.connect(self.parent.on_merge_clicked)
        # Fix #72: Add tooltip
        self.parent.btn_merge.setToolTip("Start merging the video list (Ctrl+Enter)")
        
        self.parent.merge_row.addWidget(self.parent.btn_merge)
        self.parent.merge_row.addStretch(1)
        self.parent.merge_row.addWidget(self.parent.btn_back)
        return merge_wrap

    def create_music_layout(self):
        music_layout = QHBoxLayout()
        music_layout.setSpacing(15)
        self.parent.unified_music_widget = UnifiedMusicWidget(self.parent)
        music_layout.addWidget(self.parent.unified_music_widget)
        
        # Backward compatibility map
        self.parent.add_music_checkbox = self.parent.unified_music_widget.toggle_button
        self.parent.music_combo = self.parent.unified_music_widget.track_combo
        self.parent.music_offset_input = self.parent.unified_music_widget.offset_spin
        self.parent.music_volume_slider = self.parent.unified_music_widget.volume_slider
        
        return music_layout