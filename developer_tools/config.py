HUD_ELEMENT_MAPPINGS = {
    "loot": "Loot Area",
    "stats": "Mini Map + Stats",
    "normal_hp": "Own Health Bar (HP)",
    "boss_hp": "Boss HP (For When You Are The Boss Character)",
    "team": "Teammates health Bars (HP)"
}
UNIFIED_STYLESHEET = """
QWidget {
    background-color: #111827;
    color: #F9FAFB;
    font-family: 'Segoe UI', 'Inter', -apple-system, sans-serif;
    font-size: 14px;
    border: none;
}
QLabel { 
    color: #E5E7EB;
    font-weight: 500;
    padding: 4px;
}
QLabel.title {
    color: #FFFFFF;
    font-weight: 700;
    font-size: 16px;
}
QLabel.status { color: #9CA3AF; font-weight: 600; }
QLabel.info { color: #9CA3AF; }
QLabel.italic { font-style: italic; color: #60A5FA; }
QSlider::groove:horizontal { 
    border: 1px solid #374151; 
    height: 16px; 
    background: #1F2937; 
    margin: 2px 0; 
    border-radius: 4px;
}
QSlider::handle:horizontal { 
    background: #2563EB;
    border: 2px solid #FFFFFF; 
    width: 22px; 
    height: 40px;
    margin: -12px 0; 
    border-radius: 11px;
}
QSlider::handle:horizontal:hover { 
    background: #3B82F6; 
    border: 2px solid #FFFFFF;
}
QProgressBar {
    border: 1px solid #374151;
    border-radius: 6px;
    background-color: #111827;
    text-align: center;
    color: #E5E7EB;
    height: 18px;
}
QProgressBar::chunk {
    background-color: #2563EB;
    border-radius: 6px;
}
QPushButton { 
    background-color: #374151; 
    color: #FFFFFF;
    border: 1px solid #4B5563;
    padding: 8px 20px;
    border-radius: 8px;
    font-weight: 600; 
    font-size: 13px;
    height: 40px;
    border-bottom: 3px solid #111827;
}
QPushButton:hover { 
    background-color: #4B5563; 
    border: 1px solid #6B7280;
    border-bottom: 3px solid #111827;
}
QPushButton:pressed { 
    background-color: #1F2937;
    border: 1px solid #4B5563;
    border-bottom: 1px solid #4B5563;
    padding-top: 9px;
    padding-left: 21px;
}
QPushButton:disabled {
    background-color: #6B7280;
    color: #9CA3AF;
}
QPushButton.primary {
    background-color: #2563EB; color: white;
    border-bottom: 3px solid #1E3A8A;
}
QPushButton.primary:hover {
    background-color: #3B82F6;
}
QPushButton.primary:pressed {
    background-color: #1D4ED8;
    border-bottom: 1px solid #1D4ED8;
}
QPushButton.success {
    background-color: #10B981; color: white;
    border-bottom: 3px solid #047857;
}
QPushButton.success:hover {
    background-color: #34D399;
}
QPushButton.success:pressed {
    background-color: #059669;
    border-bottom: 1px solid #059669;
}
QPushButton.warning {
    background-color: #F59E0B; color: white;
    border-bottom: 3px solid #92400E;
}
QPushButton.warning:hover {
    background-color: #FBBF24;
}
QPushButton.warning:pressed {
    background-color: #D97706;
    border-bottom: 1px solid #D97706;
}
QPushButton.danger {
    background-color: #7F1D1D; color: #FEE2E2;
    border: 1px solid #991B1B;
    border-bottom: 3px solid #4C0519;
}
QPushButton.danger:hover {
    background-color: #991B1B;
    border: 1px solid #B91C1C;
    border-bottom: 3px solid #4C0519;
}
QPushButton.danger:pressed {
    background-color: #5B0F0F;
    border: 1px solid #5B0F0F;
    border-bottom: 1px solid #5B0F0F;
}
QPushButton.accent {
    background-color: #0D9488; color: white;
    border-bottom: 3px solid #0F766E;
}
QPushButton.accent:hover {
    background-color: #14B8A6;
}
QPushButton.accent:pressed {
    background-color: #0F766E;
    border-bottom: 1px solid #0F766E;
}
QPushButton.large {
    padding: 8px 24px;
    font-size: 14px;
}
QComboBox {
    background-color: #1F2937;
    color: #FFFFFF;
    border: 1px solid #374151;
    padding: 8px 12px;
    border-radius: 6px;
    min-height: 36px;
    font-size: 13px;
}
QComboBox:hover {
    border: 1px solid #2563EB;
    background-color: #374151;
}
QComboBox::drop-down { 
    border: 0px;
    width: 24px;
}
QComboBox QAbstractItemView {
    background-color: #1F2937;
    color: #E5E7EB;
    border: 1px solid #374151;
    selection-background-color: #2563EB;
}
QFrame.container {
    background-color: #1F2937;
    border-radius: 8px;
    border: 1px solid #374151;
}
QFrame.header {
    background-color: #1F2937;
    border-bottom: 2px solid #2563EB;
}
QFrame.footer {
    background-color: #1F2937;
    padding: 10px;
}
QLineEdit {
    background-color: #1F2937;
    color: #FFFFFF;
    border: 1px solid #374151;
    border-radius: 6px;
    padding: 8px 12px;
    font-size: 13px;
}
QLineEdit:focus {
    border: 1px solid #2563EB;
    background-color: #374151;
}
QCheckBox, QRadioButton {
    color: #E5E7EB;
    spacing: 8px;
}
QCheckBox::indicator, QRadioButton::indicator {
    width: 18px;
    height: 18px;
}
QCheckBox::indicator:checked {
    background-color: #2563EB;
    border: 2px solid #2563EB;
}
QRadioButton::indicator:checked {
    background-color: #2563EB;
    border: 2px solid #2563EB;
}
QScrollBar:vertical {
    background: #F3F4F6;
    width: 26px;
    border-radius: 6px;
}
QScrollBar::handle:vertical {
    background: #000000;
    border-radius: 6px;
    min-height: 32px;
}
QScrollBar::handle:vertical:hover {
    background: #1a1a1a;
}
QScrollBar::add-line:vertical, QScrollBar::sub-line:vertical {
    background: #D1D5DB;
    height: 26px;
    subcontrol-origin: margin;
}
QScrollBar::up-arrow:vertical, QScrollBar::down-arrow:vertical {
    border: 6px solid transparent;
}
QScrollBar::up-arrow:vertical { border-bottom-color: #1F2937; }
QScrollBar::down-arrow:vertical { border-top-color: #1F2937; }
QScrollBar::add-page:vertical, QScrollBar::sub-page:vertical {
    background: #F3F4F6;
}
QScrollBar:horizontal {
    background: #F3F4F6;
    height: 26px;
    border-radius: 6px;
}
QScrollBar::handle:horizontal {
    background: #000000;
    border-radius: 6px;
    min-width: 32px;
}
QScrollBar::handle:horizontal:hover {
    background: #1a1a1a;
}
QScrollBar::add-line:horizontal, QScrollBar::sub-line:horizontal {
    background: #D1D5DB;
    width: 26px;
    subcontrol-origin: margin;
}
QScrollBar::left-arrow:horizontal, QScrollBar::right-arrow:horizontal {
    border: 6px solid transparent;
}
QScrollBar::left-arrow:horizontal { border-right-color: #1F2937; }
QScrollBar::right-arrow:horizontal { border-left-color: #1F2937; }
QScrollBar::add-page:horizontal, QScrollBar::sub-page:horizontal {
    background: #F3F4F6;
}
"""
PORTRAIT_WINDOW_STYLESHEET = UNIFIED_STYLESHEET
CROP_APP_STYLESHEET = UNIFIED_STYLESHEET








