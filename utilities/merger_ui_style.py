class MergerUIStyleMixin:
    def set_style(self):
        """Applies a dark theme stylesheet similar to the main app."""
        self.parent.setStyleSheet("""
            QMainWindow, QWidget {
                background-color: #2c3e50;
                color: #ecf0f1;
                font-family: "Helvetica Neue", Arial, sans-serif;
                font-size: 13px;
            }
            QLabel#titleLabel {
                font-size: 18px;
                font-weight: bold;
                color: #3498db;
                padding-bottom: 10px;
            }
            QPushButton {
                background-color: #3498db;
                color: #ffffff;
                border: none;
                padding: 8px 16px;
                border-radius: 6px;
                font-weight: bold;
                min-height: 20px;
            }
            QPushButton:hover {
                background-color: #2980b9;
            }
            QPushButton[class="move-btn"] {
                 background-color: #2b7089;
                 color: white;
            }
            QPushButton[class="move-btn"]:hover {
                 background-color: #3b8099;
            }
            QPushButton:disabled {
                background-color: #566573;
                color: #aeb6bf;
            }
            QPushButton#aux-btn {
                 background-color: #2b7089;
            }
            QPushButton#aux-btn:hover {
                 background-color: #3b8099;
            }
            QPushButton#danger-btn {
                 background-color: #d96a6a;
                 color: #ffffff;
            }
            QPushButton#danger-btn:hover {
                 background-color: #c05252;
            }
            QPushButton#mergeButton {
                background-color: #2ecc71;
                color: #1e242d;
                font-weight: bold;
                padding: 10px 25px;
                border-radius: 8px;
            }
            QPushButton#mergeButton:hover {
                background-color: #48e68e;
            }
            QPushButton#mergeButton:disabled {
                 background-color: #566573;
                 color: #aeb6bf;
            }
            QPushButton#returnButton {
                background-color: #bfa624;
                color: black;
                font-weight: 600;
                padding: 6px 12px;
                border-radius: 6px;
                min-height: 35px;
            }
            QPushButton#returnButton:hover {
                 background-color: #dcbd2f;
            }
            QPushButton#returnButton:disabled {
                 background-color: #566573;
                 color: #aeb6bf;
            }
            QListWidget {
                background-color: #34495e;
                border: 1px solid #4a667a;
                border-radius: 8px;
                padding: 8px;
                outline: 0;
            }
            QListWidget::item {
                padding: 0;
                margin: 2px 0;
                border: 0;
                background: transparent;
                color: #ecf0f1;
            }
            QCheckBox { spacing: 8px; }
            QCheckBox::indicator { width: 16px; height: 16px; }
            QComboBox {
                background-color: #4a667a; border: 1px solid #3498db; border-radius: 5px;
                padding: 4px 8px; min-height: 24px; color: #ecf0f1;
            }
            QComboBox::drop-down { border: none; }
            QComboBox::down-arrow { image: url(none); }
            QComboBox QAbstractItemView {
                background-color: #34495e; border: 1px solid #4a667a; selection-background-color: #3498db;
                color: #ecf0f1;
            }
            QDoubleSpinBox {
                background-color: #4a667a; border: 1px solid #3498db; border-radius: 5px;
                padding: 4px 6px; min-height: 24px; color: #ecf0f1;
            }
            QSlider::groove:vertical {
                border: 1px solid #4a4a4a; background: #333; width: 16px; border-radius: 6px;
            }
            QSlider::handle:vertical {
                 background: #7289da; border: 1px solid #5c5c5c;
                 height: 18px; margin: 0 -2px; border-radius: 6px;
            }
            QLabel {
                 padding: 0; margin: 0;
            }
        """)