## [2026-02-11T12:08:45Z] Modification
**File:** `c:/Fortnite_Video_Software/developer_tools/utils.py`
**Reason:** Remove redundant unused Qt imports for hygiene and startup clarity.
**Location:** Lines 4-4

**ORIGINAL CODE (What was removed):**
```python
from PyQt5.QtCore import QSettings, QByteArray, QTimer
```

## [2026-02-11T12:08:45Z] Modification
**File:** `c:/Fortnite_Video_Software/developer_tools/app_handlers.py`
**Reason:** Offload snapshot capture from GUI thread, remove local re-import noise, and add safer input validation.
**Location:** Lines 3-5, 468-480, 539-548, 587-589

**ORIGINAL CODE (What was removed):**
```python
from PyQt5.QtWidgets import QApplication, QFileDialog, QMessageBox
from PyQt5.QtCore import QTimer, QThread, Qt

def load_file(self, file_path):
    """[FIX #12] Defer slider and status updates until media length is confirmed."""
    self.last_dir = os.path.dirname(file_path)
    if hasattr(self, 'save_geometry'):
        self.save_geometry()
    loaded_ok = self.media_processor.load_media(file_path, self.video_frame.winId())
    if not loaded_ok:
        QMessageBox.critical(self, "Video Load Error", "Failed to load this video. Please choose another file.")
        self.play_pause_button.setEnabled(False)
        self.snapshot_button.setEnabled(False)
        self._set_upload_hint_active(True)
        self.timer.start()
        return

def _execute_snapshot_capture(self, path, time_val):
    """[FIX #2] Robust snapshot execution with explicit verification."""
    success, message = self.media_processor.take_snapshot(path, time_val)
    if success: 
        QTimer.singleShot(UI_BEHAVIOR.SNAPSHOT_RETRY_INTERVAL_MS, self._check_and_show_snapshot)
    else: 
        if hasattr(self, 'status_label'):
            self.status_label.setText(f"Snapshot Error: {message}")
        self._reset_snapshot_ui()

from PyQt5.QtWidgets import QApplication
QApplication.processEvents()
```

## [2026-02-11T12:08:45Z] Modification
**File:** `c:/Fortnite_Video_Software/developer_tools/media_processor.py`
**Reason:** Harden subprocess timeout/cleanup flow and guarantee temporary snapshot file cleanup.
**Location:** Lines 9, 103-104, 125-127, 222-261, 283-300

**ORIGINAL CODE (What was removed):**
```python
from PyQt5.QtCore import QTimer, QObject, pyqtSignal

import shutil
system_path = shutil.which(name)

import threading
thread = threading.Thread(target=self.get_video_info, args=(file_path,), daemon=True)
thread.start()

proc_json = subprocess.Popen(cmd_json, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, creationflags=creation_flags)
self._ffprobe_procs = [proc_json]
start_time = time.time()
while time.time() - start_time < 3.0:
    if proc_json.poll() is not None:
        out, _ = proc_json.communicate()
        if proc_json.returncode == 0:
            try:
                import json
                data = json.loads(out)
                w = data['streams'][0]['width']
                h = data['streams'][0]['height']
                self.original_resolution = f"{w}x{h}"
                logger.info(f"ffprobe (JSON) resolution: {self.original_resolution}")
                self.info_retrieved.emit(self.original_resolution)
                self._kill_ffprobe_procs()
                return self.original_resolution
            except Exception as e:
                logger.warning(f"Failed to parse ffprobe JSON: {e}")
    time.sleep(0.1)
if not self.original_resolution:
    cmd_csv = [
        ffprobe_path, '-v', 'error', '-select_streams', 'v:0',
        '-show_entries', 'stream=width,height', '-of', 'csv=p=0:s=x',
        file_path
    ]
    proc_csv = subprocess.run(cmd_csv, capture_output=True, text=True, creationflags=creation_flags)
    if proc_csv.returncode == 0:
        res = proc_csv.stdout.strip()
        if res:
            self.original_resolution = res
            logger.info(f"ffprobe (CSV) fallback resolution: {self.original_resolution}")
            self.info_retrieved.emit(self.original_resolution)
            return self.original_resolution
QTimer.singleShot(500, lambda: self._fetch_vlc_resolution())
return None

subprocess.run(
    cmd, check=True, capture_output=True,
    creationflags=(subprocess.CREATE_NO_WINDOW if sys.platform == "win32" else 0)
)
if os.path.exists(temp_path) and os.path.getsize(temp_path) > 0:
    os.replace(temp_path, snapshot_path)
    return True, "Snapshot created."
return False, "Snapshot file empty."
```

## [2026-02-11T12:08:45Z] Modification
**File:** `c:/Fortnite_Video_Software/developer_tools/crop_tools.py`
**Reason:** Add deterministic media teardown on window close to reduce handle leaks.
**Location:** Lines 1003-1012

