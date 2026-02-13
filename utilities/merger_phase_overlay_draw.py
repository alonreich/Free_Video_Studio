class MergerPhaseOverlayDraw:
    def _draw_performance_graphs(self, event):
        """Draws 4 separate horizontal timelines of 10x45px sticks."""
        if not hasattr(self, "_graph"): return
        painter = QPainter(self._graph)
        painter.setRenderHint(QPainter.Antialiasing)
        w = self._graph.width()
        painter.fillRect(self._graph.rect(), QColor(11, 20, 29, 180))
        try:
            cpu_data = list(getattr(self, "_cpu_hist", []))
            gpu_data = list(getattr(self, "_gpu_hist", []))
            mem_data = list(getattr(self, "_mem_hist", []))
            io_data = list(getattr(self, "_iops_hist", []))
        except:
            cpu_data = gpu_data = mem_data = io_data = []
        metrics = [
            (cpu_data, "#3498db", "CPU"),
            (gpu_data, "#e74c3c", "GPU"),
            (mem_data, "#2ecc71", "MEM"),
            (io_data, "#f1c40f", "I/O")
        ]
        stick_w = 10
        stick_max_h = 45
        gap = 2
        row_spacing = 55
        start_x = 75 
        painter.setFont(QFont("Segoe UI", 9, QFont.Bold))
        for idx, (data, color, label) in enumerate(metrics):
            y_base = idx * row_spacing + 10
            cur_val = data[-1] if data else 0
            painter.setPen(QColor("white"))
            painter.drawText(5, y_base + 18, label)
            painter.setPen(QColor(color))
            painter.drawText(5, y_base + 38, f"{cur_val}%")
            if not data: continue
            max_visible_sticks = (w - start_x) // (stick_w + gap)
            visible_data = data[-max_visible_sticks:]
            for i, val in enumerate(visible_data):
                x = start_x + i * (stick_w + gap)
                painter.fillRect(x, y_base, stick_w, stick_max_h, QColor(31, 53, 69, 100))
                fill_h = max(1, int((val / 100.0) * stick_max_h))
                painter.fillRect(x, y_base + stick_max_h - fill_h, stick_w, fill_h, QBrush(QColor(color)))
                painter.setPen(QPen(QColor(color).lighter(130), 1))
                painter.drawLine(x, y_base + stick_max_h - fill_h, x + stick_w - 1, y_base + stick_max_h - fill_h)
            if idx < len(metrics) - 1:
                line_y = y_base + row_spacing - 5
                painter.setPen(QPen(QColor(16, 185, 129, 60), 2)) 
                painter.drawLine(0, line_y, w, line_y)
        painter.end()
