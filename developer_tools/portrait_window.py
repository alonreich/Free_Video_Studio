from PyQt5.QtWidgets import (
    QApplication, QWidget, QPushButton, QHBoxLayout, QVBoxLayout,
    QLabel, QGraphicsScene, QGraphicsView, QGraphicsPixmapItem,
    QDialog, QTextEdit, QMessageBox, QGraphicsItem
)
from PyQt5.QtCore import Qt, QSize, QEvent, QTimer
from PyQt5.QtGui import QPainter
from utils import PersistentWindowMixin
from graphics_items import ResizablePixmapItem
from config import PORTRAIT_WINDOW_STYLESHEET

class FinishedDialog(QDialog):

    def __init__(self, data_string, parent=None):
        super(FinishedDialog, self).__init__(parent)
        self.setWindowTitle("Crop Information")
        self.setMinimumWidth(500)
        self.setMinimumHeight(400)
        self.text_edit = QTextEdit(self)
        self.text_edit.setReadOnly(True)
        self.text_edit.setText(data_string)
        self.copy_button = QPushButton("Copy to Clipboard", self)
        self.copy_button.clicked.connect(self.copy_text)
        layout = QVBoxLayout(self)
        layout.addWidget(self.text_edit)
        layout.addWidget(self.copy_button)
        button_size = QSize(160, 40)
        self.copy_button.setFixedSize(button_size)
        self.setLayout(layout)

    def copy_text(self):
        QApplication.clipboard().setText(self.text_edit.toPlainText())
        self.copy_button.setText("Copied!")
        self.copy_button.setEnabled(False)
        self.original_style = self.copy_button.styleSheet()
        self.flash_timer = QTimer(self)
        self.flash_count = 0
        self.flash_timer.timeout.connect(self.flash_animation)
        self.flash_timer.start(150)

    def flash_animation(self):
        if self.flash_count >= 4:
            self.flash_timer.stop()
            self.copy_button.setStyleSheet(self.original_style)
            self.accept()
            return
        if self.flash_count % 2 == 0:
            self.copy_button.setStyleSheet("background-color: #27ae60; color: white;")
        else:
            self.copy_button.setStyleSheet(self.original_style)
        self.flash_count += 1

class PortraitView(QGraphicsView):

    def keyPressEvent(self, event):
        selected_items = self.scene().selectedItems()
        if selected_items:
            item = selected_items[0]
            delta = 1
            key = event.key()
            if key == Qt.Key_Up:
                item.setPos(item.x(), item.y() - delta)
                event.accept()
                return
            elif key == Qt.Key_Down:
                item.setPos(item.x(), item.y() + delta)
                event.accept()
                return
            elif key == Qt.Key_Left:
                item.setPos(item.x() - delta, item.y())
                event.accept()
                return
            elif key == Qt.Key_Right:
                item.setPos(item.x() + delta, item.y())
                event.accept()
                return
        super().keyPressEvent(event)