**ORIGINAL CODE (What was removed):**
```python
def closeEvent(self, event):
    if self._confirm_discard_changes():
        if hasattr(self, '_autosave_file') and os.path.exists(self._autosave_file):
            try: os.unlink(self._autosave_file)
            except: pass
        try: cleanup_temp_snapshots()
        except: pass
        super().closeEvent(event)
    else:
        event.ignore()
```

## [2026-02-11T14:50:30Z] Modification
**File:** `c:/Fortnite_Video_Software/developer_tools/app_handlers.py`
**Reason:** Expand file picker formats and stop snapshot worker thread during reset.
**Location:** Lines 473-485, 414-421

**ORIGINAL CODE (What was removed):**
```python
file_path, _ = QFileDialog.getOpenFileName(self, "Open Video", self.last_dir or "", "Video Files (*.mp4 *.avi *.mkv)")

for attr in ['_magic_wand_preview_timer', '_magic_wand_timeout_timer', '_analyzing_timer', '_scrubbing_safety_timer']:
    timer = getattr(self, attr, None)
    if timer: 
        try:
            timer.stop()
        except RuntimeError as timer_err:
            self.logger.debug(f"Timer stop skipped for {attr}: {timer_err}")
```

## [2026-02-11T14:51:10Z] Modification
**File:** `c:/Fortnite_Video_Software/developer_tools/crop_tools.py`
**Reason:** Remove confirmed redundant imports and add validated drag/drop UX path for single video file intake.
**Location:** Lines 14-17, 32-34, 127-130, 293-295, 993+

**ORIGINAL CODE (What was removed):**
```python
import ctypes
import psutil
from config import CROP_APP_STYLESHEET
from system.shared_paths import SharedPaths
QShortcut(QKeySequence("I"), self).activated.connect(self.open_image_button.click)
import time
```

## [2026-02-11T14:52:55Z] Modification
**File:** `c:/Fortnite_Video_Software/developer_tools/app_handlers.py`
**Reason:** Replace blocked image fallback with validated screenshot workflow for VLC-missing mode and safer UX.
**Location:** Lines 122-129

**ORIGINAL CODE (What was removed):**
```python
def open_image_fallback(self):
    """Enforce video-only workflow for production consistency."""
    QMessageBox.information(
        self,
        "Video Required",
        "This workflow supports video input only. Please upload a video file."
    )
```

## [2026-02-11T15:00:40Z] Modification
**File:** `c:/Fortnite_Video_Software/developer_tools/app_handlers.py`
**Reason:** Prevent `QThread: Destroyed while thread is still running` by keeping the snapshot thread alive until `finished` and only clearing references after thread shutdown.
**Location:** Lines 624-646

**ORIGINAL CODE (What was removed):**
```python
def _execute_snapshot_capture(self, path, time_val):
    """Run snapshot capture off the GUI thread and return via signal."""
    if getattr(self, '_snapshot_thread', None) and self._snapshot_thread.isRunning():
        return
    self._snapshot_thread = QThread()
    self._snapshot_worker = SnapshotWorker(self.media_processor, path, time_val)
    self._snapshot_worker.moveToThread(self._snapshot_thread)
    self._snapshot_thread.started.connect(self._snapshot_worker.run)
    self._snapshot_worker.finished.connect(self._on_snapshot_capture_result)
    self._snapshot_worker.finished.connect(self._snapshot_thread.quit)
    self._snapshot_worker.finished.connect(self._snapshot_worker.deleteLater)
    self._snapshot_thread.finished.connect(self._snapshot_thread.deleteLater)
    self._snapshot_thread.start()

def _on_snapshot_capture_result(self, success, message):
    self._snapshot_worker = None
    self._snapshot_thread = None
    if success:
        QTimer.singleShot(UI_BEHAVIOR.SNAPSHOT_RETRY_INTERVAL_MS, self._check_and_show_snapshot)
        return
    if hasattr(self, 'status_label'):
        self.status_label.setText(f"Snapshot Error: {message}")
    self._reset_snapshot_ui()
```

## [2026-02-11T15:04:50Z] Modification
**File:** `c:/Fortnite_Video_Software/developer_tools/crop_tools.py`
**Reason:** Fix upload guidance overlay centering and clipping by anchoring to video area bounds and removing hardcoded offset geometry.
**Location:** Lines 387-443

