"""
State management system with transaction support and rollback capabilities.
Provides robust error recovery for the Fortnite Video Software application.
"""

import json
import os
import tempfile
import shutil
import time
from typing import Dict, Any, Optional, List, Callable
from enum import Enum
import logging

class OperationType(Enum):
    """Types of operations that can be rolled back."""
    CONFIG_SAVE = "config_save"
    CROP_OPERATION = "crop_operation"
    PORTRAIT_ADJUSTMENT = "portrait_adjustment"
    VIDEO_LOAD = "video_load"
    SNAPSHOT = "snapshot"
    FILE_OPERATION = "file_operation"
    VIDEO_PROCESSING = "video_processing"

class TransactionState:
    """Represents a single transaction that can be rolled back."""
    
    def __init__(self, operation_type: OperationType, description: str):
        self.operation_type = operation_type
        self.description = description
        self.created_at = time.time()
        self.transaction_id = f"{operation_type.value}_{int(time.time() * 1000)}"
        self.backup_files: Dict[str, str] = {}
        self.rollback_actions: List[Callable[[], bool]] = []
        self.state_snapshots: Dict[str, Any] = {}
        self.content_hashes: Dict[str, str] = {}
        self.metadata: Dict[str, Any] = {
            'transaction_id': self.transaction_id,
            'created_at': self.created_at,
            'operation_type': operation_type.value,
            'description': description
        }
        
    def add_file_backup(self, file_path: str) -> bool:
        """Create a backup of a file for rollback with content hash."""
        try:
            if os.path.exists(file_path):
                backup_dir = tempfile.gettempdir()
                timestamp = int(time.time() * 1000)
                backup_filename = f"{os.path.basename(file_path)}.backup.{timestamp}"
                backup_path = os.path.join(backup_dir, backup_filename)
                shutil.copy2(file_path, backup_path)
                self.backup_files[file_path] = backup_path

                import hashlib
                with open(file_path, 'rb') as f:
                    file_hash = hashlib.sha256(f.read()).hexdigest()
                self.content_hashes[file_path] = file_hash
                return True
        except Exception as e:
            logging.error(f"Failed to backup file {file_path}: {e}")
        return False
    
    def verify_backup_integrity(self, file_path: str) -> bool:
        """Verify that backup file still exists and matches original hash."""
        if file_path not in self.backup_files:
            return False
        backup_path = self.backup_files[file_path]
        if not os.path.exists(backup_path):
            return False
        if file_path not in self.content_hashes:
            return True
        try:
            import hashlib
            with open(backup_path, 'rb') as f:
                current_hash = hashlib.sha256(f.read()).hexdigest()
            return current_hash == self.content_hashes[file_path]
        except Exception:
            return False
    
    def add_state_snapshot(self, key: str, state: Any):
        """Store a state snapshot for rollback."""
        self.state_snapshots[key] = state
    
    def add_rollback_action(self, action: Callable[[], bool]):
        """Add a custom rollback action."""
        self.rollback_actions.append(action)
    
    def rollback(self) -> bool:
        """Execute rollback for this transaction."""
        success = True
        for action in self.rollback_actions:
            try:
                if not action():
                    success = False
            except Exception as e:
                logging.error(f"Rollback action failed: {e}")
                success = False
        for original_path, backup_path in self.backup_files.items():
            try:
                if os.path.exists(backup_path):
                    shutil.copy2(backup_path, original_path)
                    logging.info(f"Restored {original_path} from backup")
            except Exception as e:
                logging.error(f"Failed to restore {original_path}: {e}")
                success = False
        return success
    
    def cleanup(self):
        """Clean up backup files after successful completion."""
        for backup_path in self.backup_files.values():
            try:
                if os.path.exists(backup_path):
                    os.unlink(backup_path)
            except PermissionError as e:
                logging.warning(f"Permission denied when cleaning up backup file {backup_path}: {e}")
            except OSError as e:
                logging.warning(f"OS error when cleaning up backup file {backup_path}: {e}")
            except Exception as e:
                logging.warning(f"Unexpected error when cleaning up backup file {backup_path}: {e}")

class UndoAction:
    """Represents a single undoable action."""
    
    def __init__(self, action_type: str, description: str, undo_func: Callable[[], bool], redo_func: Callable[[], bool]):
        self.action_type = action_type
        self.description = description
        self.undo_func = undo_func
        self.redo_func = redo_func
        self.timestamp = time.time()
    
    def undo(self) -> bool:
        """Execute undo for this action."""
        try:
            return self.undo_func()
        except Exception as e:
            logging.error(f"Undo action failed: {e}")
            return False
    
    def redo(self) -> bool:
        """Execute redo for this action."""
        try:
            return self.redo_func()
        except Exception as e:
            logging.error(f"Redo action failed: {e}")
            return False

