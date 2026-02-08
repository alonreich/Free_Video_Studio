class UIStyles:
    COLOR_PRIMARY = "#266b89"
    COLOR_HOVER = "#2980b9"
    COLOR_BACKGROUND = "#2c3e50"
    COLOR_TEXT = "#ecf0f1"
    BUTTON_STANDARD = """
        QPushButton {
            background-color: #266b89;
            color: #ffffff;
            border: none;
            padding: 10px 18px;
            border-radius: 8px;
            font-weight: bold;
        }
        QPushButton:hover { background-color: #2980b9; }
        QPushButton:pressed { background-color: #1a5276; }
        QPushButton:disabled { background-color: #7f8c8d; color: #bdc3c7; }
    """
    BUTTON_PLAY = """
        QPushButton {
            background-color: #1b6d26;
            color: white;
            font-weight: bold;
            border-radius: 8px;
            font-size: 12px;
        }
        QPushButton:hover { background-color: #22822d; }
        QPushButton:disabled { background-color: #7f8c8d; color: #bdc3c7; }
    """
    BUTTON_PROCESS = """
        QPushButton {
            background-color: #1b6d26;
            color: white;
            font-weight: bold;
            font-size: 12px;
            border-radius: 10px;
        }
        QPushButton:hover { background-color: #22822d; }
        QPushButton:disabled { background-color: #7f8c8d; color: #bdc3c7; }
    """
    BUTTON_CANCEL = """
        QPushButton {
            background-color: #c0392b;
            color: white;
            font-weight: bold;
            font-size: 16px;
            border-radius: 10px;
        }
        QPushButton:hover { background-color: #e74c3c; }
    """
    BUTTON_TOOL = """
        QPushButton {
            background-color: #318181;
            color: #ffffff;
            border: 2px solid #2c3e50;
            border-radius: 6px;
            font-size: 10px;
            padding: 5px;
            font-weight: bold;
        }
        QPushButton:hover {
            background-color: #3b9191;
            border-color: #3498db;
            color: #ffffff;
        }
    """
    SLIDER_VOLUME_VERTICAL = ""
    SLIDER_VOLUME_VERTICAL_METALLIC = """
        QSlider::groove:vertical {
            background: #1a1a1a;
            width: 6px;
            border-radius: 3px;
            subcontrol-origin: padding;
            subcontrol-position: center;
        }
        QSlider::handle:vertical {
            background: qlineargradient(x1:0, y1:0, x2:1, y2:0, 
                stop:0 #5a5a5a, 
                stop:0.35 #9a9a9a, 
                stop:0.38 #000, stop:0.42 #000,
                stop:0.45 #9a9a9a,
                stop:0.48 #000, stop:0.52 #000,
                stop:0.55 #9a9a9a,
                stop:0.58 #000, stop:0.62 #000,
                stop:0.65 #9a9a9a,
                stop:1 #5a5a5a);
            border: 1px solid #111;
            width: 40px;
            height: 15px;
            margin: 0 -17px;
            border-radius: 2px;
        }
        QSlider::add-page:vertical {
            background: #1b6d26;
            border-radius: 3px;
        }
        QSlider::sub-page:vertical {
            background: #333;
            border-radius: 3px;
        }
    """
    SLIDER_MUSIC_VERTICAL_METALLIC = """
        QSlider::groove:vertical {
            background: #1a1a1a;
            width: 6px;
            border-radius: 3px;
            subcontrol-origin: padding;
            subcontrol-position: center;
        }
        QSlider::handle:vertical {
            background: qlineargradient(x1:0, y1:0, x2:1, y2:0, 
                stop:0 #5a5a5a, 
                stop:0.35 #9a9a9a, 
                stop:0.38 #000, stop:0.42 #000,
                stop:0.45 #9a9a9a,
                stop:0.48 #000, stop:0.52 #000,
                stop:0.55 #9a9a9a,
                stop:0.58 #000, stop:0.62 #000,
                stop:0.65 #9a9a9a,
                stop:1 #5a5a5a);
            border: 1px solid #111;
            width: 40px;
            height: 12px;
            margin: 0 -17px;
            border-radius: 2px;
        }
        QSlider::add-page:vertical {
            background: #1b6d26;
            border-radius: 3px;
        }
        QSlider::sub-page:vertical {
            background: #333;
            border-radius: 3px;
        }
    """
    SLIDER_TIMELINE_METALLIC = """
        QSlider::groove:horizontal {
            background: #1a1a1a;
            height: 6px;
            border-radius: 3px;
        }
        QSlider::handle:horizontal {
            background: qlineargradient(x1:0, y1:0, x2:0, y2:1, 
                stop:0 #5a5a5a, 
                stop:0.35 #9a9a9a, 
                stop:0.38 #000, stop:0.42 #000,
                stop:0.45 #9a9a9a,
                stop:0.48 #000, stop:0.52 #000,
                stop:0.55 #9a9a9a,
                stop:0.58 #000, stop:0.62 #000,
                stop:0.65 #9a9a9a,
                stop:1 #5a5a5a);
            border: 1px solid #111;
            width: 15px;
            height: 40px;
            margin: -17px 0;
            border-radius: 2px;
        }
        QSlider::sub-page:horizontal {
            background: #1b6d26;
            border-radius: 3px;
        }
        QSlider::add-page:horizontal {
            background: #333;
            border-radius: 3px;
        }
    """
    SLIDER_QUALITY = ""
    SPINBOX = """
        QSpinBox, QDoubleSpinBox {
            background-color: #4a667a;
            border: 1px solid #266b89;
            border-radius: 5px;
            padding: 10px;
            color: #ecf0f1;
            font-size: 13px;
        }
        QSpinBox:disabled, QDoubleSpinBox:disabled {
            background-color: #7f8c8d;
            color: #bdc3c7;
            border-color: #95a5a6;
        }
    """
    CHECKBOX = """
        QCheckBox {
            spacing: 5px;
            font-size: 11px;
            font-weight: bold;
        }
        QCheckBox::indicator {
            width: 18px;
            height: 18px;
        }
    """
    PROGRESS_BAR = """
        QProgressBar {
            border: 1px solid #266b89;
            border-radius: 5px;
            text-align: center;
            height: 18px;
            background-color: #34495e;
            color: white;
        }
        QProgressBar::chunk {
            background-color: #2ecc71;
            border-radius: 4px;
        }
    """
    STATUS_BAR = """
        QStatusBar {
            background: #2c3e50;
            color: #bdc3c7;
            border-top: 1px solid #34495e;
        }
    """
    LABEL_STATUS = """
        color: #ecf0f1;
        font-weight: bold;
        padding: 0 5px;
    """
    LABEL_SEPARATOR = """
        color: #7f8c8d;
        font-weight: bold;
        padding: 0 5px;
    """
    @staticmethod
    def get_drop_area_style(is_active=False):
        color = "#2ecc71" if is_active else "#266b89"
        return f"""
            QFrame#dropArea {{
                border: 3px dashed {color};
                border-radius: 10px;
                background-color: #34495e;
                padding: 20px;
            }}
        """