class PortraitWindow(PersistentWindowMixin, QWidget):

    def __init__(self, original_resolution, config_path, parent=None):
        super(PortraitWindow, self).__init__(parent)
        self.original_resolution = original_resolution
        self.base_title = "Portrait Composer"
        self.setFixedSize(594, 1000)
        self.setup_persistence(
            config_path=config_path,
            settings_key='portrait_window_geometry',
            default_geo={'x': 0, 'y': 0, 'w': 594, 'h': 1000},
            title_info_provider=self.get_title_info
        )
        self.scene = QGraphicsScene(self)
        self.scene.setSceneRect(0, 0, 1280, 1920)
        self.view = PortraitView(self.scene, self)
        self.view.setRenderHint(QPainter.Antialiasing)
        self.view.setRenderHint(QPainter.SmoothPixmapTransform)
        self.view.setHorizontalScrollBarPolicy(Qt.ScrollBarAlwaysOff)
        self.view.setVerticalScrollBarPolicy(Qt.ScrollBarAlwaysOff)
        self.finished_button = QPushButton("Show Cropping Coordinates")
        self.finished_button.clicked.connect(self.on_finished)
        self.finished_button.setEnabled(False)
        self.delete_button = QPushButton("Delete Selected Piece")
        self.delete_button.clicked.connect(self.delete_selected)
        self.delete_button.setEnabled(False)
        self.pos_label = QLabel("Position: ")
        self.scale_label = QLabel("Size: ")
        self.set_style()
        layout = QVBoxLayout(self)
        layout.addWidget(self.view)
        info_layout = QHBoxLayout()
        info_layout.addWidget(self.pos_label)
        info_layout.addWidget(self.scale_label)
        layout.addLayout(info_layout)
        buttons_layout = QHBoxLayout()
        buttons_layout.addWidget(self.finished_button)
        buttons_layout.addWidget(self.delete_button)
        layout.addLayout(buttons_layout)
        button_size = QSize(184, 40)
        self.finished_button.setFixedSize(button_size)
        self.delete_button.setFixedSize(button_size)
        self.setLayout(layout)
        self.scene.selectionChanged.connect(self.on_selection_changed)
        self.on_selection_changed()

    def resizeEvent(self, event):
        super().resizeEvent(event)
        self.view.fitInView(self.scene.sceneRect(), Qt.KeepAspectRatio)

    def showEvent(self, event):
        super().showEvent(event)
        self.view.fitInView(self.scene.sceneRect(), Qt.KeepAspectRatio)

    def get_title_info(self):
        monitor_id = QApplication.desktop().screenNumber(self) + 1
        pos = self.frameGeometry()
        return (
                f"Portrait "
                f"mntr: {monitor_id}  |  "
                f"Pos: x={pos.x()}, y={pos.y()}  |  "
                f"Width: {self.width()}, Height: {self.height()}"
        )

    def set_background(self, pixmap):
        if not pixmap.isNull():
            scaled_pixmap = pixmap.scaled(int(self.scene.width()), int(self.scene.height()), Qt.IgnoreAspectRatio, Qt.SmoothTransformation)
            bg_item = QGraphicsPixmapItem(scaled_pixmap)
            bg_item.setPos(0, 0)
            bg_item.setZValue(-1)
            bg_item.setFlag(QGraphicsItem.ItemIsSelectable, False)
            self.scene.addItem(bg_item)

    def set_style(self):
        self.setStyleSheet(PORTRAIT_WINDOW_STYLESHEET)

    def on_selection_changed(self):
        selected_items = self.scene.selectedItems()
        are_items_selected = bool(selected_items)
        self.finished_button.setEnabled(are_items_selected)
        self.delete_button.setEnabled(are_items_selected)
        if are_items_selected:
            self.finished_button.setStyleSheet("background-color: #27ae60; color: white;")
            self.delete_button.setStyleSheet("background-color: #c0392b; color: white;")
            item = selected_items[0]
            if len(selected_items) > 1:
                for i in selected_items[1:]:
                    i.setSelected(False)
            self.update_item_info(item)
        else:
            grey_style = "background-color: #95a5a6; color: #bdc3c7;"
            self.finished_button.setStyleSheet(grey_style)
            self.delete_button.setStyleSheet(grey_style)
            self.pos_label.setText("Position: ")
            self.scale_label.setText("Size: ")

    def add_scissored_item(self, pixmap, crop_rect, background_crop_width):
        item = ResizablePixmapItem(pixmap, crop_rect)
        if background_crop_width > 0:
            visual_scale_factor = 1280 / background_crop_width
            item.current_width *= visual_scale_factor
            item.current_height *= visual_scale_factor
            item.update_handle_positions()
        self.scene.addItem(item)
        item.setPos(self.scene.width()/2 - item.boundingRect().width()/2,
                    self.scene.height()/2 - item.boundingRect().height()/2)
        item.setSelected(True)

    def update_item_info(self, item):
        pos = item.pos()
        scale_x = 1280 / self.scene.width() 
        scale_y = 1920 / self.scene.height() 
        real_x = pos.x() * scale_x
        real_y = pos.y() * scale_y
        self.pos_label.setText(f"Pos (1280x1920): x={real_x:.0f}, y={real_y:.0f}")
        current_w = item.boundingRect().width()
        current_h = item.boundingRect().height()
        real_w = current_w * scale_x
        real_h = current_h * scale_y
        self.scale_label.setText(f"Size: {real_w:.0f}x{real_h:.0f}")

    def delete_selected(self):
        for item in self.scene.selectedItems():
            self.scene.removeItem(item)
        self.on_selection_changed()

    def closeEvent(self, event):
        if hasattr(self, 'parent_window') and self.parent_window:
            self.parent_window.show()
        super().closeEvent(event)

    def on_finished(self):
        data = []
        data.append(f"Original Video Resolution: {getattr(self, 'original_resolution', 'N/A')}")
        data.append("Target Canvas: 1280x1920")
        selected_items = self.scene.selectedItems()
        if not selected_items:
            QMessageBox.information(self, "No Item Selected", "Please select a cropped piece to show its coordinates.")
            return
        item = selected_items[0]
        if isinstance(item, ResizablePixmapItem):
            data.append("---")
            crop_rect = item.crop_rect
            data.append(f"SOURCE CROP (Original): crop={crop_rect.width()}:{crop_rect.height()}:{crop_rect.x()}:{crop_rect.y()}")
            pos = item.pos()
            scale_x = 1280 / self.scene.width() 
            scale_y = 1920 / self.scene.height() 
            real_x = pos.x() * scale_x
            real_y = pos.y() * scale_y
            current_rect = item.boundingRect()
            target_w = current_rect.width() * scale_x
            target_h = current_rect.height() * scale_y
            data.append(f"FFMPEG COMMANDS (1:1 Match):")
            data.append(f"  filter: scale={target_w:.0f}:{target_h:.0f}")
            data.append(f"  overlay: {real_x:.0f}:{real_y:.0f}")
        dialog = FinishedDialog("\n".join(data), self)
        dialog.exec_()