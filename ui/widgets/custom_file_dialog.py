import subprocess
import shutil
import os
import sys
from PyQt5.QtWidgets import (
    QFileDialog,
    QDesktopWidget,
    QHeaderView,
    QTreeView,
    QListView,
    QComboBox,
    QRubberBand,
    QAbstractItemView,
    QPushButton,
    QMenu,
    QMessageBox,
    QProxyStyle,
    QStyle,
    QInputDialog,
    QApplication,
    QStyledItemDelegate,
)

from PyQt5.QtCore import (
    QByteArray,
    Qt,
    QUrl,
    QPoint,
    QRect,
    QEvent,
    QObject,
    QTimer,
    QMimeData,
    QSize,
)

from PyQt5.QtGui import (
    QColor, 
    QPalette
)

class _CenteredTextDelegate(QStyledItemDelegate):
    def initStyleOption(self, option, index):
        super().initStyleOption(option, index)
        option.displayAlignment = Qt.AlignCenter
        dialog = self.parent()
        while dialog:
            if hasattr(dialog, "_cut_file_paths"):
                break
            dialog = dialog.parent()
        if not dialog:
            return
        model = index.model()
        idx = index
        while hasattr(model, "sourceModel"):
            source = model.sourceModel()
            if not source:
                break
            idx = model.mapToSource(idx)
            model = source
        if hasattr(model, "filePath"):
            file_path = model.filePath(idx)
            if file_path in dialog._cut_file_paths:
                option.palette.setColor(QPalette.Text, QColor("#808080"))

    def sizeHint(self, option, index):
        s = super().sizeHint(option, index)
        return QSize(s.width(), s.height())

class RubberBandHelper(QObject):
    def __init__(self, tree_view: QTreeView):
        super().__init__(tree_view)
        self._tree = tree_view
        self._vp = tree_view.viewport()
        self._rb = QRubberBand(QRubberBand.Rectangle, self._vp)
        self._origin = QPoint()
        self._dragging = False
        self._configure_tree_view()
        self._configure_viewport()

    def _configure_tree_view(self):
        self._tree.setSelectionMode(QAbstractItemView.ExtendedSelection)
        self._tree.setSelectionBehavior(QAbstractItemView.SelectRows)
        self._tree.setDragEnabled(True)
        self._tree.setAcceptDrops(True)
        self._tree.setDragDropMode(QAbstractItemView.DragDrop)
        self._tree.setDropIndicatorShown(True)

    def _configure_viewport(self):
        self._vp.setMouseTracking(True)
        self._vp.installEventFilter(self)

    def eventFilter(self, obj, event):
        if obj is not self._vp:
            return False
        et = event.type()
        if et == QEvent.MouseButtonPress:
            return self._handle_press(event)
        if et == QEvent.MouseMove:
            return self._handle_move(event)
        if et == QEvent.MouseButtonRelease:
            return self._handle_release(event)
        return False

    def _handle_press(self, event):
        if event.button() != Qt.LeftButton:
            return False
        if self._tree.indexAt(event.pos()).isValid():
            return False
        self._origin = event.pos()
        self._dragging = True
        self._rb.setGeometry(QRect(self._origin, self._origin))
        self._rb.show()
        mods = event.modifiers()
        if not (mods & (Qt.ControlModifier | Qt.ShiftModifier)):
            sm = self._tree.selectionModel()
            if sm is not None:
                sm.clearSelection()
        return True

    def _handle_move(self, event):
        if not self._dragging:
            return False
        rect = QRect(self._origin, event.pos()).normalized()
        self._rb.setGeometry(rect)
        mods = event.modifiers()
        self._select_rows_in_rect(rect, mods)
        return True

    def _handle_release(self, event):
        if not self._dragging:
            return False
        if event.button() != Qt.LeftButton:
            return False
        self._dragging = False
        self._rb.hide()
        return True

    def _select_rows_in_rect(self, rect: QRect, modifiers: Qt.KeyboardModifiers):
        sm = self._tree.selectionModel()
        model = self._tree.model()
        root = self._tree.rootIndex()
        if sm is None or model is None:
            return
        additive = bool(modifiers & (Qt.ControlModifier | Qt.ShiftModifier))
        row_count = model.rowCount(root)
        if row_count <= 0:
            return
        for row in range(row_count):
            idx = model.index(row, 0, root)
            if not idx.isValid():
                continue
            vrect = self._tree.visualRect(idx)
            hit = rect.intersects(vrect)
            if hit:
                sm.select(idx, sm.Select | sm.Rows)
            else:
                if not additive:
                    sm.select(idx, sm.Deselect | sm.Rows)

    def set_enabled(self, enabled: bool):
        if enabled:
            self._vp.installEventFilter(self)
        else:
            self._vp.removeEventFilter(self)
            self._rb.hide()
            self._dragging = False

    def is_dragging(self) -> bool:
        return self._dragging

    def hide_rubberband(self):
        self._rb.hide()
        self._dragging = False

    def show_rubberband(self):
        self._rb.show()

    def rubberband_geometry(self) -> QRect:
        return self._rb.geometry()

    def set_rubberband_geometry(self, rect: QRect):
        self._rb.setGeometry(rect)

    def reset_origin(self, pos: QPoint):
        self._origin = pos

    def origin(self) -> QPoint:
        return self._origin

