from PyQt5.QtCore import QObject, QEvent, QPoint
from PyQt5.QtGui import QCursor
from PyQt5.QtWidgets import QToolTip

class ToolTipManager(QObject):
    def __init__(self, parent=None):
        super().__init__(parent)
        self._tooltips = {}

    def add_tooltip(self, widget, text):
        """Register a widget to show a custom tooltip."""
        if widget:
            widget.installEventFilter(self)
            self._tooltips[widget.objectName()] = text

    def eventFilter(self, obj, event):
        """Filter events to show/hide tooltips."""
        if event.type() == QEvent.Enter:
            tooltip_text = self._tooltips.get(obj.objectName())
            if tooltip_text:
                offset = QPoint(20, -40) 
                QToolTip.showText(QCursor.pos() + offset, tooltip_text, obj)
            return False 
        elif event.type() == QEvent.Leave:
            QToolTip.hideText()
            return False
        elif event.type() == QEvent.MouseButtonPress:
            QToolTip.hideText()
        return super().eventFilter(obj, event)