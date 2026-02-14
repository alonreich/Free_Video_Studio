from __future__ import annotations
from dataclasses import dataclass
import types
from typing import Any
import sys

class DummyLogger:
    def info(self, *args: Any, **kwargs: Any) -> None:
        return None

    def error(self, *args: Any, **kwargs: Any) -> None:
        return None

    def warning(self, *args: Any, **kwargs: Any) -> None:
        return None

class DummySpinBox:
    def __init__(self, value: float):
        self._value = float(value)

    def value(self) -> float:
        return self._value

class DummyCheckBox:
    def __init__(self, checked: bool):
        self._checked = bool(checked)

    def isChecked(self) -> bool:
        return self._checked

class DummyTimer:
    def __init__(self, active: bool = False):
        self._active = active
        self.started_with: list[int] = []

    def isActive(self) -> bool:
        return self._active

    def stop(self) -> None:
        self._active = False

    def start(self, interval: int = 0) -> None:
        self._active = True
        self.started_with.append(interval)

class DummyButton:
    def __init__(self):
        self.text = ""

    def setText(self, text: str) -> None:
        self.text = text

    def setIcon(self, _icon: Any) -> None:
        return None

class DummySlider:
    def __init__(self):
        self.visible_calls: list[bool] = []
        self.time_calls: list[tuple[int, int]] = []

    def set_music_visible(self, visible: bool) -> None:
        self.visible_calls.append(bool(visible))

    def set_music_times(self, start_ms: int, end_ms: int) -> None:
        self.time_calls.append((int(start_ms), int(end_ms)))

    def reset_music_times(self) -> None:
        self.time_calls.append((0, 0))

class DummyMediaPlayer:
    def __init__(self, playing: bool = False, current_ms: int = 0, rate: float = 1.0):
        self._playing = playing
        self._time = int(current_ms)
        self._rate = float(rate)
        self.set_time_calls: list[int] = []
        self.set_rate_calls: list[float] = []
        self.paused = 0

    def is_playing(self) -> bool:
        return self._playing

    def play(self) -> None:
        self._playing = True

    def pause(self) -> None:
        self._playing = False
        self.paused += 1

    def get_time(self) -> int:
        return self._time

    def set_time(self, value: int) -> None:
        self._time = int(value)
        self.set_time_calls.append(int(value))

    def get_rate(self) -> float:
        return self._rate

    def set_rate(self, value: float) -> None:
        self._rate = float(value)
        self.set_rate_calls.append(float(value))

    def stop(self) -> None:
        self._playing = False

    def set_media(self, _media: Any) -> None:
        return None

    def audio_set_volume(self, _vol: int) -> None:
        return None
@dataclass
class DummyConfigManager:
    config: dict[str, Any]

    def save_config(self, cfg: dict[str, Any]) -> None:
        self.config = dict(cfg)

class DummySignal:
    def __init__(self) -> None:
        self._callbacks: list[Any] = []

    def connect(self, cb: Any) -> None:
        self._callbacks.append(cb)

    def emit(self, *args: Any, **kwargs: Any) -> None:
        for cb in list(self._callbacks):
            cb(*args, **kwargs)

class DummyKeyEvent:
    def __init__(self, txt: str, modifiers: int = 0) -> None:
        self._txt = txt
        self._mods = modifiers

    def text(self) -> str:
        return self._txt

    def modifiers(self) -> int:
        return self._mods

class DummyListItem:
    def __init__(self, label: str) -> None:
        self._hidden = False
        self._widget = types.SimpleNamespace(name_lbl=types.SimpleNamespace(text=lambda: label))

    def isHidden(self) -> bool:
        return self._hidden