**ORIGINAL CODE (What was removed):**
```python
def _update_upload_hint_responsive(self):
    if not hasattr(self, 'upload_hint_container'):
        return

    from PyQt5.QtGui import QPolygon
    scale = (self.width() / self.REF_WIDTH) * 0.9
    box_w, box_h = int(self.REF_BOX_W * scale), int(self.REF_BOX_H * scale)
    font_size = int(self.REF_FONT_SIZE * scale)
    self.upload_hint_container.setFixedSize(box_w, box_h)
    self.upload_hint_container.setStyleSheet(f"background-color: #000000; border: {max(2, int(3*scale))}px solid #7DD3FC; border-radius: {int(14*scale)}px;")
    self.upload_hint_label.setStyleSheet(f"color: #7DD3FC; font-family: Arial; font-size: {font_size}px; font-weight: bold; background: transparent;")
    if self.hint_group_layout.direction() != QHBoxLayout.TopToBottom:
        self.hint_group_layout.setDirection(QHBoxLayout.TopToBottom)
        self.hint_group_layout.setAlignment(Qt.AlignCenter)
    gap = 60
    self.hint_group_layout.setSpacing(gap)
    c_w, c_h = 1200, 600
    self.upload_hint_arrow.setFixedSize(c_w, c_h)
    self.upload_hint_arrow.setContentsMargins(0, 0, 0, 0)
    pix = QPixmap(c_w, c_h)
    pix.fill(Qt.transparent)
    p = QPainter(pix)
    p.setRenderHint(QPainter.Antialiasing)
    p.setBrush(QColor("#7DD3FC"))
    p.setPen(QPen(QColor("#7DD3FC"), max(2, int(10*scale)), Qt.SolidLine, Qt.FlatCap, Qt.MiterJoin))
    draw_shift_left = 565 
    start_pt = QPoint(c_w // 2, 10)
    end_pt = QPoint(c_w // 2 - 390, 10 + 290)
    p.drawLine(start_pt, end_pt + QPoint(12, -10))
    p.setPen(Qt.NoPen)
    h_s = int(42 * scale) 
    head = QPolygon([
        end_pt,
        QPoint(end_pt.x() + h_s, end_pt.y() - int(h_s * 0.35)),
        QPoint(end_pt.x() + int(h_s * 0.35), end_pt.y() - h_s)
    ])
    p.drawPolygon(head)
    p.end()
    self.upload_hint_arrow.setPixmap(pix)
    self._apply_hint_position()

def _apply_hint_position(self):
    if not hasattr(self, 'hint_group_container'): return
    try:
        scale = (self.width() / self.REF_WIDTH) * 0.9
        box_w = int(self.REF_BOX_W * scale)
        box_h = int(self.REF_BOX_H * scale)
        gap = 60
        c_w, c_h = 1200, 600
        win_w, win_h = self.width(), self.height()
        draw_shift_left = 565 
        target_x = (win_w - box_w) // 2 - draw_shift_left
        target_y = (win_h - (box_h + gap + c_h)) // 2 + 290
        self.hint_group_container.setFixedSize(max(box_w, c_w), box_h + gap + c_h)
            self.hint_group_container.move(target_x, target_y)
        except Exception:
            pass
```

## [2026-02-11T15:18:20Z] Modification
**File:** `c:/Fortnite_Video_Software/developer_tools/crop_tools.py`
**Reason:** Restore larger hint scale and force reliable centering in visible video area after layout settles.
**Location:** Lines 372-383, 392-455

**ORIGINAL CODE (What was removed):**
```python
if active:
    if target.parent() is getattr(self, 'video_frame', None):
        target.setGeometry(self.video_frame.rect())
    self._update_upload_hint_responsive()
    target.show()
    target.raise_()
    self._hint_group.start()

host = getattr(self, 'hint_overlay_widget', None) or getattr(self, 'video_frame', None) or self
host_w = max(1, host.width())
host_h = max(1, host.height())
scale = max(0.45, min(1.25, (host_w / self.REF_WIDTH) * 0.9))

host = getattr(self, 'hint_overlay_widget', None) or getattr(self, 'video_frame', None) or self
win_w, win_h = host.width(), host.height()
box_w = self.upload_hint_container.width()
box_h = self.upload_hint_container.height()
c_w = self.upload_hint_arrow.width()
c_h = self.upload_hint_arrow.height()
gap = self.hint_group_layout.spacing()

container_w = max(box_w, c_w)
container_h = box_h + gap + c_h
self.hint_group_container.setFixedSize(container_w, container_h)

target_x = max(0, (win_w - container_w) // 2)
target_y = max(0, (win_h - container_h) // 2)
self.hint_group_container.move(target_x, target_y)
```

## [2026-02-11T15:31:55Z] Modification
**File:** `c:/Fortnite_Video_Software/developer_tools/crop_tools.py`
**Reason:** Fix right-side clipping and intermittent top-left placement by sizing hint box from text metrics and re-centering after layout settles.
**Location:** Lines 26, 372-383, 387-456

