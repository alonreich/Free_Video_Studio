from PyQt5.QtWidgets import (
    QHBoxLayout, QLabel, QPushButton, QCheckBox,
    QComboBox, QDoubleSpinBox, QSlider, QSizePolicy, QVBoxLayout, QWidget
)

from PyQt5.QtCore import Qt

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
        self.parent.merge_row = QHBoxLayout()
        merge_wrap = QWidget()
        merge_wrap.setLayout(self.parent.merge_row)
        merge_wrap.setSizePolicy(QSizePolicy.Preferred, QSizePolicy.Fixed)
        self.parent.merge_row.addStretch(1)
        self.parent.btn_merge = QPushButton("Merge Videos")
        self.parent.btn_merge.setObjectName("mergeButton")
        self.parent.btn_merge.setFixedSize(221, 41)
        self.parent._merge_btn_base_css = (
            "background-color: #59A06D;"
            "color: black;"
            "font-weight: bold;"
            "font-size: 16px;"
            "border-radius: 15px;"
            "padding: 6px 20px;"
        )
        self.parent.btn_merge.setStyleSheet(self.parent._merge_btn_base_css)
        self.parent.btn_merge.clicked.connect(self.parent.on_merge_clicked)
        self.parent.merge_row.addWidget(self.parent.btn_merge)
        self.parent.merge_row.addStretch(1)
        self.parent.merge_row.addWidget(self.parent.btn_back)
        return merge_wrap

    def create_music_layout(self):
        music_layout = QHBoxLayout()
        music_layout.setSpacing(15)
        self.parent.add_music_checkbox = QCheckBox("Add Background Music")
        self.parent.add_music_checkbox.setToolTip("Toggle background MP3 mixing from the ./mp3 folder.")
        self.parent.add_music_checkbox.setChecked(False)
        music_layout.addWidget(self.parent.add_music_checkbox)
        self.parent.music_combo = QComboBox()
        try:
            self.parent.music_combo.setElideMode(Qt.ElideMiddle)
        except Exception:
            pass
        self.parent.music_combo.setSizePolicy(QSizePolicy.Preferred, QSizePolicy.Fixed)
        self.parent.music_combo.setMinimumWidth(400)
        self.parent.music_combo.setMaximumWidth(400)
        self.parent.music_combo.setMinimumContentsLength(24)
        self.parent.music_combo.setSizeAdjustPolicy(QComboBox.AdjustToMinimumContentsLengthWithIcon)
        self.parent.music_combo.setVisible(False)
        music_layout.addWidget(self.parent.music_combo)
        self.parent.music_offset_input = QDoubleSpinBox()
        self.parent.music_offset_input.setPrefix("Music Start (s): ")
        self.parent.music_offset_input.setMinimumWidth(180)
        self.parent.music_offset_input.setMaximumWidth(180)
        self.parent.music_offset_input.setDecimals(2)
        self.parent.music_offset_input.setSingleStep(0.5)
        self.parent.music_offset_input.setRange(0.0, 0.0)
        self.parent.music_offset_input.setValue(0.0)
        self.parent.music_offset_input.setVisible(False)
        music_layout.addWidget(self.parent.music_offset_input)
        music_slider_box = self.create_music_slider()
        music_layout.addLayout(music_slider_box)
        return music_layout

    def create_music_slider(self):
        self.parent.music_volume_slider = QSlider(Qt.Vertical, self.parent)
        self.parent.music_volume_slider.setObjectName("musicVolumeSlider")
        self.parent.music_volume_slider.setRange(0, 100)
        self.parent.music_volume_slider.setTickInterval(1)
        self.parent.music_volume_slider.setTracking(True)
        self.parent.music_volume_slider.setVisible(False)
        self.parent.music_volume_slider.setFocusPolicy(Qt.NoFocus)
        self.parent.music_volume_slider.setMinimumHeight(150)
        self.parent.music_volume_slider.setInvertedAppearance(True)
        eff_default = int(25)
        self.parent.music_volume_slider.setValue(eff_default)
        self.parent.music_volume_slider.setStyleSheet(f"""
        QSlider#musicVolumeSlider::groove:vertical {{
            border: 1px solid #1f2a36;
            background: qlineargradient(x1:0, y1:1, x2:0, y2:0,
                stop:0   #e64c4c,
                stop:0.25 #f7a8a8,
                stop:0.50 #f2f2f2,
                stop:0.75 #7bcf43,
                stop:1   #009b00);
            width: 30px;
            border-radius: 3px;
        }}
        QSlider#musicVolumeSlider::handle:vertical {{
            background: qlineargradient(x1:0, y1:0, x2:0, y2:1,
                stop:0 #455A64,
                stop:0.40 #455A64,
                stop:0.42 #90A4AE, stop:0.44 #90A4AE,
                stop:0.46 #455A64,
                stop:0.48 #455A64,
                stop:0.50 #90A4AE, stop:0.52 #90A4AE,
                stop:0.54 #455A64,
                stop:0.56 #455A64,
                stop:0.58 #90A4AE, stop:0.60 #90A4AE,
                stop:0.62 #455A64, stop:1 #455A64);
            border: 1px solid #1f2a36;
            width: 36px; 
            height: 40px; 
            margin: 0 -3px;
            border-radius: 4px;
        }}
        QSlider#musicVolumeSlider::handle:vertical:hover {{
            border: 1px solid #90A4AE;
            background: qlineargradient(x1:0, y1:0, x2:0, y2:1,
                stop:0 #546E7A,
                stop:0.40 #546E7A, stop:0.42 #CFD8DC, stop:0.44 #CFD8DC,
                stop:0.46 #546E7A, stop:0.48 #546E7A, stop:0.50 #CFD8DC, stop:0.52 #CFD8DC,
                stop:0.54 #546E7A, stop:0.56 #546E7A, stop:0.58 #CFD8DC, stop:0.60 #CFD8DC,
                stop:0.62 #546E7A, stop:1 #546E7A);
        }}
        QSlider#musicVolumeSlider::sub-page:vertical,
        QSlider#musicVolumeSlider::add-page:vertical {{ background: transparent;
        }}
        """)
        self.parent.music_volume_label = QLabel(f"{eff_default}%")
        self.parent.music_volume_label.setAlignment(Qt.AlignHCenter)
        self.parent.music_volume_label.setVisible(False)
        self.parent.music_volume_badge = QLabel(f"{eff_default}%", self.parent)
        self.parent.music_volume_badge.setObjectName("musicVolumeBadge")
        self.parent.music_volume_badge.setStyleSheet(
            "color: white; background: rgba(0,0,0,160); padding: 2px 6px; "
            "border-radius: 6px; font-weight: bold;"
        )
        self.parent.music_volume_badge.hide()
        music_slider_box = QVBoxLayout()
        music_slider_box.setSpacing(2)
        music_slider_box.addWidget(self.parent.music_volume_slider, 0, Qt.AlignHCenter)
        music_slider_box.addWidget(self.parent.music_volume_label, 0, Qt.AlignHCenter)
        return music_slider_box