class CenterHeaderProxyStyle(QProxyStyle):
    """
    1. Hides the default system arrow.
    2. Forces text to be DEAD CENTER by temporarily clearing the sort flag 
       (preventing the style from shifting text to make room for the arrow).
    3. Manually paints symmetric arrows on both sides.
    """

    def drawPrimitive(self, element, option, painter, widget=None):
        if element == QStyle.PE_IndicatorHeaderArrow:
            return 
        super().drawPrimitive(element, option, painter, widget)

    def drawControl(self, element, option, painter, widget=None):
        if element == QStyle.CE_HeaderLabel:
            original_indicator = 0
            if hasattr(option, "sortIndicator"):
                original_indicator = option.sortIndicator
            option.sortIndicator = 0 
            option.textAlignment = Qt.AlignCenter
            super().drawControl(element, option, painter, widget)
            option.sortIndicator = original_indicator
            if option.sortIndicator:
                arrow_color = QColor("#ecf0f1")
                painter.save()
                painter.setRenderHint(painter.Antialiasing)
                painter.setBrush(arrow_color)
                painter.setPen(Qt.NoPen)
                r = option.rect
                cx = r.center().x()
                cy = r.center().y()
                fm = option.fontMetrics
                text_width = fm.width(option.text)
                padding = 26 
                left_x = int(cx - (text_width / 2) - padding)
                right_x = int(cx + (text_width / 2) + padding)
                s = 4 
                is_down = (option.sortIndicator == 1)
                
                def draw_arrow(x, y, down):
                    if down:
                        painter.drawConvexPolygon(
                            QPoint(x - s, y - s),
                            QPoint(x + s, y - s),
                            QPoint(x, y + s)
                        )
                    else:
                        painter.drawConvexPolygon(
                            QPoint(x - s, y + s),
                            QPoint(x + s, y + s),
                            QPoint(x, y - s)
                        )
                draw_arrow(left_x, cy, is_down)
                draw_arrow(right_x, cy, is_down)
                painter.restore()
            return
        super().drawControl(element, option, painter, widget)

class CenterAlignedTreeView(QTreeView):
    def __init__(self, *args, **kwargs):
        super().__init__(*args, **kwargs)
        self._align_timer = None

    def showEvent(self, event):
        super().showEvent(event)
        self._apply_center_alignment()

    def setModel(self, model):
        super().setModel(model)
        self._apply_center_alignment()

    def _apply_center_alignment(self):
        model = self.model()
        if model is None:
            return
        try:
            for col in range(model.columnCount(self.rootIndex())):
                self.setTextElideMode(Qt.ElideRight)
                self.setColumnWidth(col, self.columnWidth(col))
        except Exception:
            pass

