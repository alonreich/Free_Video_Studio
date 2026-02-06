class MergerUIStyleMixin:
    def set_style(self):
        """Applies a modernized dark theme stylesheet."""
        self.parent.setStyleSheet("""
            QMainWindow, QWidget {
                background-color: #2c3e50;
                color: #ecf0f1;
                font-family: "Segoe UI", "Helvetica Neue", Arial, sans-serif;
                font-size: 13px; /* Fix #48: Increased font size */
            }
            QLabel#titleLabel {
                font-size: 18px;
                font-weight: bold;
                color: #3498db;
                padding-bottom: 12px;
            }
            QPushButton {
                background-color: #3498db;
                color: #ffffff;
                border: none;
                padding: 8px 16px;
                border-radius: 6px;
                font-weight: 600;
            }
            QPushButton:hover {
                background-color: #2980b9;
            }
            QPushButton:disabled {
                background-color: #566573;
                color: #aeb6bf;
            }
            /* Fix #80: Removed non-standard [class=...] selectors */
            QPushButton#moveUpBtn, QPushButton#moveDownBtn {
                 background-color: #2b7089;
                 font-size: 14px;
            }
            QPushButton#moveUpBtn:hover, QPushButton#moveDownBtn:hover {
                 background-color: #3b8099;
            }
            QPushButton#aux-btn {
                 background-color: #2b7089;
            }
            QPushButton#aux-btn:hover {
                 background-color: #3b8099;
            }
            QPushButton#danger-btn {
                 background-color: #e74c3c;
            }
            QPushButton#danger-btn:hover {
                 background-color: #c0392b;
            }
            QPushButton#mergeButton {
                background-color: #146314;
                color: black;
                font-size: 16px;
                font-weight: bold;
                padding: 12px 30px;
                border-radius: 8px;
            }
            QPushButton#mergeButton:hover {
                background-color: #c8f7c5;
            }
            QPushButton#returnButton {
                background-color: #f39c12;
                color: #2c3e50;
            }
            QPushButton#returnButton:hover {
                 background-color: #f1c40f;
            }
            QListWidget {
                background-color: #1e2a36;
                border: 2px solid #266b89;
                border-radius: 12px;
                padding: 5px;
                outline: none;
            }
            QListWidget::item {
                background-color: transparent;
                border: none;
                margin: 2px 0px;
                padding: 0px;
            }
            QListWidget::item:selected {
                background-color: rgba(52, 152, 219, 0.15);
                border-radius: 10px;
            }
            QComboBox, QDoubleSpinBox {
                background-color: #34495e;
                border: 1px solid #5d6d7e;
                border-radius: 4px;
                padding: 6px;
                min-height: 24px;
            }
            QScrollBar:vertical {
                background: #2c3e50;
                width: 14px; /* Fix #28: Reduced scrollbar width */
                margin: 0;
            }
            QScrollBar::handle:vertical {
                background: #5d6d7e;
                min-height: 20px;
                border-radius: 7px;
            }
            QScrollBar::add-line:vertical, QScrollBar::sub-line:vertical {
                height: 0px;
            }
        """)
