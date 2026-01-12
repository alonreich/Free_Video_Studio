from PyQt5.QtCore import Qt
from PyQt5.QtWidgets import QStyleOptionSlider, QStyle

class MergerUIBadgeMixin:
    def _update_music_badge(self):
        try:
            if not self.parent.music_volume_slider.isVisible():
                self.parent.music_volume_badge.hide()
                return
            s = self.parent.music_volume_slider
            opt = QStyleOptionSlider()
            opt.initFrom(s)
            opt.orientation = Qt.Vertical
            opt.minimum = s.minimum()
            opt.maximum = s.maximum()
            opt.sliderPosition = int(s.value())
            opt.sliderValue = int(s.value())
            opt.upsideDown = not s.invertedAppearance()
            opt.rect = s.rect()
            handle = s.style().subControlRect(QStyle.CC_Slider, opt, QStyle.SC_SliderHandle, s)
            pt = s.mapTo(self.parent, handle.center())
            eff_volume = self.parent.music_handler._music_eff(int(s.value()))
            self.parent.music_volume_badge.setText(f"{eff_volume}%")
            self.parent.music_volume_badge.adjustSize()
            x_slider_right = s.mapTo(self.parent, s.rect().topRight()).x()
            x = x_slider_right + 8
            y = pt.y() - (self.parent.music_volume_badge.height() // 2)
            y = max(2, min((self.parent.height() - self.parent.music_volume_badge.height() - 2), y))
            self.parent.music_volume_badge.move(x, y)
            self.parent.music_volume_badge.show()
        except Exception:
            pass