**ORIGINAL CODE (What was removed):**
```python
from PyQt5.QtGui import QPainter, QColor, QFont, QBrush, QPixmap, QPen, QPolygon, QKeySequence

if active:
    if target.parent() is getattr(self, 'video_frame', None):
        target.setGeometry(self.video_frame.rect())
    self._update_upload_hint_responsive()
    target.show()
    target.raise_()
    QTimer.singleShot(0, self._update_upload_hint_responsive)
    self._hint_group.start()

host = getattr(self, 'video_frame', None) or getattr(self, 'hint_overlay_widget', None) or self
host_w = max(1, host.width())
host_h = max(1, host.height())
scale = max(0.85, min(1.35, (host_w / self.REF_WIDTH) * 0.95))

box_w = min(int(self.REF_BOX_W * scale), max(280, host_w - 40))
box_h = int(self.REF_BOX_H * scale)
font_size = int(self.REF_FONT_SIZE * scale)
self.upload_hint_container.setFixedSize(box_w, box_h)
self.upload_hint_container.setStyleSheet(f"background-color: #000000; border: {max(2, int(3*scale))}px solid #7DD3FC; border-radius: {int(14*scale)}px;")
self.upload_hint_label.setStyleSheet(f"color: #7DD3FC; font-family: Arial; font-size: {font_size}px; font-weight: bold; background: transparent;")

host = getattr(self, 'video_frame', None) or getattr(self, 'hint_overlay_widget', None) or self
win_w, win_h = host.width(), host.height()
if win_w < 50 or win_h < 50:
    return
box_w = self.upload_hint_container.width()
box_h = self.upload_hint_container.height()
c_w = self.upload_hint_arrow.width()
c_h = self.upload_hint_arrow.height()
gap = self.hint_group_layout.spacing()

container_w = max(box_w, c_w)
container_h = box_h + gap + c_h
self.hint_group_container.setFixedSize(container_w, container_h)

target_x = max(0, (win_w - container_w) // 2)
target_y = max(0, (win_h - container_h) // 2)
self.hint_group_container.move(target_x, target_y)
```

## [2026-02-11T15:40:40Z] Modification
**File:** `c:/Fortnite_Video_Software/developer_tools/crop_tools.py`
**Reason:** Eliminate persistent right-edge clipping by sizing the upload hint from label size hints plus layout margins, with bounded auto-downscale and strict container clamping.
**Location:** Lines 389-479

**ORIGINAL CODE (What was removed):**
```python
def _update_upload_hint_responsive(self):
    if not hasattr(self, 'upload_hint_container'):
        return

    from PyQt5.QtGui import QPolygon
    host = getattr(self, 'video_frame', None) or getattr(self, 'hint_overlay_widget', None) or self
    host_w = max(1, host.width())
    host_h = max(1, host.height())
    scale = max(0.95, min(1.35, (host_w / self.REF_WIDTH) * 0.98))

    max_box_w = max(280, host_w - 24)
    min_box_w = min(max_box_w, int(self.REF_BOX_W * scale))
    box_h = int(self.REF_BOX_H * scale)
    font_size = max(18, int(self.REF_FONT_SIZE * scale))

    label_font = QFont("Arial", font_size, QFont.Bold)
    text_metrics = QFontMetrics(label_font)
    text_w = text_metrics.horizontalAdvance(self.upload_hint_label.text())
    padded_text_w = text_w + 48
    box_w = min(max_box_w, max(min_box_w, padded_text_w))

    while box_w < padded_text_w and font_size > 14:
        font_size -= 1
        label_font = QFont("Arial", font_size, QFont.Bold)
        text_metrics = QFontMetrics(label_font)
        text_w = text_metrics.horizontalAdvance(self.upload_hint_label.text())
        padded_text_w = text_w + 48

    self.upload_hint_container.setFixedSize(box_w, box_h)
    self.upload_hint_container.setStyleSheet(f"background-color: #000000; border: {max(2, int(3*scale))}px solid #7DD3FC; border-radius: {int(14*scale)}px;")
    self.upload_hint_label.setStyleSheet(f"color: #7DD3FC; font-family: Arial; font-size: {font_size}px; font-weight: bold; background: transparent;")
    self.upload_hint_label.setAlignment(Qt.AlignCenter)
    self.upload_hint_label.setWordWrap(False)
    if self.upload_hint_container.layout() is not None:
        self.upload_hint_container.layout().setContentsMargins(20, 10, 20, 10)
    if self.hint_group_layout.direction() != QHBoxLayout.TopToBottom:
        self.hint_group_layout.setDirection(QHBoxLayout.TopToBottom)
        self.hint_group_layout.setAlignment(Qt.AlignCenter)

    gap = max(24, int(40 * scale))
    self.hint_group_layout.setSpacing(gap)

    c_w = min(max(box_w + 40, int(host_w * 0.8)), max(320, host_w - 20))
    c_h = min(max(120, int(host_h * 0.28)), 240)
    self.upload_hint_arrow.setFixedSize(c_w, c_h)
    self.upload_hint_arrow.setContentsMargins(0, 0, 0, 0)

    pix = QPixmap(c_w, c_h)
    pix.fill(Qt.transparent)
    p = QPainter(pix)
    p.setRenderHint(QPainter.Antialiasing)
    p.setBrush(QColor("#7DD3FC"))
    p.setPen(QPen(QColor("#7DD3FC"), max(2, int(8 * scale)), Qt.SolidLine, Qt.FlatCap, Qt.MiterJoin))

    start_pt = QPoint(c_w // 2, 8)
    end_pt = QPoint(c_w // 2, c_h - max(18, int(20 * scale)))
    p.drawLine(start_pt, end_pt)
    p.setPen(Qt.NoPen)
    h_s = max(14, int(26 * scale))
    head = QPolygon([
        end_pt,
        QPoint(end_pt.x() - h_s, end_pt.y() - h_s),
        QPoint(end_pt.x() + h_s, end_pt.y() - h_s)
    ])
    p.drawPolygon(head)
    p.end()
    self.upload_hint_arrow.setPixmap(pix)
    self._apply_hint_position()

def _apply_hint_position(self):
    if not hasattr(self, 'hint_group_container'): return
    try:
        host = getattr(self, 'video_frame', None) or getattr(self, 'hint_overlay_widget', None) or self
        win_w, win_h = host.width(), host.height()
        if win_w < 50 or win_h < 50:
            return
        box_w = self.upload_hint_container.width()
        box_h = self.upload_hint_container.height()
        c_w = self.upload_hint_arrow.width()
        c_h = self.upload_hint_arrow.height()
        gap = self.hint_group_layout.spacing()

        container_w = max(box_w, c_w)
        container_h = box_h + gap + c_h
        self.hint_group_container.setFixedSize(container_w, container_h)

        target_x = max(0, (win_w - container_w) // 2)
        target_y = max(0, (win_h - container_h) // 2)
        self.hint_group_container.move(target_x, target_y)
            self.hint_group_container.raise_()
        except Exception:
            pass
```

