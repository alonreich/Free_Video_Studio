from PyQt5.QtWidgets import (
    QHBoxLayout, QLabel, QPushButton, QCheckBox,
    QComboBox, QDoubleSpinBox, QSlider, QSizePolicy, QVBoxLayout, QWidget
)

from PyQt5.QtCore import Qt
from utilities.merger_unified_music_widget import UnifiedMusicWidget

class MergerUIWidgetsMixin:
    def create_center_buttons(self):
        center = QHBoxLayout()
        center.setContentsMargins(0, 0, 0, 0)
        center.setSpacing(14)
        self.parent.btn_add = QPushButton("Add Videos")
        self.parent.btn_remove = QPushButton("Remove Selected Video")
        self.parent.btn_clear = QPushButton("Remove All Videos")
        buttons = [self.parent.btn_add, self.parent.btn_remove, self.parent.btn_clear]
        for btn in buttons:
            btn.setFixedSize(155, 34)
            btn.setCursor(Qt.PointingHandCursor)
        self.parent.btn_add.setObjectName("aux-btn")
        self.parent.btn_remove.setObjectName("danger-btn")
        self.parent.btn_clear.setObjectName("danger-btn")
        center.addWidget(self.parent.btn_add)
        center.addWidget(self.parent.btn_remove)
        center.addWidget(self.parent.btn_clear)
        return center

    def create_merge_row(self):
        self.parent.btn_back = QPushButton("Return to Main App")
        self.parent.btn_back.setFixedSize(157, 24)
        self.parent.btn_back.setObjectName("returnButton")
        self.parent.btn_back.clicked.connect(self.parent.return_to_main_app)
        self.parent.btn_back.setCursor(Qt.PointingHandCursor)
        self.parent.merge_row = QHBoxLayout()
        merge_wrap = QWidget()
        merge_wrap.setLayout(self.parent.merge_row)
        merge_wrap.setSizePolicy(QSizePolicy.Preferred, QSizePolicy.Fixed)
        self.parent.merge_row.addStretch(1)
        self.parent.btn_merge = QPushButton("Merge Videos")
        self.parent.btn_merge.setObjectName("mergeButton")
        self.parent.btn_merge.setFixedSize(221, 41)
        self.parent._merge_btn_base_css = (
            "QPushButton#mergeButton {"
            "  background-color: #59A06D;"
            "  color: black;"
            "  font-weight: bold;"
            "  font-size: 16px;"
            "  border-radius: 15px;"
            "  padding: 6px 20px;"
            "  border: 2px solid #3d7a4d;"
            "}"
            "QPushButton#mergeButton:hover {"
            "  background-color: #6bb47d;"
            "  border: 2px solid #4d8a5d;"
            "}"
            "QPushButton#mergeButton:pressed {"
            "  background-color: #4d8a5d;"
            "  border: 2px solid #3d7a4d;"
            "}"
            "QPushButton#mergeButton:disabled {"
            "  background-color: #7a8a7d;"
            "  color: #555555;"
            "  border: 2px solid #6a7a6d;"
            "}"
        )
        self.parent.btn_merge.setStyleSheet(self.parent._merge_btn_base_css)
        self.parent.btn_merge.setCursor(Qt.PointingHandCursor)
        self.parent.btn_merge.clicked.connect(self.parent.on_merge_clicked)
        self.parent.merge_row.addWidget(self.parent.btn_merge)
        self.parent.merge_row.addStretch(1)
        self.parent.merge_row.addWidget(self.parent.btn_back)
        return merge_wrap

    def create_music_layout(self):
        """Create unified music widget layout."""
        music_layout = QHBoxLayout()
        music_layout.setSpacing(15)
        self.parent.unified_music_widget = UnifiedMusicWidget(self.parent)
        music_layout.addWidget(self.parent.unified_music_widget)
        self.parent.add_music_checkbox = self.parent.unified_music_widget.toggle_button
        self.parent.music_combo = self.parent.unified_music_widget.track_combo
        self.parent.music_offset_input = self.parent.unified_music_widget.offset_spin
        self.parent.music_volume_slider = self.parent.unified_music_widget.volume_slider
        self.parent.music_volume_label = self.parent.unified_music_widget.volume_label
        return music_layout

    def create_music_slider(self):
        """Legacy method kept for compatibility - returns empty layout."""
        music_slider_box = QVBoxLayout()
        music_slider_box.setSpacing(2)
        return music_slider_box
