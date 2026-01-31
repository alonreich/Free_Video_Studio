"""
Test script for swap animation geometry fix.
"""

import sys
import os
from PyQt5.QtWidgets import QApplication, QMainWindow, QVBoxLayout, QWidget, QListWidget, QListWidgetItem, QLabel
from PyQt5.QtCore import Qt
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

class TestSwapWindow(QMainWindow):
    def __init__(self):
        super().__init__()
        self.setWindowTitle("Swap Animation Test")
        self.resize(600, 400)
        central_widget = QWidget()
        self.setCentralWidget(central_widget)
        layout = QVBoxLayout(central_widget)
        layout.setContentsMargins(20, 20, 20, 20)
        layout.setSpacing(20)
        self.listw = QListWidget()
        self.listw.setAlternatingRowColors(False)
        self.listw.setSpacing(6)
        test_items = [
            "Video 1 - Test File 1.mp4",
            "Video 2 - Test File 2.mp4",
            "Video 3 - Test File 3.mp4",
            "Video 4 - Test File 4.mp4",
            "Video 5 - Test File 5.mp4"
        ]
        for i, text in enumerate(test_items):
            item = QListWidgetItem(text)
            item.setData(Qt.UserRole, f"/fake/path/video{i+1}.mp4")
            self.listw.addItem(item)
            widget = QWidget()
            widget_layout = QVBoxLayout(widget)
            label = QLabel(text)
            label.setObjectName("fileLabel")
            widget_layout.addWidget(label)
            self.listw.setItemWidget(item, widget)
        layout.addWidget(self.listw)
        self.status_label = QLabel("Ready. Select items and test swapping.")
        self.status_label.setAlignment(Qt.AlignCenter)
        layout.addWidget(self.status_label)
        self.test_swap_logic()
    
    def test_swap_logic(self):
        """Test the swap logic to ensure geometry doesn't get distorted."""
        print("Testing swap logic...")
        item1 = self.listw.item(0)
        item2 = self.listw.item(1)
        print(f"Before swap:")
        print(f"  Item 0: {item1.text()}, data: {item1.data(Qt.UserRole)}")
        print(f"  Item 1: {item2.text()}, data: {item2.data(Qt.UserRole)}")
        d1 = item1.data(Qt.UserRole)
        d2 = item2.data(Qt.UserRole)
        item1.setData(Qt.UserRole, d2)
        item2.setData(Qt.UserRole, d1)
        t1 = item1.text()
        t2 = item2.text()
        item1.setText(t2)
        item2.setText(t1)
        print(f"After swap:")
        print(f"  Item 0: {item1.text()}, data: {item1.data(Qt.UserRole)}")
        print(f"  Item 1: {item2.text()}, data: {item2.data(Qt.UserRole)}")
        widget1 = self.listw.itemWidget(item1)
        widget2 = self.listw.itemWidget(item2)
        if widget1 and widget2:
            print(f"Widget 1 geometry: {widget1.geometry()}")
            print(f"Widget 2 geometry: {widget2.geometry()}")
            label1 = widget1.findChild(QLabel, "fileLabel")
            label2 = widget2.findChild(QLabel, "fileLabel")
            if label1 and label2:
                label1.setText(item1.text())
                label2.setText(item2.text())
                print(f"Widget labels updated successfully")
        self.status_label.setText("Swap test completed. Check console for output.")

def main():
    app = QApplication(sys.argv)
    window = TestSwapWindow()
    window.show()
    sys.exit(app.exec_())
if __name__ == "__main__":
    main()