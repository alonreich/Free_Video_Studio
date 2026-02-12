from PyQt5.QtGui import QPainter, QColor, QFont, QPen, QBrush
from PyQt5.QtCore import Qt

class MergerPhaseOverlayDraw:
    def _draw_performance_graphs(self, event):
        painter = QPainter(self._graph)
        painter.setRenderHint(QPainter.Antialiasing)
        w = self._graph.width()
        painter.fillRect(self._graph.rect(), QColor(11, 20, 29, 120))
        metrics = [
            (list(self._cpu_hist), "#3498db", "CPU"),
            (list(self._gpu_hist), "#e74c3c", "GPU"),
            (list(self._mem_hist), "#2ecc71", "MEM"),
            (list(self._iops_hist), "#f1c40f", "I/O")
        ]
        stick_w, stick_max_h, gap, row_spacing, start_x = 10, 45, 2, 55, 75
        painter.setFont(QFont("Segoe UI", 9, QFont.Bold))
        for idx, (data, color, label) in enumerate(metrics):
            y_base = idx * row_spacing + 10
            cur_val = data[-1] if data else 0
            painter.setPen(QColor("white"))
            painter.drawText(5, y_base + 18, label)
            painter.setPen(QColor(color))
            painter.drawText(5, y_base + 38, f"{cur_val}%")
            if not data: continue
            for i, val in enumerate(data):
                x = start_x + i * (stick_w + gap)
                if x + stick_w > w:
                    offset = (i * (stick_w + gap)) - (w - start_x - stick_w)
                    x = start_x + i * (stick_w + gap) - offset
                    if x < start_x: continue
                painter.fillRect(x, y_base, stick_w, stick_max_h, QColor(31, 53, 69, 80))
                fill_h = max(1, int((val / 100.0) * stick_max_h))
                painter.fillRect(x, y_base + stick_max_h - fill_h, stick_w, fill_h, QBrush(QColor(color)))
                painter.setPen(QPen(QColor(color).lighter(130), 1))
                painter.drawLine(x, y_base + stick_max_h - fill_h, x + stick_w - 1, y_base + stick_max_h - fill_h)
            if idx < len(metrics) - 1:
                line_y = y_base + row_spacing - 5
                painter.setPen(QPen(QColor(16, 185, 129, 60), 2))
                painter.drawLine(0, line_y, w, line_y)
