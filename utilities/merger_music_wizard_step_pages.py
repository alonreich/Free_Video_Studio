import os
from PyQt5.QtCore import Qt
from PyQt5.QtWidgets import QWidget, QVBoxLayout, QLabel, QProgressBar, QHBoxLayout, QLineEdit, QListWidgetItem, QFrame
from utilities.merger_trimmed_slider import MergerTrimmedSlider
from utilities.merger_music_wizard_widgets import SearchableListWidget, MusicItemWidget

class MergerMusicWizardStepPagesMixin:
    def load_tracks(self, folder_path):
        """Scans the folder for MP3 files and populates the list."""
        if not os.path.isdir(folder_path):
            self.logger.warning(f"WIZARD: MP3 folder not found: {folder_path}")
            return
        self.track_list.clear()
        files = [f for f in os.listdir(folder_path) if f.lower().endswith(".mp3")]
        files.sort()
        for filename in files:
            full_path = os.path.join(folder_path, filename)
            item = QListWidgetItem(self.track_list)
            custom_widget = MusicItemWidget(filename)
            item.setSizeHint(custom_widget.sizeHint())
            item.setData(Qt.UserRole, full_path)
            self.track_list.addItem(item)
            self.track_list.setItemWidget(item, custom_widget)
        self.logger.info(f"WIZARD: Loaded {len(files)} tracks from {folder_path}")

    def setup_step1_select(self):
        page = QWidget()
        layout = QVBoxLayout(page)
        layout.setSpacing(0)
        layout.setContentsMargins(0, 0, 0, 0)
        self.lbl_step1 = QLabel("STEP 1: Pick a song from your folder")
        self.lbl_step1.setStyleSheet("font-size: 20px; font-weight: bold; color: #7DD3FC; padding: 0px; margin: 0px;")
        self.lbl_step1.setAlignment(Qt.AlignCenter)
        self.lbl_step1.setFixedHeight(30)
        layout.addWidget(self.lbl_step1)
        layout.addSpacing(10)
        self.coverage_progress = QProgressBar()
        self.coverage_progress.setFixedHeight(25)
        self.coverage_progress.setStyleSheet("""
            QProgressBar {
                background: #1f3545;
                border: 1px solid #34495e;
                border-radius: 6px;
                text-align: center;
                color: white;
                font-weight: bold;
            }
            QProgressBar::chunk {
                background: qlineargradient(x1:0, y1:0, x2:1, y2:0, stop:0 #3498db, stop:1 #2ecc71);
                border-radius: 5px;
            }
        """)
        layout.addWidget(self.coverage_progress)
        layout.addSpacing(15)
        search_layout = QHBoxLayout()
        search_layout.setSpacing(10)
        search_icon = QLabel("🔍")
        search_icon.setStyleSheet("font-size: 18px;")
        self.search_input = QLineEdit()
        self.search_input.setPlaceholderText("Search songs by name...")
        self.search_input.setStyleSheet("""
            QLineEdit {
                background: #0b141d;
                border: 2px solid #1f3545;
                border-radius: 8px;
                padding: 8px 12px;
                color: #ecf0f1;
                font-size: 14px;
            }
            QLineEdit:focus { border-color: #3498db; }
        """)
        self.search_input.textChanged.connect(self._on_search_changed)
        search_layout.addWidget(search_icon)
        search_layout.addWidget(self.search_input)
        layout.addLayout(search_layout)
        layout.addSpacing(10)
        self.track_list = SearchableListWidget()
        self.track_list.setStyleSheet("""
            QListWidget {
                background: #0b141d;
                border: 2px solid #1f3545;
                border-radius: 12px;
                outline: none;
            }
            QListWidget::item {
                background: transparent;
                border: none;
                padding: 0px;
                margin: 0px;
            }
            QListWidget::item:selected {
                background: #3498db;
                border-radius: 4px;
            }
            QScrollBar:vertical {
                width: 22px;
                background: #0b141d;
                border: 1px solid #1f3545;
                border-radius: 10px;
                margin: 2px;
            }
            QScrollBar::handle:vertical {
                min-height: 34px;
                border-radius: 9px;
                border: 1px solid #b8c0c8;
                background: qlineargradient(
                    x1:0, y1:0, x2:1, y2:0,
                    stop:0 #c9d0d6,
                    stop:0.5 #e1e6eb,
                    stop:1 #b6bec6
                );
            }
            QScrollBar::handle:vertical:hover {
                border: 1px solid #d9e0e6;
                background: qlineargradient(
                    x1:0, y1:0, x2:1, y2:0,
                    stop:0 #d7dde2,
                    stop:0.5 #edf1f5,
                    stop:1 #c6cdd4
                );
            }
            QScrollBar::add-line:vertical, QScrollBar::sub-line:vertical {
                height: 0px;
            }
            QScrollBar::add-page:vertical, QScrollBar::sub-page:vertical {
                background: transparent;
            }
        """)
        self.track_list.itemDoubleClicked.connect(self.go_to_offset_step)
        layout.addWidget(self.track_list)
        layout.addSpacing(10)
        self.stack.addWidget(page)

    def setup_step2_offset(self):
        page = QWidget()
        layout = QVBoxLayout(page)
        layout.setSpacing(0)
        layout.setContentsMargins(0, 0, 0, 0)
        self.lbl_step2 = QLabel("STEP 2: Choose music custom starting point in seconds")
        self.lbl_step2.setStyleSheet("font-size: 20px; font-weight: bold; color: #7DD3FC; margin: 0px; padding: 0px;")
        self.lbl_step2.setAlignment(Qt.AlignCenter)
        self.lbl_step2.setFixedHeight(30)
        layout.addWidget(self.lbl_step2)
        layout.addSpacing(10)
        self.wave_container = QFrame()
        self.wave_container.setObjectName("waveContainer")
        self.wave_container.setFixedHeight(270)
        self.wave_container.setStyleSheet("#waveContainer { background: #000; border: 3px solid #266b89; border-radius: 12px; }")
        container_layout = QVBoxLayout(self.wave_container)
        container_layout.setContentsMargins(0, 0, 0, 0)
        container_layout.setSpacing(0)
        self.wave_preview = QLabel("Visualizing audio...")
        self.wave_preview.setStyleSheet("background: transparent; border: none;")
        self.wave_preview.setAlignment(Qt.AlignCenter)
        self.wave_preview.installEventFilter(self)
        container_layout.addWidget(self.wave_preview)
        layout.addWidget(self.wave_container)
        self.slider_unified_container = QWidget()
        self.slider_unified_container.setFixedHeight(100)
        self.slider_unified_layout = QVBoxLayout(self.slider_unified_container)
        self.slider_unified_layout.setContentsMargins(0, 0, 0, 0)
        self.slider_unified_layout.setSpacing(0)
        self.offset_slider = MergerTrimmedSlider()
        self.offset_slider.setProperty("is_wizard_slider", True)
        self.offset_slider.setFixedHeight(100)
        self.offset_slider.setStyleSheet("QSlider::handle:horizontal { width: 10px; background: #2196F3; }")
        self.offset_slider.valueChanged.connect(self._on_slider_seek)
        try:
            self.offset_slider.sliderPressed.connect(self._on_drag_start)
            self.offset_slider.sliderReleased.connect(self._on_drag_end)
        except Exception as ex:
            self.logger.debug("WIZARD: slider drag signal hookup skipped: %s", ex)
        self.slider_unified_layout.addWidget(self.offset_slider)
        layout.addWidget(self.slider_unified_container)
        self._wave_caret = QLabel(self)
        self._wave_caret.setStyleSheet("background: #3498db;")
        self._wave_caret.setFixedWidth(2)
        self._wave_caret.setAttribute(Qt.WA_TransparentForMouseEvents, True)
        self._wave_caret.hide()
        self._wave_time_badge = QLabel(self)
        self._wave_time_badge.setStyleSheet("background: rgba(52, 152, 219, 220); color: white; border-radius: 4px; padding: 2px 6px; font-weight: bold; font-size: 11px;")
        self._wave_time_badge.setAlignment(Qt.AlignCenter)
        self._wave_time_badge.setAttribute(Qt.WA_TransparentForMouseEvents, True)
        self._wave_time_badge.hide()
        self._wave_time_badge_bottom = QLabel(self)
        self._wave_time_badge_bottom.setStyleSheet("background: rgba(52, 152, 219, 220); color: white; border-radius: 4px; padding: 2px 6px; font-weight: bold; font-size: 11px;")
        self._wave_time_badge_bottom.setAlignment(Qt.AlignCenter)
        self._wave_time_badge_bottom.setAttribute(Qt.WA_TransparentForMouseEvents, True)
        self._wave_time_badge_bottom.hide()
        self.stack.addWidget(page)
