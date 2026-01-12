from PyQt5.QtWidgets import QPushButton, QApplication
from PyQt5.QtCore import Qt

class ClickableButton(QPushButton):
    def enterEvent(self, event):
        """
        Set the override cursor to a pointing hand when the mouse enters the button.
        """
        QApplication.setOverrideCursor(Qt.PointingHandCursor)
        super().enterEvent(event)

    def leaveEvent(self, event):
        """
        Restore the previous override cursor when the mouse leaves the button.
        """
        QApplication.restoreOverrideCursor()
        super().leaveEvent(event)