## [2026-02-11T15:42:20Z] Modification
**File:** `c:/Fortnite_Video_Software/developer_tools/crop_tools.py`
**Reason:** Remove remaining right-edge clipping by making hint container fill the overlay and letting centered layout position children instead of moving a fixed-size container.
**Location:** Lines 431-490

**ORIGINAL CODE (What was removed):**
```python
self.upload_hint_label.setAlignment(Qt.AlignCenter)
self.upload_hint_label.setWordWrap(False)
if self.upload_hint_container.layout() is not None:
    self.upload_hint_container.layout().setContentsMargins(margin_lr, margin_tb, margin_lr, margin_tb)
if self.hint_group_layout.direction() != QHBoxLayout.TopToBottom:
    self.hint_group_layout.setDirection(QHBoxLayout.TopToBottom)
    self.hint_group_layout.setAlignment(Qt.AlignCenter)

def _apply_hint_position(self):
    if not hasattr(self, 'hint_group_container'): return
    try:
        host = getattr(self, 'video_frame', None) or getattr(self, 'hint_overlay_widget', None) or self
        win_w, win_h = host.width(), host.height()
        if win_w < 50 or win_h < 50:
            return
        box_w = self.upload_hint_container.width()
        box_h = self.upload_hint_container.height()
        c_w = self.upload_hint_arrow.width()
        c_h = self.upload_hint_arrow.height()
        gap = self.hint_group_layout.spacing()

        container_w = min(win_w, max(box_w, c_w))
        container_h = box_h + gap + c_h
        self.hint_group_container.setFixedSize(container_w, container_h)

        target_x = max(0, (win_w - container_w) // 2)
        target_y = max(0, (win_h - container_h) // 2)
        self.hint_group_container.move(target_x, target_y)
        self.hint_group_container.raise_()
    except Exception:
        pass
```

## [2026-02-11T16:49:10Z] Modification (Items #1, #12)
**File:** `c:/Fortnite_Video_Software/developer_tools/app_handlers.py`
**Reason:** Prevent deleting user-owned fallback images and make style/state enums active in runtime flow.
**Location:** Lines 95-99, 121-151, 174-180, 442-447