class CustomFileDialog(QFileDialog):
    def __init__(self, *args, config=None, **kwargs):
        super(CustomFileDialog, self).__init__(*args, **kwargs)
        self.config = config
        self.setObjectName("CustomFileDialog")
        self._rb_helper = None
        self.tree_view = None
        self._text_delegate = None 
        self._header_style = None
        self._cut_file_paths = set()
        self._init_dialog_flags()
        self._init_modes()
        self._init_title()
        self._apply_styles()
        self._bind_tree_view()
        self._setup_lookin_width()
        self._setup_sidebar()
        self._tune_buttons()

    def _init_dialog_flags(self):
        self.setOption(QFileDialog.DontUseNativeDialog, True)

    def _init_modes(self):
        self.setFileMode(QFileDialog.ExistingFiles)

    def _init_title(self):
        self.setWindowTitle("Select Video File(s)")

    def _apply_styles(self):
        self.setStyleSheet(
            """
            QFileDialog#CustomFileDialog {
                background-color: #212f3d;
                color: #ecf0f1;
            }
            QFileDialog#CustomFileDialog QWidget {
                background-color: #212f3d;
                color: #ecf0f1;
            }
            QFileDialog#CustomFileDialog QLabel {
                color: #ecf0f1;
            }
            QFileDialog#CustomFileDialog QHeaderView::section {
                background-color: #2a3c4d;
                color: #ecf0f1;
                padding: 4px;
                padding-right: 24px;
                border: 1px solid #1f2a36;
            }
            QFileDialog#CustomFileDialog QTreeView {
                background-color: #2a3c4d;
                border: 1px solid #1f2a36;
            }
            QFileDialog#CustomFileDialog QListView {
                background-color: #2a3c4d;
                border: 1px solid #1f2a36;
            }
            QFileDialog#CustomFileDialog QPushButton {
                background-color: #4fa3e3;
                color: #ffffff;
                border: 2px solid #1b4f72;
                padding: 10px 18px;
                border-radius: 8px;
                font-weight: bold;
            }
            QFileDialog#CustomFileDialog QPushButton:hover {
                background-color: #6bb8f0;
                border: 2px solid #ecf0f1;
            }
            QFileDialog#CustomFileDialog QPushButton#openButton {
                background-color: #336b70;
                border: 2px solid #1b4f72;
            }
            QFileDialog#CustomFileDialog QPushButton#openButton:hover {
                background-color: #6aa1c5;
                border: 2px solid #ecf0f1;
            }
            QFileDialog#CustomFileDialog QPushButton#cancelButton {
                background-color: #336b70;
                border: 2px solid #1b4f72;
            }
            QFileDialog#CustomFileDialog QPushButton#cancelButton:hover {
                background-color: #6aa1c5;
                border: 2px solid #ecf0f1;
            }
            QFileDialog#CustomFileDialog QRubberBand {
                border: 1px solid #5dade2;
                background-color: transparent;
            }
            QFileDialog#CustomFileDialog QTreeView::item:selected,
            QFileDialog#CustomFileDialog QListView::item:selected {
                background-color: #2a5949;
                color: #ffffff;
                border: 1px solid #143d5c;
            }
            QFileDialog#CustomFileDialog QTreeView::item:hover,
            QFileDialog#CustomFileDialog QListView::item:hover {
                background-color: #2c3e50;
                border: 1px solid #5dade2;
            }
            QFileDialog#CustomFileDialog QLineEdit {
                background-color: #34495e;
                border: 1px solid #2980b9;
                border-radius: 5px;
                padding: 5px;
                color: #ecf0f1;
            }
            QFileDialog#CustomFileDialog QComboBox {
                background-color: #34495e;
                border: 1px solid #2980b9;
                border-radius: 5px;
                padding: 5px;
                color: #ecf0f1;
            }
            QFileDialog#CustomFileDialog QComboBox QAbstractItemView {
                background-color: #34495e;
                color: #ecf0f1;
            }
            QFileDialog#CustomFileDialog QMenu {
                background-color: #212f3d;
                color: #ecf0f1;
                border: 1px solid #5dade2;
            }
            QFileDialog#CustomFileDialog QMenu::item {
                padding: 10px 25px;
                margin: 2px 0px;
            }
            QFileDialog#CustomFileDialog QMenu::item:selected {
                background-color: #2a5949;
            }
            QFileDialog#CustomFileDialog QMenu::separator {
                height: 2px;
                background: #5dade2;
                margin: 2px 0px;
            }
            QFileDialog#CustomFileDialog QLabel#backButton,
            QFileDialog#CustomFileDialog QLabel#forwardButton,
            QFileDialog#CustomFileDialog QLabel#parentDirButton,
            QFileDialog#CustomFileDialog QLabel#newFolderButton {
                min-width: 50px;
            }
            """
        )

    def _bind_tree_view(self):
        self.tree_view = self.findChild(QTreeView)
        if self.tree_view:
            header = self.tree_view.header()
            self.tree_view.setSortingEnabled(True)
            header.setSortIndicatorShown(True)
            header.setSectionsClickable(True)
            header.setStretchLastSection(False)
            header.sortIndicatorChanged.connect(self._save_sort_state)
            self._header_style = CenterHeaderProxyStyle(header.style())
            header.setStyle(self._header_style)
            header.setDefaultAlignment(Qt.AlignCenter)
            self.restore_state(header)
            try:
                col = header.sortIndicatorSection()
                if col < 0:
                    header.setSortIndicator(0, Qt.AscendingOrder)
            except Exception:
                pass
            self.tree_view.setUniformRowHeights(True)
            try:
                self.tree_view.setAllColumnsShowFocus(True)
            except Exception:
                pass
            self._text_delegate = _CenteredTextDelegate(self.tree_view)
            self.tree_view.setItemDelegate(self._text_delegate)
            if self.tree_view.model():
                try:
                    self.tree_view.model().setReadOnly(False)
                except Exception:
                    pass
            self._rb_helper = RubberBandHelper(self.tree_view)
        self.list_view = self.findChild(QListView, "listView")
        self._install_silent_delete(self.tree_view)
        self._install_silent_delete(self.list_view)

    def _install_silent_delete(self, view):
        if view is None:
            return
        view.installEventFilter(self)
        view.viewport().installEventFilter(self)

    def eventFilter(self, obj, event):
        if event.type() == QEvent.ContextMenu:
            view = obj
            if not isinstance(obj, (QTreeView, QListView)):
                view = obj.parent()
            self._show_context_menu(view, event.globalPos())
            return True
        if event.type() in (QEvent.KeyPress, QEvent.ShortcutOverride):
            if hasattr(event, 'key') and event.key() == Qt.Key_Delete:
                if event.type() == QEvent.ShortcutOverride:
                    event.accept()
                    return True
                self._delete_selected_files_silent()
                return True
        return super().eventFilter(obj, event)

    def _show_context_menu(self, view, global_pos):
        paths = self._selected_paths_from_view(view)
        menu = QMenu(view)
        act_cut = None
        act_copy = None
        act_paste = None
        act_play = None
        act_rename = None
        act_new_folder = None
        act_delete = None
        if paths:
            act_cut = menu.addAction("        ✂️        Cut         ✂️")
            act_copy = menu.addAction("      📄       Copy        📄")
            menu.addSeparator()
            act_play = menu.addAction("▶   Preview Play the Video    ▶")
            menu.addSeparator()
        clipboard = QApplication.clipboard()
        if clipboard.mimeData().hasUrls():
            act_paste = menu.addAction("      📋       Paste       📋")
        if len(paths) == 1:
            act_rename = menu.addAction("          Rename the File")
            menu.addSeparator()
        act_new_folder = menu.addAction("📂   Create a New Folder   📂")
        if paths:
            menu.addSeparator()
            act_delete = menu.addAction("        ⛔    Delete File   ⛔")
        chosen = menu.exec_(global_pos)
        if chosen == act_cut:
            self._cut_files(paths)
        elif chosen == act_copy:
            self._copy_files(paths)
        elif chosen == act_paste:
            self._paste_files()
        elif chosen == act_new_folder:
            self._create_new_folder()
        elif paths and chosen == act_play:
            self._play_video(paths)
        elif paths and chosen == act_delete:
            self._delete_selected_files_silent()
        elif paths and len(paths) == 1 and chosen == act_rename:
            self._rename_file(paths[0])
    
    def _cut_files(self, paths):
        if not paths:
            return
        self._copy_files(paths, is_cut=True)

    def _copy_files(self, paths, is_cut=False):
        if not paths:
            return
        if self._cut_file_paths:
            self._cut_file_paths.clear()
            self.tree_view.viewport().update()
        mime_data = QMimeData()
        urls = [QUrl.fromLocalFile(p) for p in paths]
        mime_data.setUrls(urls)
        if is_cut:
            mime_data.setData("application/x-qt-cut-files", QByteArray(b"1"))
            self._cut_file_paths.update(paths)
            self.tree_view.viewport().update()
        clipboard = QApplication.clipboard()
        clipboard.setMimeData(mime_data)

    def _paste_files(self):
        clipboard = QApplication.clipboard()
        mime_data = clipboard.mimeData()
        if not mime_data.hasUrls():
            return
        is_cut = mime_data.hasFormat("application/x-qt-cut-files")
        source_paths = [url.toLocalFile() for url in mime_data.urls()]
        dest_dir = self.directory().absolutePath()
        for src_path in source_paths:
            if not os.path.exists(src_path):
                continue
            base_name = os.path.basename(src_path)
            dest_path = os.path.join(dest_dir, base_name)
            if os.path.exists(dest_path) and src_path.lower() != dest_path.lower():
                action = self._handle_overwrite(base_name)
                if action == "skip":
                    continue
                elif action == "rename":
                    name, ext = os.path.splitext(base_name)
                    i = 1
                    while True:
                        new_name = f"{name} ({i}){ext}"
                        new_dest_path = os.path.join(dest_dir, new_name)
                        if not os.path.exists(new_dest_path):
                            dest_path = new_dest_path
                            break
                        i += 1
            try:
                if is_cut:
                    shutil.move(src_path, dest_path)
                else:
                    if os.path.isdir(src_path):
                        shutil.copytree(src_path, dest_path)
                    else:
                        shutil.copy(src_path, dest_path)
            except Exception as e:
                QMessageBox.warning(self, "Paste Error", f"Could not paste file:\n{src_path}\n\nError: {e}")
        if is_cut:
            self._cut_file_paths.clear()
        self.setDirectory(self.directory())
        self.tree_view.viewport().update()

    def _handle_overwrite(self, file_name):
        msg_box = QMessageBox(self)
        msg_box.setWindowTitle("Confirm Overwrite")
        msg_box.setText(f"The destination already has a file named '{file_name}'.")
        msg_box.setInformativeText("Do you want to replace it?")
        overwrite_btn = msg_box.addButton("Overwrite", QMessageBox.AcceptRole)
        skip_btn = msg_box.addButton("Skip", QMessageBox.RejectRole)
        rename_btn = msg_box.addButton("Rename", QMessageBox.ActionRole)
        msg_box.setDefaultButton(overwrite_btn)
        msg_box.exec_()
        if msg_box.clickedButton() == skip_btn:
            return "skip"
        elif msg_box.clickedButton() == rename_btn:
            return "rename"
        else:
            return "overwrite"

    def _create_new_folder(self):
        current_dir = self.directory().absolutePath()
        name, ok = QInputDialog.getText(self, "New Folder", "Folder Name:")
        if ok and name:
            new_path = os.path.join(current_dir, name)
            try:
                os.mkdir(new_path)
                self.setDirectory(current_dir)
            except Exception as e:
                QMessageBox.warning(self, "Error", f"Could not create folder: {e}")

    def _rename_file(self, old_path):
        dirname = os.path.dirname(old_path)
        basename = os.path.basename(old_path)
        name, ok = QInputDialog.getText(self, "Rename File", "New Name:", text=basename)
        if ok and name and name != basename:
            new_path = os.path.join(dirname, name)
            try:
                os.rename(old_path, new_path)
                self.setDirectory(dirname)
            except Exception as e:
                QMessageBox.warning(self, "Error", f"Could not rename file: {e}")

    def _play_video(self, paths):
        if not paths:
            return
        target = paths[0]
        try:
            if hasattr(os, 'startfile'):
                os.startfile(target)
            else:
                opener = 'open' if sys.platform == 'darwin' else 'xdg-open'
                subprocess.Popen([opener, target])
        except Exception:
            pass
    
    def _delete_selected_files_silent(self):
        """
        Delete selected file(s) or folder(s) with NO confirmation.
        """
        view = None
        if getattr(self, "tree_view", None) is not None and self.tree_view.hasFocus():
            view = self.tree_view
        elif getattr(self, "list_view", None) is not None and self.list_view.hasFocus():
            view = self.list_view
        else:
            view = getattr(self, "tree_view", None) or getattr(self, "list_view", None)
        paths = self._selected_paths_from_view(view)
        if not paths:
            return

        def _send_to_bin_windows(path):
            import ctypes
            from ctypes import wintypes

            class SHFILEOPSTRUCTW(ctypes.Structure):
                _fields_ = [("hwnd", wintypes.HWND), ("wFunc", wintypes.UINT), ("pFrom", wintypes.LPCWSTR),
                            ("pTo", wintypes.LPCWSTR), ("fFlags", ctypes.c_uint), ("fAnyOperationsAborted", wintypes.BOOL),
                            ("hNameMappings", wintypes.LPVOID), ("lpszProgressTitle", wintypes.LPCWSTR)]
            FO_DELETE, FOF_ALLOWUNDO, FOF_NOCONFIRMATION = 3, 0x40, 0x10
            fileop = SHFILEOPSTRUCTW(hwnd=None, wFunc=FO_DELETE, pFrom=path + '\0', pTo=None,
                                     fFlags=FOF_ALLOWUNDO | FOF_NOCONFIRMATION, fAnyOperationsAborted=False,
                                     hNameMappings=None, lpszProgressTitle=None)
            return ctypes.windll.shell32.SHFileOperationW(ctypes.byref(fileop)) == 0
        failed = []
        for p in paths:
            try:
                if not os.path.exists(p): continue
                try:
                    from send2trash import send2trash
                    send2trash(p)
                except ImportError:
                    if not _send_to_bin_windows(os.path.abspath(p)):
                        raise OSError("Windows Shell delete failed.")
            except Exception as e:
                failed.append((p, str(e)))
        try:
            self.setDirectory(self.directory())
        except Exception:
            pass
        if failed:
            msg = "Some items could not be deleted:\n\n"
            msg += "\n".join([f"- {p}\n  {err}" for p, err in failed[:8]])
            if len(failed) > 8:
                msg += f"\n\n(and {len(failed) - 8} more...)"
            QMessageBox.warning(self, "Delete failed", msg)

    def _selected_paths_from_view(self, view):
        if view is None:
            return []
        sm = view.selectionModel()
        model = view.model()
        if sm is None or model is None:
            return []
        indexes = sm.selectedRows(0)
        if not indexes:
            indexes = sm.selectedIndexes()
        paths = []
        for idx in indexes:
            try:
                p = model.filePath(idx)
            except Exception:
                p = None
            if p:
                paths.append(p)
        seen = set()
        out = []
        for p in paths:
            if p not in seen:
                seen.add(p)
                out.append(p)
        return out

    def _setup_lookin_width(self):
        look_in_combobox = self.findChild(QComboBox, "lookInCombo")
        if look_in_combobox:
            look_in_combobox.setMinimumWidth(450)

    def _setup_sidebar(self):
        sidebar = self.findChild(QListView, "sidebar")
        if not sidebar:
            return
        sidebar.setMinimumWidth(250)
        quick_access_paths = self.get_windows_quick_access()
        if not quick_access_paths:
            return
        urls = [QUrl.fromLocalFile(p) for p in quick_access_paths]
        if "C:/" not in quick_access_paths and "C:\\" not in quick_access_paths:
            urls.append(QUrl.fromLocalFile("C:/"))
        self.setSidebarUrls(urls)

    def _tune_buttons(self):
        QTimer.singleShot(0, self._apply_button_ids_and_restyle)

    def _apply_button_ids_and_restyle(self):
        buttons = self.findChildren(QPushButton)
        for b in buttons:
            raw = (b.text() or "")
            txt = raw.replace("&", "").replace("...", "").strip().lower()
            if txt in ("open", "ok"):
                b.setObjectName("openButton")
            elif txt == "cancel":
                b.setObjectName("cancelButton")
        self.update()

    def _save_sort_state(self, logicalIndex: int, order: Qt.SortOrder):
        if self.config:
            self.config.config["file_dialog_sort_column"] = logicalIndex
            self.config.config["file_dialog_sort_order"] = int(order)
            self.config.save_config(self.config.config)

    def get_windows_quick_access(self):
        try:
            ps_script = (
                "$s = New-Object -ComObject Shell.Application; "
                "$s.Namespace('shell:::{679f85cb-0220-4080-b29b-5540cc05aab6}').Items() | "
                "ForEach-Object { $_.Path }"
            )
            cmd = [
                "powershell",
                "-NoProfile",
                "-NonInteractive",
                "-Command",
                ps_script
            ]
            result = subprocess.run(
                cmd,
                capture_output=True,
                text=True,
                creationflags=0x08000000,
            )
            paths = [line.strip() for line in result.stdout.split("\n") if line.strip()]
            return [p for p in paths if os.path.exists(p)]
        except Exception:
            return []

    def save_state(self):
        if not self.config:
            return
        self.config.config["file_dialog_geometry"] = (
            self.saveGeometry().toBase64().data().decode()
        )
        if self.tree_view:
            header = self.tree_view.header()
            self.config.config["file_dialog_header_state"] = (
                header.saveState().toBase64().data().decode()
            )
            self.config.config["file_dialog_sort_column"] = header.sortIndicatorSection()
            self.config.config["file_dialog_sort_order"] = int(header.sortIndicatorOrder())
        self.config.save_config(self.config.config)

    def restore_state(self, header):
        if not self.config:
            self.set_default_state(header)
            return
        geom_b64 = self.config.config.get("file_dialog_geometry")
        if geom_b64:
            self.restoreGeometry(QByteArray.fromBase64(geom_b64.encode()))
        else:
            self.set_default_position()
        header_state_b64 = self.config.config.get("file_dialog_header_state")
        if header_state_b64:
            header.restoreState(QByteArray.fromBase64(header_state_b64.encode()))
        else:
            self.set_default_column_widths(header)
        sort_column = self.config.config.get("file_dialog_sort_column", -1)
        sort_order = self.config.config.get("file_dialog_sort_order", Qt.AscendingOrder)
        if sort_column != -1:
            try:
                order = Qt.SortOrder(sort_order)
            except Exception:
                order = Qt.AscendingOrder
            header.setSortIndicator(sort_column, order)

    def set_default_state(self, header):
        self.set_default_position()
        self.set_default_column_widths(header)

    def set_default_position(self):
        desktop = QDesktopWidget()
        screen_rect = desktop.screenGeometry()
        self.resize(1400, 800)
        self.move(screen_rect.center() - self.rect().center())

    def set_default_column_widths(self, header):
        header.setSectionResizeMode(QHeaderView.Interactive)
        header.setStretchLastSection(False)
        name_col_idx = 0
        size_col_idx = 1
        type_col_idx = 2
        date_col_idx = 3
        header.resizeSection(name_col_idx, 600)
        header.resizeSection(size_col_idx, 200)
        header.resizeSection(date_col_idx, 240)
        header.setSectionHidden(type_col_idx, True)

    def selectedFiles(self):
        return super().selectedFiles()

    def done(self, result):
        self.save_state()
        self.hide()
        QApplication.processEvents()
        super().done(result)

    def closeEvent(self, event):
        super().closeEvent(event)
if __name__ == "__main__":
    app = QApplication(sys.argv)

    class MockConfig:
        def __init__(self):
            self.config = {}

        def save_config(self, cfg):
            pass
    dialog = CustomFileDialog(config=MockConfig())
    dialog.exec_()