class StateManager:
    """Manages application state with transaction support and undo/redo stack."""
    
    def __init__(self, logger: Optional[logging.Logger] = None):
        self.logger = logger or logging.getLogger(__name__)
        self.active_transactions: List[TransactionState] = []
        self.state_history: List[Dict[str, Any]] = []
        self.max_history_size = 10
        self.undo_stack: List[UndoAction] = []
        self.redo_stack: List[UndoAction] = []
        self.max_undo_stack_size = 50
        
    def begin_transaction(self, operation_type: OperationType, description: str) -> TransactionState:
        """Begin a new transaction."""
        transaction = TransactionState(operation_type, description)
        self.active_transactions.append(transaction)
        self.logger.info(f"Began transaction: {description} ({operation_type.value})")
        return transaction
    
    def commit_transaction(self, transaction: TransactionState) -> bool:
        """Commit a transaction, making changes permanent."""
        try:
            if transaction in self.active_transactions:
                self.active_transactions.remove(transaction)
                history_entry = {
                    'timestamp': time.time(),
                    'operation_type': transaction.operation_type.value,
                    'description': transaction.description,
                    'backup_count': len(transaction.backup_files)
                }
                self.state_history.append(history_entry)
                if len(self.state_history) > self.max_history_size:
                    self.state_history = self.state_history[-self.max_history_size:]
                transaction.cleanup()
                self.logger.info(f"Committed transaction: {transaction.description}")
                return True
        except Exception as e:
            self.logger.error(f"Failed to commit transaction: {e}")
        return False
    
    def rollback_transaction(self, transaction: TransactionState) -> bool:
        """Rollback a specific transaction."""
        try:
            if transaction in self.active_transactions:
                self.logger.warning(f"Rolling back transaction: {transaction.description}")
                success = transaction.rollback()
                if success:
                    self.active_transactions.remove(transaction)
                    self.logger.info(f"Successfully rolled back: {transaction.description}")
                else:
                    self.logger.error(f"Partial rollback for: {transaction.description}")
                return success
        except Exception as e:
            self.logger.error(f"Failed to rollback transaction: {e}")
        return False
    
    def rollback_all(self) -> bool:
        """Rollback all active transactions."""
        self.logger.warning(f"Rolling back all {len(self.active_transactions)} active transactions")
        success = True
        for transaction in reversed(self.active_transactions[:]):
            if not self.rollback_transaction(transaction):
                success = False
        return success
    
    def save_application_state(self, state_data: Dict[str, Any]) -> str:
        """Save current application state to a temporary file for recovery."""
        try:
            state_dir = os.path.join(tempfile.gettempdir(), 'fortnite_video_state')
            os.makedirs(state_dir, exist_ok=True)
            timestamp = int(time.time() * 1000)
            state_file = os.path.join(state_dir, f'app_state_{timestamp}.json')
            with open(state_file, 'w', encoding='utf-8') as f:
                json.dump({
                    'timestamp': timestamp,
                    'data': state_data,
                    'active_transactions': len(self.active_transactions)
                }, f, indent=2)
            self.logger.info(f"Saved application state to {state_file}")
            return state_file
        except Exception as e:
            self.logger.error(f"Failed to save application state: {e}")
            return ""
    
    def load_application_state(self, state_file: str) -> Optional[Dict[str, Any]]:
        """Load application state from a recovery file."""
        try:
            if os.path.exists(state_file):
                with open(state_file, 'r', encoding='utf-8') as f:
                    state_data = json.load(f)
                self.logger.info(f"Loaded application state from {state_file}")
                return state_data.get('data', {})
        except Exception as e:
            self.logger.error(f"Failed to load application state: {e}")
        return None
    
    def add_undo_action(self, action_type: str, description: str, undo_func: Callable[[], bool], redo_func: Callable[[], bool]) -> None:
        """Add an undoable action to the undo stack."""
        action = UndoAction(action_type, description, undo_func, redo_func)
        self.undo_stack.append(action)
        self.redo_stack.clear()
        if len(self.undo_stack) > self.max_undo_stack_size:
            self.undo_stack = self.undo_stack[-self.max_undo_stack_size:]
        self.logger.debug(f"Added undo action: {description} ({action_type})")
    
    def undo(self) -> bool:
        """Undo the last action with optimized validation."""
        if not self.undo_stack:
            self.logger.warning("Nothing to undo")
            return False
        action = self.undo_stack[-1]
        if not self._validate_action(action) or not self._validate_action_function(action.undo_func, "undo"):
            self.logger.error(f"Invalid undo action: {action.description}")
            self.undo_stack.pop()
            return False
        action = self.undo_stack.pop()
        self.logger.info(f"Undoing: {action.description}")
        try:
            if action.undo():
                self.redo_stack.append(action)
                self.logger.info(f"Successfully undone: {action.description}")
                return True
            else:
                self.logger.error(f"Failed to undo: {action.description}")
                return False
        except Exception as e:
            self.logger.error(f"Exception during undo for {action.description}: {e}")
            return False
    
    def redo(self) -> bool:
        """Redo the last undone action with optimized validation."""
        if not self.redo_stack:
            self.logger.warning("Nothing to redo")
            return False
        action = self.redo_stack[-1]
        if not self._validate_action(action) or not self._validate_action_function(action.redo_func, "redo"):
            self.logger.error(f"Invalid redo action: {action.description}")
            self.redo_stack.pop()
            return False
        action = self.redo_stack.pop()
        self.logger.info(f"Redoing: {action.description}")
        try:
            if action.redo():
                self.undo_stack.append(action)
                self.logger.info(f"Successfully redone: {action.description}")
                return True
            else:
                self.logger.error(f"Failed to redo: {action.description}")
                return False
        except Exception as e:
            self.logger.error(f"Exception during redo for {action.description}: {e}")
            return False
    
    def _validate_undo_stack(self):
        """Validate undo stack integrity and remove corrupted actions."""
        valid_actions = []
        for action in self.undo_stack:
            if self._validate_action(action):
                valid_actions.append(action)
            else:
                self.logger.warning(f"Removing corrupted undo action: {action.description}")
        if len(valid_actions) != len(self.undo_stack):
            self.undo_stack = valid_actions
            self.logger.warning(f"Cleaned undo stack: removed {len(self.undo_stack) - len(valid_actions)} corrupted actions")
    
    def _validate_redo_stack(self):
        """Validate redo stack integrity and remove corrupted actions."""
        valid_actions = []
        for action in self.redo_stack:
            if self._validate_action(action):
                valid_actions.append(action)
            else:
                self.logger.warning(f"Removing corrupted redo action: {action.description}")
        if len(valid_actions) != len(self.redo_stack):
            self.redo_stack = valid_actions
            self.logger.warning(f"Cleaned redo stack: removed {len(self.redo_stack) - len(valid_actions)} corrupted actions")
    
    def _validate_action(self, action: UndoAction) -> bool:
        """Validate an undo/redo action for integrity."""
        if not action:
            return False
        if not hasattr(action, 'undo_func') or not hasattr(action, 'redo_func'):
            return False
        if not callable(action.undo_func) or not callable(action.redo_func):
            return False
        return True
    
    def _validate_action_function(self, func: Callable[[], bool], func_type: str) -> bool:
        """Validate that an action function is callable and appears valid."""
        if not callable(func):
            self.logger.error(f"{func_type} function is not callable")
            return False
        if hasattr(func, '__self__') and func.__self__ is None:
            self.logger.error(f"{func_type} function is unbound method")
            return False
        return True
    
    def _remove_corrupted_actions(self):
        """Remove corrupted actions from both undo and redo stacks."""
        self._validate_undo_stack()
        self._validate_redo_stack()
    
    def can_undo(self) -> bool:
        """Check if undo is available."""
        return len(self.undo_stack) > 0
    
    def can_redo(self) -> bool:
        """Check if redo is available."""
        return len(self.redo_stack) > 0
    
    def get_undo_description(self) -> Optional[str]:
        """Get description of the next undo action."""
        if self.undo_stack:
            return self.undo_stack[-1].description
        return None
    
    def get_redo_description(self) -> Optional[str]:
        """Get description of the next redo action."""
        if self.redo_stack:
            return self.redo_stack[-1].description
        return None
    
    def clear_undo_stack(self) -> None:
        """Clear the undo/redo stacks."""
        self.undo_stack.clear()
        self.redo_stack.clear()
        self.logger.info("Cleared undo/redo stacks")
    
    def get_state_summary(self) -> Dict[str, Any]:
        """Get a summary of current state manager status."""
        return {
            'active_transactions': len(self.active_transactions),
            'transaction_types': [t.operation_type.value for t in self.active_transactions],
            'history_size': len(self.state_history),
            'last_operation': self.state_history[-1] if self.state_history else None,
            'undo_stack_size': len(self.undo_stack),
            'redo_stack_size': len(self.redo_stack),
            'can_undo': self.can_undo(),
            'can_redo': self.can_redo()
        }
_state_manager_instance = None

def get_state_manager(logger: Optional[logging.Logger] = None) -> StateManager:
    """Get or create the global state manager instance."""
    global _state_manager_instance
    if _state_manager_instance is None:
        _state_manager_instance = StateManager(logger)
    return _state_manager_instance

def with_transaction(operation_type: OperationType, description: str):
    """Decorator for automatic transaction management."""

    def decorator(func):
        def wrapper(*args, **kwargs):
            state_manager = get_state_manager()
            transaction = state_manager.begin_transaction(operation_type, description)
            try:
                if operation_type == OperationType.CONFIG_SAVE:
                    try:
                        from .config_manager import get_config_manager
                    except ImportError:
                        from config_manager import get_config_manager
                    config_manager = get_config_manager()
                    transaction.add_file_backup(config_manager.config_path)
                result = func(*args, **kwargs, _transaction=transaction)
                if result:
                    state_manager.commit_transaction(transaction)
                else:
                    state_manager.rollback_transaction(transaction)
                return result
            except Exception as e:
                logging.error(f"Transaction failed in {func.__name__}: {e}")
                state_manager.rollback_transaction(transaction)
                raise
        return wrapper
    return decorator
