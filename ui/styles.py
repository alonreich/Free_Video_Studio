import os
import sys

# Try to import from developer_tools.config to get the unified stylesheet
sys.path.append(os.path.join(os.path.dirname(os.path.dirname(__file__)), 'developer_tools'))
try:
    from config import get_stylesheet
except ImportError:
    get_stylesheet = lambda: ""

from developer_tools.config import UI_COLORS, UI_LAYOUT

class UIStyles:
    # Action Button Classes
    BUTTON_STANDARD = f"QPushButton {{ background-color: {UI_COLORS.BUTTON_DEFAULT}; color: {UI_COLORS.TEXT_PRIMARY}; border: 1px solid {UI_COLORS.BORDER_MEDIUM}; border-radius: {UI_LAYOUT.BUTTON_BORDER_RADIUS}; font-weight: 500; }}"
    
    BUTTON_WIZARD_BLUE = f"QPushButton {{ background-color: {UI_COLORS.PRIMARY}; color: {UI_COLORS.TEXT_PRIMARY}; border: 1px solid {UI_COLORS.PRIMARY_BORDER}; border-radius: {UI_LAYOUT.BUTTON_BORDER_RADIUS}; font-weight: 500; }} QPushButton:hover {{ background-color: {UI_COLORS.PRIMARY_HOVER}; border: 1px solid {UI_COLORS.TEXT_ACCENT}; }}"
    
    BUTTON_WIZARD_GREEN = f"QPushButton {{ background-color: {UI_COLORS.SUCCESS}; color: {UI_COLORS.TEXT_PRIMARY}; border: 1px solid {UI_COLORS.SUCCESS_BORDER}; border-radius: {UI_LAYOUT.BUTTON_BORDER_RADIUS}; font-weight: 500; }} QPushButton:hover {{ background-color: {UI_COLORS.SUCCESS_HOVER}; border: 1px solid {UI_COLORS.SELECTION_GREEN}; }}"
    
    BUTTON_CANCEL = f"QPushButton {{ background-color: {UI_COLORS.BUTTON_DEFAULT}; color: {UI_COLORS.TEXT_PRIMARY}; border: 1px solid {UI_COLORS.BORDER_MEDIUM}; border-radius: {UI_LAYOUT.BUTTON_BORDER_RADIUS}; font-weight: 500; }} QPushButton:hover {{ background-color: {UI_COLORS.BUTTON_HOVER}; border: 1px solid {UI_COLORS.BORDER_ACCENT}; }}"
    
    BUTTON_TOOL = f"QPushButton {{ background-color: {UI_COLORS.BACKGROUND_MEDIUM}; color: {UI_COLORS.TEXT_SECONDARY}; border: 1px solid {UI_COLORS.BORDER_DARK}; border-radius: {UI_LAYOUT.BUTTON_BORDER_RADIUS}; font-size: 10px; font-weight: 500; }} QPushButton:hover {{ background-color: {UI_COLORS.BUTTON_HOVER}; color: {UI_COLORS.TEXT_PRIMARY}; border: 1px solid {UI_COLORS.BORDER_ACCENT}; }}"

    SLIDER_VOLUME_VERTICAL_METALLIC = f"""
        QSlider::groove:vertical {{
            background: {UI_COLORS.BACKGROUND_DARK};
            width: 4px;
            border-radius: 2px;
        }}
        QSlider::handle:vertical {{
            background: {UI_COLORS.PRIMARY};
            border: 1px solid {UI_COLORS.TEXT_ACCENT};
            height: 12px;
            margin: 0 -4px;
            border-radius: 6px;
        }}
    """
    
    CHECKBOX = f"QCheckBox {{ color: {UI_COLORS.TEXT_PRIMARY}; font-size: 11px; }} QCheckBox::indicator {{ width: 14px; height: 14px; border: 1px solid {UI_COLORS.BORDER_MEDIUM}; border-radius: 2px; background: {UI_COLORS.BACKGROUND_DARK}; }} QCheckBox::indicator:checked {{ background: {UI_COLORS.PRIMARY}; border: 1px solid {UI_COLORS.TEXT_ACCENT}; }}"
    
    PROGRESS_BAR = f"QProgressBar {{ border: 1px solid {UI_COLORS.BORDER_DARK}; border-radius: 4px; background: {UI_COLORS.BACKGROUND_DARK}; text-align: center; color: {UI_COLORS.TEXT_SECONDARY}; }} QProgressBar::chunk {{ background: {UI_COLORS.PRIMARY}; border-radius: 2px; }}"
    
    LABEL_STATUS = f"color: {UI_COLORS.TEXT_DISABLED}; font-size: 10px; font-weight: 500;"
    
    @staticmethod
    def get_drop_area_style(is_active=False):
        color = UI_COLORS.SELECTION_GREEN if is_active else UI_COLORS.BORDER_MEDIUM
        return f"""
            QFrame#dropArea {{
                border: 2px dashed {color};
                border-radius: 8px;
                background-color: {UI_COLORS.BACKGROUND_MEDIUM};
                padding: 10px;
            }}
        """

    # Compatibility Placeholders
    BUTTON_PLAY = BUTTON_WIZARD_GREEN
    BUTTON_PROCESS = BUTTON_WIZARD_GREEN
    BUTTON_DANGER = f"QPushButton {{ background-color: {UI_COLORS.DANGER}; color: {UI_COLORS.TEXT_PRIMARY}; border: 1px solid {UI_COLORS.DANGER_BORDER}; border-radius: {UI_LAYOUT.BUTTON_BORDER_RADIUS}; }}"
    SLIDER_VOLUME_VERTICAL = SLIDER_VOLUME_VERTICAL_METALLIC
    SLIDER_TIMELINE_METALLIC = ""
    SLIDER_QUALITY = ""
    SPINBOX = ""
    STATUS_BAR = ""
    LABEL_SEPARATOR = ""

    GLOBAL_STYLE = get_stylesheet()