**ORIGINAL CODE (What was removed):**
```python
def set_style(self):
    from config import UNIFIED_STYLESHEET
    style = CROP_APP_STYLESHEET or UNIFIED_STYLESHEET
    self.setStyleSheet(style)

def open_image_fallback(self):
    """Fallback path for VLC-missing environments: load a local screenshot safely."""
    image_path, _ = QFileDialog.getOpenFileName(
        self,
        "Open Screenshot",
        self.last_dir or "",
        "Image Files (*.png *.jpg *.jpeg *.bmp *.webp)"
    )
    if not image_path:
        return
    if not os.path.isfile(image_path):
        QMessageBox.warning(self, "Invalid Image", "Selected image does not exist.")
        return
    ext = os.path.splitext(image_path)[1].lower()
    if ext not in {'.png', '.jpg', '.jpeg', '.bmp', '.webp'}:
        QMessageBox.warning(self, "Unsupported Image", f"Unsupported image type: {ext or 'unknown'}")
        return
    pix = QPixmap(image_path)
    if pix.isNull():
        QMessageBox.warning(self, "Image Load Error", "Could not decode this image file.")
        return
    self.last_dir = os.path.dirname(image_path)
    self.snapshot_path = image_path
    try:
        if hasattr(self, 'media_processor') and self.media_processor:
            self.media_processor.stop()
            self.media_processor.set_media_to_null()
    except Exception:
        pass
    self._set_upload_hint_active(False)
    self.set_background_image(pix)
    self.view_stack.setCurrentWidget(self.draw_scroll_area)

goal_map = {
    1: "UPLOAD VIDEO",
    2: "FIND HUD FRAME",
    3: "REFINE BOX",
    4: "PORTRAIT COMPOSER",
    5: "CONFIG READY"
}

if hasattr(self, 'snapshot_path') and self.snapshot_path and os.path.exists(self.snapshot_path):
    try: os.unlink(self.snapshot_path)
    except Exception as e: self.logger.warning(f"Failed to delete snapshot: {e}")
```

## [2026-02-11T16:49:10Z] Modification (Items #2, #6)
**File:** `c:/Fortnite_Video_Software/developer_tools/media_processor.py`
**Reason:** Add shared-state locking for media metadata and timeout protection for FFmpeg snapshot extraction.
**Location:** Lines 40-47, 65-67, 97-99, 293-319

**ORIGINAL CODE (What was removed):**
```python
def __init__(self, bin_dir):
    super().__init__()
    self.bin_dir = bin_dir
    self.vlc_log_path = os.path.join(get_vlc_log_dir(), "vlc_errors.log")
    self._ffprobe_procs = []
    self._ffprobe_lock = threading.Lock()
    atexit.register(self._kill_ffprobe_procs)

if vlc is None:
    logger.error("python-vlc module is unavailable. MediaProcessor will run in no-playback mode.")
    self.vlc_instance = None
    self.media_player = None
    self.media = None
    self.original_resolution = None
    self.input_file_path = None
    return

self.media = None
self.original_resolution = None
self.input_file_path = None
self._ffprobe_procs = []
self._last_seek_time = 0

def take_snapshot(self, snapshot_path, preferred_time=None):
    """[FIX #8, #11] Reliable snapshot with atomic overwrite."""
    if self.is_playing():
        self.media_player.pause()
    if not self.media or not self.input_file_path:
        return False, "No media loaded."
    temp_path = None
    try:
        ffmpeg_path = self._get_binary_path('ffmpeg')
        curr_time = max(0, preferred_time if preferred_time is not None else self.get_time() / 1000.0)
        temp_fd, temp_path = tempfile.mkstemp(suffix=".png")
        os.close(temp_fd)
        cmd = [
            ffmpeg_path, '-ss', f"{curr_time:.3f}", '-i', self.input_file_path,
            '-frames:v', '1', '-q:v', '2', '-y', temp_path
        ]
        subprocess.run(
            cmd, check=True, capture_output=True,
            creationflags=(subprocess.CREATE_NO_WINDOW if sys.platform == "win32" else 0)
        )
        if os.path.exists(temp_path) and os.path.getsize(temp_path) > 0:
            os.replace(temp_path, snapshot_path)
            return True, "Snapshot created."
        return False, "Snapshot file empty."
    except Exception as e:
        return False, f"FFmpeg failed: {e}"
```

## [2026-02-11T16:49:10Z] Modification (Items #3, #5, #7, #9, #14)
**File:** `c:/Fortnite_Video_Software/developer_tools/crop_tools.py`
**Reason:** Fix missing snapshot cleanup import, move save I/O off GUI thread, remove duplicate UI methods, activate old-backup cleanup, and integrate resource manager lifecycle.
**Location:** Lines 25, 41-43, 771-842, 1125-1135, 1180-1189, 1060-1077

