from PyQt5.QtWidgets import QWidget

class SliderContainer(QWidget):
    def __init__(self, slider_instance, parent=None):
        super().__init__(parent)
        self.slider = slider_instance
        self.slider.setParent(self)
        self.container_height = 150
        self.slider_height = 150
        self.v_offset = self.container_height - self.slider_height

    def resizeEvent(self, event):
        self.slider.setGeometry(
            0,
            self.v_offset,
            self.width(),
            self.slider_height
        )
        super().resizeEvent(event)
