from PyQt5.QtWidgets import (
    QWidget, QPushButton, QHBoxLayout, QVBoxLayout,
    QLabel, QSlider, QStackedWidget, QComboBox, QFrame, QGridLayout, QProgressBar, QScrollArea
)

from PyQt5.QtCore import Qt
from crop_widgets import DrawWidget
from config import HUD_ELEMENT_MAPPINGS

class Ui_CropApp:
    def setupUi(self, CropAppWindow):
        CropAppWindow.setWindowTitle("Crop Tool Wizard")
        CropAppWindow.resize(1600, 900)
        CropAppWindow.main_layout = QVBoxLayout(CropAppWindow)
        CropAppWindow.main_layout.setContentsMargins(0, 0, 0, 0)
        CropAppWindow.main_layout.setSpacing(0)
        CropAppWindow.wizard_frame = QFrame()
        CropAppWindow.wizard_frame.setStyleSheet("""
            QFrame {
                background-color: #1F2937;
                border-bottom: 2px solid #374151;
            }
        """)
        CropAppWindow.wizard_frame.setFixedHeight(130)
        wizard_layout = QVBoxLayout(CropAppWindow.wizard_frame)
        wizard_layout.setContentsMargins(20, 15, 20, 15)
        wizard_layout.setSpacing(10)
        progress_frame = QFrame()
        progress_frame.setStyleSheet("background: transparent;")
        progress_layout = QHBoxLayout(progress_frame)
        progress_layout.setSpacing(15)
        CropAppWindow.hud_elements = list(HUD_ELEMENT_MAPPINGS.values())
        CropAppWindow.progress_labels = {}
        for i, element in enumerate(CropAppWindow.hud_elements):
            label = QLabel(element.upper())
            label.setAlignment(Qt.AlignCenter)
            label.setFixedHeight(30)
            label.setStyleSheet("""
                QLabel {
                    background-color: #0A7C6F;
                    color: #F0FDFA;
                    padding: 2px 8px;
                    border-radius: 4px;
                    font-weight: 700;
                    font-size: 10px;
                    border: 1px solid #065F56;
                    min-width: 120px;
                }
                QLabel.completed {
                    background-color: #065F56;
                    color: white;
                    border: 1px solid #054C44;
                }
                QLabel.current {
                    background-color: #0D9488;
                    color: white;
                    border: 1px solid #0A7C6F;
                }
            """)
            progress_layout.addWidget(label, 0, Qt.AlignCenter)
            CropAppWindow.progress_labels[element] = label
            if i < len(CropAppWindow.hud_elements) - 1:
                arrow = QLabel("→")
                arrow.setStyleSheet("background: transparent; color: #4B5563; font-size: 14px; font-weight: 900; margin: 0 5px;")
                progress_layout.addWidget(arrow, 0, Qt.AlignCenter)
        progress_layout.addStretch()
        wizard_layout.addWidget(progress_frame)
        CropAppWindow.progress_bar = QProgressBar()
        CropAppWindow.progress_bar.setRange(0, 100)
        CropAppWindow.progress_bar.setValue(0)
        CropAppWindow.progress_bar.setFormat("%p%")
        CropAppWindow.progress_bar.setTextVisible(True)
        CropAppWindow.progress_bar.setFixedHeight(18)
        wizard_layout.addWidget(CropAppWindow.progress_bar)
        CropAppWindow.main_layout.addWidget(CropAppWindow.wizard_frame)
        CropAppWindow.view_stack = QStackedWidget(CropAppWindow)
        CropAppWindow.video_frame = QWidget()
        CropAppWindow.video_frame.setStyleSheet("background-color: #111827;")
        CropAppWindow.draw_scroll_area = QScrollArea(CropAppWindow)
        CropAppWindow.draw_widget = DrawWidget()
        CropAppWindow.draw_scroll_area.setWidget(CropAppWindow.draw_widget)
        CropAppWindow.draw_scroll_area.setWidgetResizable(False)
        CropAppWindow.draw_scroll_area.setHorizontalScrollBarPolicy(Qt.ScrollBarAsNeeded)
        CropAppWindow.draw_scroll_area.setVerticalScrollBarPolicy(Qt.ScrollBarAsNeeded)
        CropAppWindow.draw_scroll_area.setFrameShape(QFrame.NoFrame)
        CropAppWindow.draw_scroll_area.setStyleSheet("background-color: #111827;")
        CropAppWindow.draw_scroll_area.setAlignment(Qt.AlignLeft | Qt.AlignTop)
        CropAppWindow.draw_widget.set_scroll_area(CropAppWindow.draw_scroll_area)
        CropAppWindow.view_stack.addWidget(CropAppWindow.video_frame)
        CropAppWindow.view_stack.addWidget(CropAppWindow.draw_scroll_area)
        CropAppWindow.main_layout.addWidget(CropAppWindow.view_stack, 1)
        CropAppWindow.position_slider = QSlider(Qt.Horizontal)
        CropAppWindow.position_slider.setRange(0, 1000)
        CropAppWindow.position_slider.setEnabled(False)
        slider_container = QFrame()
        slider_layout = QHBoxLayout()
        slider_container.setLayout(slider_layout)
        slider_layout.setContentsMargins(10, 5, 10, 5)
        slider_layout.setSpacing(10)
        CropAppWindow.current_time_label = QLabel("00:00")
        CropAppWindow.current_time_label.setAlignment(Qt.AlignCenter)
        CropAppWindow.total_time_label = QLabel("00:00")
        CropAppWindow.total_time_label.setAlignment(Qt.AlignCenter)
        CropAppWindow.current_time_label.setFixedWidth(60)
        CropAppWindow.total_time_label.setFixedWidth(60)
        slider_layout.addWidget(CropAppWindow.current_time_label)
        slider_layout.addWidget(CropAppWindow.position_slider, 1)
        slider_layout.addWidget(CropAppWindow.total_time_label)
        slider_container.setStyleSheet("background-color: #111827; border-top: 1px solid #374151;")
        CropAppWindow.main_layout.addWidget(slider_container)
        CropAppWindow.controls_frame = QFrame()
        CropAppWindow.controls_frame.setStyleSheet("""
            QFrame {
                background-color: #1F2937;
                padding: 15px 20px;
                border-top: 1px solid #374151;
            }
        """)
        controls_layout = QHBoxLayout(CropAppWindow.controls_frame)
        controls_layout.setSpacing(20)
        left_controls = QHBoxLayout()
        left_controls.setSpacing(10)
        CropAppWindow.open_button = QPushButton("UPLOAD VIDEO")
        self.style_button(CropAppWindow.open_button, primary=True, large=True)
        CropAppWindow.open_button.setFixedSize(150, 40)
        left_controls.addWidget(CropAppWindow.open_button)
        CropAppWindow.snapshot_button = QPushButton("START CROPPING")
        CropAppWindow.snapshot_button.setEnabled(False)
        self.style_button(CropAppWindow.snapshot_button, accent=True)
        CropAppWindow.snapshot_button.setFixedSize(160, 40)
        left_controls.addWidget(CropAppWindow.snapshot_button)
        CropAppWindow.magic_wand_button = QPushButton("🪄 MAGIC WAND")
        CropAppWindow.magic_wand_button.setEnabled(False)
        CropAppWindow.magic_wand_button.setVisible(False)
        self.style_button(CropAppWindow.magic_wand_button, primary=True)
        CropAppWindow.magic_wand_button.setFixedSize(150, 40)
        left_controls.addWidget(CropAppWindow.magic_wand_button)
        CropAppWindow.play_pause_button = QPushButton("▶ PLAY")
        CropAppWindow.play_pause_button.setEnabled(False)
        self.style_button(CropAppWindow.play_pause_button)
        CropAppWindow.play_pause_button.setFixedSize(120, 40)
        right_controls = QHBoxLayout()
        right_controls.setSpacing(10)
        controls_layout.addLayout(left_controls)
        controls_layout.addStretch(1)
        controls_layout.addWidget(CropAppWindow.play_pause_button)
        controls_layout.addStretch(1)
        controls_layout.addLayout(right_controls)
        action_controls = QHBoxLayout()
        action_controls.setSpacing(10)
        CropAppWindow.return_button = QPushButton("RETURN TO MAIN APP")
        self.style_button(CropAppWindow.return_button)
        CropAppWindow.return_button.setFixedSize(130, 40)
        CropAppWindow.return_button.setStyleSheet("background-color: #bfa624; color: black; text-align: center; font-size: 11px; font-weight: bold; padding: 5px;")
        CropAppWindow.reset_state_button = QPushButton("RESET ALL")
        self.style_button(CropAppWindow.reset_state_button, danger=True)
        CropAppWindow.reset_state_button.setFixedSize(120, 40)
        action_controls.addWidget(CropAppWindow.return_button)
        action_controls.addWidget(CropAppWindow.reset_state_button)
        controls_layout.addLayout(action_controls)
        CropAppWindow.main_layout.addWidget(CropAppWindow.controls_frame)
        CropAppWindow.coordinates_label = QLabel("")
        CropAppWindow.coordinates_label.setVisible(False)

    def style_button(self, btn, primary=False, accent=False, warning=False, danger=False, success=False, large=False):
        btn.setCursor(Qt.PointingHandCursor)
        btn.setProperty('class', ' '.join(filter(None, ['primary' if primary else None, 'accent' if accent else None, 'warning' if warning else None, 'danger' if danger else None, 'success' if success else None, 'large' if large else None])))
        btn.style().unpolish(btn)
        btn.style().polish(btn)