def install_qt_vlc_stubs() -> None:
    """Install lightweight stubs for PyQt5/vlc so logic modules can be imported in tests."""
    if "PyQt5" in sys.modules and "vlc" in sys.modules:
        return
    pyqt5 = types.ModuleType("PyQt5")
    qtcore = types.ModuleType("PyQt5.QtCore")
    qtwidgets = types.ModuleType("PyQt5.QtWidgets")
    qtgui = types.ModuleType("PyQt5.QtGui")

    class Qt:
        NoModifier = 0
        LeftButton = 1
        PointingHandCursor = 13
        ArrowCursor = 0
        AlignLeft = 1
        AlignRight = 2
        AlignCenter = 4
        AlignTop = 8
        AlignBottom = 16
        AlignHCenter = 32
        AlignVCenter = 64
        WA_NativeWindow = 0
        WA_TransparentForMouseEvents = 0
        ActiveWindowFocusReason = 0
        NoFocus = 0
        @staticmethod
        def Alignment() -> int:
            return 0

    class QTimer:
        def __init__(self, *args: Any, **kwargs: Any) -> None:
            self._interval = 0
            self._active = False
            self.timeout = DummySignal()

        def setSingleShot(self, _value: bool) -> None:
            return None

        def setInterval(self, value: int) -> None:
            self._interval = int(value)

        def start(self, _value: int | None = None) -> None:
            self._active = True

        def stop(self) -> None:
            self._active = False

        def isActive(self) -> bool:
            return self._active
        @staticmethod
        def singleShot(_ms: int, cb: Any) -> None:
            cb()

    class QThread:
        def __init__(self, *_args: Any, **_kwargs: Any) -> None:
            return None
        @staticmethod
        def msleep(_ms: int) -> None:
            return None

    class QObject:
        def __init__(self, *_args: Any, **_kwargs: Any) -> None:
            return None

    class QStyle:
        SP_MediaPlay = 1
        SP_MediaPause = 2

    class QDialog:
        Accepted = 1

    class QListWidget:
        def __init__(self, *_args: Any, **_kwargs: Any) -> None:
            self._items: list[Any] = []
            self._widgets: dict[int, Any] = {}
            self._current = None

        def count(self) -> int:
            return len(self._items)

        def item(self, idx: int) -> Any:
            return self._items[idx]

        def itemWidget(self, item: Any) -> Any:
            return getattr(item, "_widget", None)

        def setCurrentItem(self, item: Any) -> None:
            self._current = item

        def scrollToItem(self, _item: Any) -> None:
            return None

        def keyPressEvent(self, _event: Any) -> None:
            return None

    class QWidget:
        def __init__(self, *args: Any, **kwargs: Any) -> None:
            return None

    class QHBoxLayout:
        def __init__(self, *args: Any, **kwargs: Any) -> None:
            return None

        def setContentsMargins(self, *_args: Any) -> None:
            return None

        def setSpacing(self, *_args: Any) -> None:
            return None

        def addWidget(self, *_args: Any) -> None:
            return None

    class QLabel:
        def __init__(self, text: str = "", *args: Any, **kwargs: Any) -> None:
            self._text = text

        def setStyleSheet(self, *_args: Any) -> None:
            return None

        def text(self) -> str:
            return self._text
    qtcore.Qt = Qt
    qtcore.QTimer = QTimer
    qtcore.QThread = QThread
    qtcore.QObject = QObject
    qtcore.QSize = object
    qtcore.QEvent = object
    qtcore.QCoreApplication = type("QCoreApplication", (), {"instance": staticmethod(lambda: None)})
    qtcore.QPropertyAnimation = type("QPropertyAnimation", (), {})
    qtcore.QAbstractAnimation = type("QAbstractAnimation", (), {})
    qtcore.QRect = object
    qtcore.QPoint = object
    qtcore.pyqtSignal = lambda *_a, **_k: DummySignal()
    qtwidgets.QStyle = QStyle
    qtwidgets.QDialog = QDialog
    qtwidgets.QListWidget = QListWidget
    qtwidgets.QWidget = QWidget
    qtwidgets.QHBoxLayout = QHBoxLayout
    qtwidgets.QLabel = QLabel
    for n in [
        "QGridLayout",
        "QStyleOptionSlider",
        "QStyleOptionSpinBox",
        "QVBoxLayout",
        "QStackedLayout",
        "QFrame",
        "QPushButton",
        "QSlider",
        "QLabel",
        "QSpinBox",
        "QDoubleSpinBox",
        "QCheckBox",
        "QProgressBar",
        "QComboBox",
        "QSizePolicy",
        "QApplication",
        "QMessageBox",
        "QToolTip",
        "QFileDialog",
    ]:
        setattr(qtwidgets, n, type(n, (), {}))
    for n in [
        "QPixmap",
        "QPainter",
        "QColor",
        "QIcon",
        "QFont",
        "QFontMetrics",
        "QPen",
        "QCursor",
        "QPainterPath",
        "QLinearGradient",
        "QBrush",
    ]:
        setattr(qtgui, n, type(n, (), {}))
    sys.modules["PyQt5"] = pyqt5
    sys.modules["PyQt5.QtCore"] = qtcore
    sys.modules["PyQt5.QtWidgets"] = qtwidgets
    sys.modules["PyQt5.QtGui"] = qtgui
    pyqt5.QtCore = qtcore
    pyqt5.QtWidgets = qtwidgets
    pyqt5.QtGui = qtgui
    vlc_mod = types.ModuleType("vlc")
    vlc_mod.State = types.SimpleNamespace(Ended=9, Playing=1)
    vlc_mod.EventType = types.SimpleNamespace()
    sys.modules["vlc"] = vlc_mod









