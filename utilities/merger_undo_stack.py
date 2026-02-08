class MergerUndoStack:
    """
    Manages Undo/Redo history for the Video Merger file list.
    """

    def __init__(self, max_depth=50):
        self._undo_stack = []
        self._redo_stack = []
        self._max_depth = max_depth

    def push(self, state):
        """
        Push a new state onto the undo stack.
        Clears the redo stack.
        'state' should be a list of file paths or dictionaries representing the current list.
        """
        if self._undo_stack and self._undo_stack[-1] == state:
            return
        self._undo_stack.append(state)
        if len(self._undo_stack) > self._max_depth:
            self._undo_stack.pop(0)
        self._redo_stack.clear()

    def undo(self, current_state):
        """
        Returns the previous state to restore, or None if undo is not possible.
        Saves 'current_state' to the redo stack.
        """
        if not self._undo_stack:
            return None
        previous_state = self._undo_stack.pop()
        if previous_state == current_state and self._undo_stack:
            self._redo_stack.append(previous_state)
            previous_state = self._undo_stack.pop()
        elif previous_state == current_state:
             self._undo_stack.append(previous_state)
             return None
        self._redo_stack.append(current_state)
        return previous_state

    def redo(self, current_state):
        """
        Returns the next state to restore, or None if redo is not possible.
        Saves 'current_state' to the undo stack.
        """
        if not self._redo_stack:
            return None
        next_state = self._redo_stack.pop()
        self._undo_stack.append(current_state)
        return next_state

    def can_undo(self):
        return len(self._undo_stack) > 0

    def can_redo(self):
        return len(self._redo_stack) > 0

    def clear(self):
        self._undo_stack.clear()
        self._redo_stack.clear()
