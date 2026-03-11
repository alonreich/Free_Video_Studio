from PyQt5.QtCore import QObject, QEvent, QPoint
from PyQt5.QtGui import QCursor
from PyQt5.QtWidgets import QToolTip

class ToolTipManager(QObject):
    def __init__(self, parent=None):
        super().__init__(parent)
        self._tooltips = {}

    def add_tooltip(self, widget, text):
        if widget is None:
            return
        widget.installEventFilter(self)
        self._tooltips[widget.objectName()] = text

    def eventFilter(self, obj, event):
        try:
            if obj is None: return False
            if event.type() == QEvent.Enter:
                name = obj.objectName()
                if not name: return False
                tooltip_text = self._tooltips.get(name)
                if tooltip_text:
                    offset = QPoint(20, -40) 
                    QToolTip.showText(QCursor.pos() + offset, tooltip_text, obj)
                return False 
            elif event.type() == QEvent.Leave:
                QToolTip.hideText()
                return False
            elif event.type() == QEvent.MouseButtonPress:
                QToolTip.hideText()
        except (RuntimeError, AttributeError):
            pass
        return super().eventFilter(obj, event)