**ORIGINAL CODE (What was removed):**
```python
from utils import PersistentWindowMixin

from system.utils import ProcessManager, LogManager, DependencyDoctor
from system.state_transfer import StateTransfer

def on_done_clicked(self):
    pd = QProgressDialog("Saving Configuration...", None, 0, 0, self)
    pd.setWindowTitle("Saving"); pd.show(); QApplication.processEvents()
    try:
        tk_map = {v: k for k, v in HUD_ELEMENT_MAPPINGS.items()}
        items = [item for item in self.portrait_scene.items() if isinstance(item, ResizablePixmapItem)]
        if not items: pd.close(); QMessageBox.warning(self, "Save", "No HUD elements to save."); return
        res_str = self.media_processor.original_resolution or "1920x1080"
        config = self.config_manager.load_config()
        configured, unchanged, saved_keys = [], [], set()
        items.sort(key=lambda i: i.zValue())
        for item in items:
            role = item.assigned_role
            if not role: continue
            tk = tk_map.get(role, "unknown")
            if tk == "unknown": continue
            r = item.crop_rect
            fx, fy, fw, fh = transform_to_content_area((float(r.x()), float(r.y()), float(r.width()), float(r.height())), res_str)
            ix, iy, iw, ih = outward_round_rect(fx, fy, fw, fh)
            normalized_rect = [iw, ih, ix, iy]
            scale = max(0.001, round(float(item.current_width) / max(1.0, fw), 4))
            ox, oy, zv = int(scale_round(item.scenePos().x())), int(scale_round(item.scenePos().y())), int(item.zValue())
            config["crops_1080p"][tk] = normalized_rect
            config["scales"][tk] = scale
            config["overlays"][tk] = {"x": ox, "y": oy}
            config["z_orders"][tk] = zv
            configured.append(HUD_ELEMENT_MAPPINGS.get(tk, tk))
            saved_keys.add(tk)
        all_tech_keys = list(config["crops_1080p"].keys())
        for tk in all_tech_keys:
            if tk not in saved_keys:
                self.logger.info(f"Removing zombie element from config: {tk}")
                for s in ["crops_1080p", "scales", "overlays", "z_orders"]:
                    if tk in config[s]: del config[s][tk]
        if self.config_manager.save_config(config):
            try:
                processing_dir = os.path.dirname(self.hud_config_path)
                backup_names = [
                    "old_crops_coordinations.conf",
                    "old1_crops_coordinations.conf",
                    "old2_crops_coordinations.conf",
                    "old3_crops_coordinations.conf",
                    "old4_crops_coordinations.conf"
                ]
                target_backup = None
                for name in backup_names:
                    path = os.path.join(processing_dir, name)
                    if not os.path.exists(path):
                        target_backup = path
                        break
                if not target_backup:
                    oldest_time = float('inf')
                    for name in backup_names:
                        path = os.path.join(processing_dir, name)
                        mtime = os.path.getmtime(path)
                        if mtime < oldest_time:
                            oldest_time = mtime
                            target_backup = path
                if target_backup:
                    import shutil
                    shutil.copy2(self.hud_config_path, target_backup)
                    self.logger.info(f"Rotation backup created at: {target_backup}")
            except Exception as backup_err:
                self.logger.error(f"Failed to create rotation backup: {backup_err}")
            self._dirty = False
            if os.path.exists(self._autosave_file):
                try: os.unlink(self._autosave_file)
                except: pass
            pd.close(); summary = SummaryToast(configured, unchanged, self); summary.show(); self._start_exit_sequence(summary)
        else: pd.close(); QMessageBox.critical(self, "Save", "Failed to save config.")
    except Exception as e: pd.close(); self.logger.exception(f"Save failed: {e}")

def get_title_info(self): return self.base_title

def _format_time(self, ms):
    ts = int(ms / 1000); return f"{ts // 60:02d}:{ts % 60:02d}"

def update_ui(self):
    if not self.media_processor.media: return
    if not self.is_scrubbing:
        curr = self.media_processor.get_time()
        if self.position_slider.isEnabled(): self.position_slider.setValue(curr)
    self.update_time_labels()

def main():
    try:
        QApplication.setAttribute(Qt.AA_EnableHighDpiScaling)
        QApplication.setAttribute(Qt.AA_UseHighDpiPixmaps)
        log_dir = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "logs")
        os.makedirs(log_dir, exist_ok=True)
        enhanced_logger_instance = setup_logger()
        logger = enhanced_logger_instance.base_logger
        logger.info("Application starting...")

def closeEvent(self, event):
    if self._confirm_discard_changes():
        if hasattr(self, '_autosave_file') and os.path.exists(self._autosave_file):
            try: os.unlink(self._autosave_file)
            except: pass
        if hasattr(self, '_autosave_timer') and self._autosave_timer.isActive():
            self._autosave_timer.stop()
        if hasattr(self, 'media_processor') and self.media_processor:
            try:
                self.media_processor.stop()
                self.media_processor.set_media_to_null()
            except Exception as media_cleanup_err:
                self.logger.debug(f"Media cleanup during close skipped: {media_cleanup_err}")
        try: cleanup_temp_snapshots()
        except: pass
        super().closeEvent(event)
    else:
        event.ignore()
```

## [2026-02-11T16:49:10Z] Modification (Item #4)
**File:** `c:/Fortnite_Video_Software/developer_tools/ui_setup.py`
**Reason:** Keep Goal/Status labels visible so users get persistent progress feedback during long operations.
**Location:** Lines 219-221

