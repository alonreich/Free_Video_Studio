from PyQt5.QtWidgets import QWidget

class SliderContainer(QWidget):
    """
    A container to hold the TrimmedSlider, allowing the slider to be
    taller than the space it visually occupies in the layout. This enables
    drawing handles above the container's bounds without disturbing
    the positions of other widgets.
    """

    def __init__(self, slider_instance, parent=None):
        super().__init__(parent)
        self.slider = slider_instance
        self.slider.setParent(self)
        self.container_height = 50
        self.slider_height = 80
        self.v_offset = self.container_height - self.slider_height

    def resizeEvent(self, event):
        """
        Manually set the geometry of the child slider.
        It's positioned with a negative y-offset, making it draw
        outside and above this container's top boundary.
        """
        self.slider.setGeometry(
            0,
            self.v_offset,
            self.width(),
            self.slider_height
        )
        super().resizeEvent(event)
