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
    height: 8px; 
    background: #1F2937; 
    margin: 2px 0; 
    border-radius: 4px;
}
QSlider::handle:horizontal { 
    background: #2563EB;
    border: 2px solid #FFFFFF; 
    width: 18px; 
    height: 18px;
    margin: -6px 0; 
    border-radius: 9px;
}
QSlider::handle:horizontal:hover { 
    background: #3B82F6; 
    border: 2px solid #FFFFFF;
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
}
QPushButton:hover { 
    background-color: #4B5563; 
    border: 1px solid #6B7280;
}
QPushButton:pressed { 
    background-color: #1F2937;
    border: 1px solid #4B5563;
}
QPushButton:disabled {
    background-color: #6B7280;
    color: #9CA3AF;
}
QPushButton.primary {
    background-color: #2563EB; color: white;
}
QPushButton.primary:hover {
    background-color: #3B82F6;
}
QPushButton.primary:pressed {
    background-color: #1D4ED8;
}
QPushButton.success {
    background-color: #10B981; color: white;
}
QPushButton.success:hover {
    background-color: #34D399;
}
QPushButton.success:pressed {
    background-color: #059669;
}
QPushButton.warning {
    background-color: #F59E0B; color: white;
}
QPushButton.warning:hover {
    background-color: #FBBF24;
}
QPushButton.warning:pressed {
    background-color: #D97706;
}
QPushButton.accent {
    background-color: #0D9488; color: white;
}
QPushButton.accent:hover {
    background-color: #14B8A6;
}
QPushButton.accent:pressed {
    background-color: #0F766E;
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
    background: #1F2937;
    width: 12px;
    border-radius: 6px;
}
QScrollBar::handle:vertical {
    background: #374151;
    border-radius: 6px;
    min-height: 20px;
}
QScrollBar::handle:vertical:hover {
    background: #4B5563;
}
"""

# For backward compatibility, keep the old names
PORTRAIT_WINDOW_STYLESHEET = UNIFIED_STYLESHEET
CROP_APP_STYLESHEET = UNIFIED_STYLESHEET

