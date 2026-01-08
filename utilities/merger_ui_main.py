from utilities.merger_ui_build import MergerUIBuildMixin
from utilities.merger_ui_style import MergerUIStyleMixin
from utilities.merger_ui_badge import MergerUIBadgeMixin
from utilities.merger_ui_widgets import MergerUIWidgetsMixin
from utilities.merger_ui_overlay import MergerUIOverlayMixin

class MergerUI(MergerUIBuildMixin, MergerUIStyleMixin, MergerUIBadgeMixin, MergerUIWidgetsMixin, MergerUIOverlayMixin):

    def __init__(self, parent):
        self.parent = parent