**ORIGINAL CODE (What was removed):**
```python
CropAppWindow.goal_label = QLabel("")
CropAppWindow.goal_label.setVisible(False)
CropAppWindow.status_label = QLabel("")
CropAppWindow.status_label.setVisible(False)
```

## [2026-02-11T16:49:10Z] Modification (Items #8, #12)
**File:** `c:/Fortnite_Video_Software/developer_tools/magic_wand.py`
**Reason:** Route empty detection through non-error completion flow and consume shared CV heuristic constants.
**Location:** Lines 1-6, 77-79, 109-125, 140-150, 164-168, 188-192, 219-220, 240, 264, 350-351

**ORIGINAL CODE (What was removed):**
```python
from PyQt5.QtCore import QRect, QObject, pyqtSignal, QThread

def _tighten_rect(self, frame_gray, rect, padding=8):
    """[FIX #29] High-precision shrink-wrap with safety padding."""

def _heuristic_map_rect(self, frame_gray):
    h, w = frame_gray.shape[:2]
    aspect = w / h
    nx = 0.85 if aspect > 2.0 else 0.78
    raw = self._rect_from_norm(frame_gray, nx, 0.02, 0.20, 0.25)
    return self._tighten_rect(frame_gray, raw, padding=8)

def _detect_minimap_by_circle(self, frame_gray):
    h, w = frame_gray.shape[:2]
    x0, y0 = int(0.70 * w), 0
    rw, rh = int(0.30 * w), int(0.40 * h)

def _detect_hp_by_color(self, frame_color):
    h, w = frame_color.shape[:2]
    x0, y0 = 0, int(0.60 * h)
    rw, rh = int(0.55 * w), int(0.40 * h)

def _detect_loot_by_boxes(self, frame_gray):
    h, w = frame_gray.shape[:2]
    x0, y0 = int(0.40 * w), int(0.60 * h)
    rw, rh = int(0.60 * w), int(0.40 * h)

p1 = self._find_anchor_multiscale(frame_gray, 'loot_start', 0.52, search)
p5 = self._find_anchor_multiscale(frame_gray, 'loot_end', 0.52, search)

found = self._find_anchor_multiscale(frame_gray, 'hp_icon', 0.50, search)

found = self._find_anchor_multiscale(frame_gray, 'map_edge', 0.50, search)

if not regions: self.error.emit("No HUD elements detected.")
else: self.finished.emit(regions)
```

## [2026-02-11T16:49:10Z] Modification (Item #10)
**File:** `c:/Fortnite_Video_Software/developer_tools/config_manager.py`
**Reason:** Activate internal validation helpers as part of the public validation flow to eliminate dead private checks.
**Location:** Lines 709-711

**ORIGINAL CODE (What was removed):**
```python
def validate_config(self) -> List[str]:
    """Validate persisted configuration and return list of issues."""
    return self.validate_config_data(self.load_config())
```

## [2026-02-11T16:49:10Z] Modification (Item #11)
**File:** `c:/Fortnite_Video_Software/developer_tools/crop_widgets.py`
**Reason:** Use role-menu fallback trigger after release to ensure dead fallback code path is now active and useful.
**Location:** Lines 482, 492, 509

**ORIGINAL CODE (What was removed):**
```python
self._role_popup_timer.start(50)
self._role_popup_timer.start(100)
self._role_popup_timer.start(150)
```

## [2026-02-11T16:49:10Z] Modification (Items #12, #13)
**File:** `c:/Fortnite_Video_Software/developer_tools/config.py` and `c:/Fortnite_Video_Software/developer_tools/portrait_view.py`
**Reason:** Retain configuration constructs by integrating their runtime use and remove portrait view import noise.
**Location:** `config.py` lines 620-629 usage path; `portrait_view.py` lines 1-11 import block

**ORIGINAL CODE (What was removed):**
```python
def get_stylesheet():
    try:
        import os
        qss_path = os.path.join(os.path.dirname(__file__), "theme.qss")
        if os.path.exists(qss_path):
            with open(qss_path, "r", encoding="utf-8") as f:
                return f.read()
    except:
        pass
    return ""

from PyQt5.QtWidgets import (
    QApplication, QWidget, QPushButton, QHBoxLayout, QVBoxLayout,
    QLabel, QGraphicsScene, QGraphicsView, QGraphicsPixmapItem,
    QGraphicsItem, QComboBox, QMessageBox, QFrame, QGraphicsRectItem, QGraphicsSimpleTextItem,
    QDialog
)
from PyQt5.QtCore import Qt, QTimer, QRectF, pyqtSignal, QPointF, QPropertyAnimation, QEasingCurve
from PyQt5.QtGui import QPainter, QPen, QColor, QFont, QBrush, QPixmap, QCursor
```
