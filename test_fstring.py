import sys
sys.path.insert(0, 'C:\\Fortnite_Video_Software\\advanced')

from PyQt5.QtWidgets import QApplication
app = QApplication(sys.argv)

import constants
s = f"""
QPushButton {{
    background: qlineargradient(x1:0, y1:0, x2:0, y2:1, stop:0 {constants.COLOR_SUCCESS.name()}, stop:1 {constants.COLOR_SUCCESS.darker(150).name()});
    color: white; font-size: 14px; font-weight: bold;
    border: 1px solid {constants.COLOR_SUCCESS.darker(150).name()}; border-style: outset; border-radius: 2px;
}}
"""
print("SUCCESS:", len(s))
