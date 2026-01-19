from PyQt5.QtWidgets import (
    QWidget, QPushButton, QHBoxLayout, QVBoxLayout,
    QLabel, QSlider, QStackedWidget, QComboBox, QFrame, QGridLayout
)

from PyQt5.QtCore import Qt
from crop_widgets import DrawWidget
from config import HUD_ELEMENT_MAPPINGS

class Ui_CropApp:
    def setupUi(self, CropAppWindow):
        CropAppWindow.setWindowTitle(CropAppWindow.base_title)
        CropAppWindow.resize(1600, 900)
        CropAppWindow.main_layout = QVBoxLayout(CropAppWindow)
        CropAppWindow.main_layout.setContentsMargins(0, 0, 0, 0)
        CropAppWindow.main_layout.setSpacing(0)
        
        # Wizard Header
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

        # Progress Tracker
        progress_frame = QFrame()
        progress_frame.setStyleSheet("background: transparent;")
        progress_layout = QHBoxLayout(progress_frame)
        progress_layout.setSpacing(15)
        CropAppWindow.hud_elements = list(HUD_ELEMENT_MAPPINGS.values())
        CropAppWindow.progress_labels = {}
        for i, element in enumerate(CropAppWindow.hud_elements):
            label = QLabel(element.upper())
            label.setAlignment(Qt.AlignCenter)
            label.setStyleSheet("""
                QLabel {
                    background-color: #374151;
                    color: #9CA3AF;
                    padding: 6px 12px;
                    border-radius: 6px;
                    font-weight: 700;
                    font-size: 12px;
                    border: 1px solid #4B5563;
                }
                QLabel.completed {
                    background-color: #10B981;
                    color: white;
                    border: 1px solid #059669;
                }
                QLabel.current {
                    background-color: #2563EB;
                    color: white;
                    border: 1px solid #1D4ED8;
                }
            """)
            progress_layout.addWidget(label)
            CropAppWindow.progress_labels[element] = label
            if i < len(CropAppWindow.hud_elements) - 1:
                arrow = QLabel("→")
                arrow.setStyleSheet("background: transparent; color: #4B5563; font-size: 16px; font-weight: 900;")
                progress_layout.addWidget(arrow)

        progress_layout.addStretch()
        wizard_layout.addWidget(progress_frame)

        # Step Instructions
        step_row = QHBoxLayout()
        CropAppWindow.step_label = QLabel("STEP 1: LOAD VIDEO")
        CropAppWindow.step_label.setStyleSheet("color: #60A5FA; font-weight: 700; font-size: 16px;")
        CropAppWindow.instruction_label = QLabel("Open a video to begin.")
        CropAppWindow.instruction_label.setStyleSheet("color: #E5E7EB; font-size: 14px;")
        step_row.addWidget(CropAppWindow.step_label)
        step_row.addWidget(CropAppWindow.instruction_label, 1)
        wizard_layout.addLayout(step_row)
        CropAppWindow.main_layout.addWidget(CropAppWindow.wizard_frame)

        # Main View Stack
        CropAppWindow.view_stack = QStackedWidget(CropAppWindow)
        CropAppWindow.video_frame = QWidget()
        CropAppWindow.video_frame.setStyleSheet("background-color: #111827;")
        CropAppWindow.draw_widget = DrawWidget(CropAppWindow)
        CropAppWindow.view_stack.addWidget(CropAppWindow.video_frame)
        CropAppWindow.view_stack.addWidget(CropAppWindow.draw_widget)
        CropAppWindow.main_layout.addWidget(CropAppWindow.view_stack, 1)

        # Position Slider
        CropAppWindow.position_slider = QSlider(Qt.Horizontal)
        CropAppWindow.position_slider.setRange(0, 1000)
        CropAppWindow.position_slider.setEnabled(False)
        # Stylesheet is in config.py, no need to set here

        slider_container = QFrame()
        slider_container.setLayout(QHBoxLayout())
        slider_container.layout().setContentsMargins(10, 5, 10, 5)
        slider_container.layout().addWidget(CropAppWindow.position_slider)
        slider_container.setStyleSheet("background-color: #111827; border-top: 1px solid #374151;")
        CropAppWindow.main_layout.addWidget(slider_container)


        # Controls Footer
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

        # Left Controls
        video_controls = QHBoxLayout()
        video_controls.setSpacing(10)
        CropAppWindow.open_button = QPushButton("1. OPEN VIDEO")
        self.style_button(CropAppWindow.open_button, primary=True, large=True)
        CropAppWindow.play_pause_button = QPushButton("PLAY")
        CropAppWindow.play_pause_button.setEnabled(False)
        self.style_button(CropAppWindow.play_pause_button)
        CropAppWindow.snapshot_button = QPushButton("2. TAKE SNAPSHOT")
        CropAppWindow.snapshot_button.setEnabled(False)
        self.style_button(CropAppWindow.snapshot_button, accent=True)
        video_controls.addWidget(CropAppWindow.open_button)
        video_controls.addWidget(CropAppWindow.play_pause_button)
        video_controls.addWidget(CropAppWindow.snapshot_button)
        controls_layout.addLayout(video_controls)
        
        # Center Guidance
        guidance_layout = QVBoxLayout()
        guidance_layout.setContentsMargins(10, 0, 10, 0)
        CropAppWindow.guidance_label = QLabel("STEP 1: LOAD YOUR VIDEO")
        CropAppWindow.guidance_label.setStyleSheet("color: #F59E0B; font-weight: 700; font-size: 14px;")
        CropAppWindow.guidance_label.setAlignment(Qt.AlignCenter)
        CropAppWindow.next_step_label = QLabel("Click 'OPEN VIDEO' to select a file.")
        CropAppWindow.next_step_label.setStyleSheet("color: #E5E7EB; font-size: 12px;")
        CropAppWindow.next_step_label.setAlignment(Qt.AlignCenter)
        CropAppWindow.hint_label = QLabel("")
        CropAppWindow.hint_label.setStyleSheet("color: #60A5FA; font-style: italic;")
        CropAppWindow.hint_label.setAlignment(Qt.AlignCenter)
        CropAppWindow.hint_label.setVisible(False)
        guidance_layout.addWidget(CropAppWindow.guidance_label)
        guidance_layout.addWidget(CropAppWindow.next_step_label)
        guidance_layout.addWidget(CropAppWindow.hint_label)
        controls_layout.addLayout(guidance_layout, 1)

        # Right Controls
        action_controls = QHBoxLayout()
        action_controls.setSpacing(10)
        CropAppWindow.back_button = QPushButton("BACK TO VIDEO")
        CropAppWindow.back_button.setVisible(False)
        self.style_button(CropAppWindow.back_button)
        CropAppWindow.reset_state_button = QPushButton("RESET ALL")
        self.style_button(CropAppWindow.reset_state_button, warning=True)
        CropAppWindow.complete_button = QPushButton("FINISH & SAVE")
        CropAppWindow.complete_button.setVisible(False)
        self.style_button(CropAppWindow.complete_button, success=True)
        action_controls.addWidget(CropAppWindow.back_button)
        action_controls.addWidget(CropAppWindow.reset_state_button)
        action_controls.addWidget(CropAppWindow.complete_button)
        controls_layout.addLayout(action_controls)

        CropAppWindow.main_layout.addWidget(CropAppWindow.controls_frame)
        
        # Hidden/Utility widgets
        CropAppWindow.send_crop_button = QPushButton("Send")
        CropAppWindow.send_crop_button.setVisible(False)
        CropAppWindow.coordinates_label = QLabel("")
        CropAppWindow.coordinates_label.setVisible(False)

    def style_button(self, btn, primary=False, accent=False, warning=False, success=False, large=False):
        btn.setProperty('class', ' '.join(filter(None, ['primary' if primary else None, 'accent' if accent else None, 'warning' if warning else None, 'success' if success else None, 'large' if large else None])))
        btn.style().unpolish(btn)
        btn.style().polish(btn)
