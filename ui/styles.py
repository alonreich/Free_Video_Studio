class UIStyles:
    COLOR_PRIMARY = "#266b89"
    COLOR_HOVER = "#2980b9"
    COLOR_BACKGROUND = "#2c3e50"
    COLOR_TEXT = "#ecf0f1"
    _3D_COMMON = """
        QPushButton {
            border-style: solid;
            font-weight: bold;
        }
        QPushButton:disabled {
            background-color: #7f8c8d;
            color: #bdc3c7;
            border: none;
        }
    """
    _HOVER_BORDER = """
        QPushButton:hover:!disabled {
            border: 2px solid #7DD3FC;
        }
    """
    BUTTON_STANDARD = _3D_COMMON + """
        QPushButton {
            background-color: #266b89;
            color: #ffffff;
            padding: 10px 18px;
            border-radius: 8px;
            border-top: 1px solid rgba(255, 255, 255, 0.2);
            border-left: 1px solid rgba(255, 255, 255, 0.2);
            border-bottom: 1px solid rgba(0, 0, 0, 0.6);
            border-right: 1px solid rgba(0, 0, 0, 0.6);
        }
        QPushButton:pressed:!disabled {
            background-color: #1a5276;
            border-top: 1px solid rgba(0, 0, 0, 0.7);
            border-left: 1px solid rgba(0, 0, 0, 0.7);
            border-bottom: 1px solid rgba(255, 255, 255, 0.1);
            border-right: 1px solid rgba(255, 255, 255, 0.1);
            padding-top: 11px;
            padding-left: 19px;
            padding-bottom: 9px;
            padding-right: 17px;
        }
    """ + _HOVER_BORDER
    BUTTON_PLAY = _3D_COMMON + """
        QPushButton {
            background-color: #1b6d26;
            color: white;
            border-radius: 8px;
            font-size: 12px;
            padding: 8px 14px;
            border-top: 1px solid rgba(255, 255, 255, 0.2);
            border-left: 1px solid rgba(255, 255, 255, 0.2);
            border-bottom: 1px solid rgba(0, 0, 0, 0.6);
            border-right: 1px solid rgba(0, 0, 0, 0.6);
        }
        QPushButton:pressed:!disabled {
            border-top: 1px solid rgba(0, 0, 0, 0.7);
            border-left: 1px solid rgba(0, 0, 0, 0.7);
            border-bottom: 1px solid rgba(255, 255, 255, 0.1);
            border-right: 1px solid rgba(255, 255, 255, 0.1);
            padding-top: 9px;
            padding-left: 15px;
            padding-bottom: 7px;
            padding-right: 13px;
        }
    """ + _HOVER_BORDER
    BUTTON_PROCESS = _3D_COMMON + """
        QPushButton {
            background-color: #1b6d26;
            color: white;
            font-size: 12px;
            border-radius: 10px;
            padding: 10px 18px;
            border-top: 1px solid rgba(255, 255, 255, 0.2);
            border-left: 1px solid rgba(255, 255, 255, 0.2);
            border-bottom: 1px solid rgba(0, 0, 0, 0.6);
            border-right: 1px solid rgba(0, 0, 0, 0.6);
        }
        QPushButton:pressed:!disabled {
            border-top: 1px solid rgba(0, 0, 0, 0.7);
            border-left: 1px solid rgba(0, 0, 0, 0.7);
            border-bottom: 1px solid rgba(255, 255, 255, 0.1);
            border-right: 1px solid rgba(255, 255, 255, 0.1);
            padding-top: 11px;
            padding-left: 19px;
            padding-bottom: 9px;
            padding-right: 17px;
        }
    """ + _HOVER_BORDER
    BUTTON_CANCEL = _3D_COMMON + """
        QPushButton {
            background-color: #c0392b;
            color: white;
            font-size: 16px;
            border-radius: 10px;
            padding: 10px 18px;
            border-top: 1px solid rgba(255, 255, 255, 0.2);
            border-left: 1px solid rgba(255, 255, 255, 0.2);
            border-bottom: 1px solid rgba(0, 0, 0, 0.6);
            border-right: 1px solid rgba(0, 0, 0, 0.6);
        }
        QPushButton:pressed:!disabled {
            border-top: 1px solid rgba(0, 0, 0, 0.7);
            border-left: 1px solid rgba(0, 0, 0, 0.7);
            border-bottom: 1px solid rgba(255, 255, 255, 0.1);
            border-right: 1px solid rgba(255, 255, 255, 0.1);
            padding-top: 11px;
            padding-left: 19px;
            padding-bottom: 9px;
            padding-right: 17px;
        }
    """ + _HOVER_BORDER
    BUTTON_TOOL = _3D_COMMON + """
        QPushButton {
            background-color: #318181;
            color: #ffffff;
            border-radius: 6px;
            font-size: 10px;
            padding: 5px;
            border-top: 1px solid rgba(255, 255, 255, 0.1);
            border-left: 1px solid rgba(255, 255, 255, 0.1);
            border-bottom: 1px solid rgba(0, 0, 0, 0.5);
            border-right: 1px solid rgba(0, 0, 0, 0.5);
        }
        QPushButton:pressed:!disabled {
            border-top: 1px solid rgba(0, 0, 0, 0.6);
            border-left: 1px solid rgba(0, 0, 0, 0.6);
            border-bottom: 1px solid rgba(255, 255, 255, 0.1);
            border-right: 1px solid rgba(255, 255, 255, 0.1);
            padding-top: 6px;
            padding-left: 6px;
            padding-bottom: 4px;
            padding-right: 4px;
        }
    """ + _HOVER_BORDER
    BUTTON_WIZARD_BLUE = _3D_COMMON + """
        QPushButton {
            background: qlineargradient(x1:0, y1:0, x2:0, y2:1, stop:0 #3a8db0, stop:0.1 #2d7da1, stop:1 #1a5276);
            color: #ffffff;
            border-radius: 8px;
            font-weight: bold;
            font-size: 10px;
            padding: 8px 14px;
            border-top: 1px solid rgba(255, 255, 255, 0.2);
            border-left: 1px solid rgba(255, 255, 255, 0.2);
            border-bottom: 1px solid rgba(0, 0, 0, 0.6);
            border-right: 1px solid rgba(0, 0, 0, 0.6);
        }
        QPushButton:pressed:!disabled {
            background: qlineargradient(x1:0, y1:0, x2:0, y2:1, stop:0 #0d2c3d, stop:1 #1a5276);
            border-top: 1px solid rgba(0, 0, 0, 0.7);
            border-left: 1px solid rgba(0, 0, 0, 0.7);
            border-bottom: 1px solid rgba(255, 255, 255, 0.1);
            border-right: 1px solid rgba(255, 255, 255, 0.1);
            padding-top: 9px;
            padding-left: 15px;
            padding-bottom: 7px;
            padding-right: 13px;
        }
    """ + _HOVER_BORDER
    BUTTON_WIZARD_GREEN = _3D_COMMON + """
        QPushButton {
            background: qlineargradient(x1:0, y1:0, x2:0, y2:1, stop:0 #2ecc71, stop:0.1 #27ae60, stop:1 #1b6d26);
            color: #ffffff;
            border-radius: 10px;
            font-weight: bold;
            font-size: 12px;
            padding: 10px 18px;
            border-top: 1px solid rgba(255, 255, 255, 0.2);
            border-left: 1px solid rgba(255, 255, 255, 0.2);
            border-bottom: 1px solid rgba(0, 0, 0, 0.6);
            border-right: 1px solid rgba(0, 0, 0, 0.6);
        }
        QPushButton:pressed:!disabled {
            background: qlineargradient(x1:0, y1:0, x2:0, y2:1, stop:0 #0e3514, stop:1 #1b6d26);
            border-top: 1px solid rgba(0, 0, 0, 0.7);
            border-left: 1px solid rgba(0, 0, 0, 0.7);
            border-bottom: 1px solid rgba(255, 255, 255, 0.1);
            border-right: 1px solid rgba(255, 255, 255, 0.1);
            padding-top: 11px;
            padding-left: 19px;
            padding-bottom: 9px;
            padding-right: 17px;
        }
    """ + _HOVER_BORDER
    @staticmethod
    def get_3d_style(color, font_size=12, border_radius=8, padding="10px 18px"):
        try:
            parts = padding.split()
            if len(parts) == 2:
                pt_val = int(''.join(filter(str.isdigit, parts[0])))
                pl_val = int(''.join(filter(str.isdigit, parts[1])))
                pt, pl, pb, pr = pt_val, pl_val, pt_val, pl_val
            elif len(parts) == 4:
                pt = int(''.join(filter(str.isdigit, parts[0])))
                pr = int(''.join(filter(str.isdigit, parts[1])))
                pb = int(''.join(filter(str.isdigit, parts[2])))
                pl = int(''.join(filter(str.isdigit, parts[3])))
            else:
                val = int(''.join(filter(str.isdigit, padding)))
                pt = pr = pb = pl = val
            pres_t, pres_l, pres_b, pres_r = pt+1, pl+1, pb-1, pr-1
        except:
            pres_t, pres_l, pres_b, pres_r = "11px", "19px", "9px", "17px"
        return f"""
            QPushButton {{
                background-color: {color};
                color: white;
                font-weight: bold;
                font-size: {font_size}px;
                padding: {padding};
                border-radius: {border_radius}px;
                border-style: solid;
                border-top: 1px solid rgba(255, 255, 255, 0.2);
                border-left: 1px solid rgba(255, 255, 255, 0.2);
                border-bottom: 1px solid rgba(0, 0, 0, 0.6);
                border-right: 1px solid rgba(0, 0, 0, 0.6);
            }}
            QPushButton:hover:!disabled {{
                border: 2px solid #7DD3FC;
            }}
            QPushButton:pressed:!disabled {{
                border-top: 1px solid rgba(0, 0, 0, 0.7);
                border-left: 1px solid rgba(0, 0, 0, 0.7);
                border-bottom: 1px solid rgba(255, 255, 255, 0.1);
                border-right: 1px solid rgba(255, 255, 255, 0.1);
                padding-top: {pres_t}px;
                padding-left: {pres_l}px;
                padding-bottom: {pres_b}px;
                padding-right: {pres_r}px;
            }}
            QPushButton:disabled {{
                background-color: #7f8c8d;
                color: #bdc3c7;
                border: none;
            }}
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
    GLOBAL_STYLE = """
        QDialog, QMessageBox, QProgressDialog {
            background-color: #2c3e50;
            color: #ecf0f1;
            font-family: "Helvetica Neue", Arial, sans-serif;
        }
        QLabel {
            color: #ecf0f1;
            font-size: 13px;
        }
        QPushButton {
            background-color: #266b89;
            color: #ffffff;
            border-style: solid;
            border-top: 1px solid rgba(255, 255, 255, 0.2);
            border-left: 1px solid rgba(255, 255, 255, 0.2);
            border-bottom: 1px solid rgba(0, 0, 0, 0.6);
            border-right: 1px solid rgba(0, 0, 0, 0.6);
            padding: 8px 16px;
            border-radius: 6px;
            font-weight: bold;
            min-width: 80px;
        }
        QPushButton:hover {
            background-color: #2980b9;
            border: 1px solid #7DD3FC;
        }
        QPushButton:pressed {
            background-color: #1a5276;
        }
        QTextEdit {
            background-color: #1a1a1a;
            color: #bdc3c7;
            border: 1px solid #34495e;
            font-family: Consolas, monospace;
            font-size: 11px;
        }
        QProgressBar {
            border: 1px solid #266b89;
            border-radius: 4px;
            text-align: center;
            background-color: #1a1a1a;
            color: white;
        }
        QProgressBar::chunk {
            background-color: #2ecc71;
        }
    """
