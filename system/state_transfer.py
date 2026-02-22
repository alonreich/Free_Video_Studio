import os
import json
import logging
try:
    from system.shared_paths import SharedPaths
except ImportError:
    try:
        import sys
        sys.path.append(os.path.dirname(os.path.abspath(__file__)))

        from shared_paths import SharedPaths
    except ImportError:
        class SharedPaths:
            import tempfile
            ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), '..'))
            TEMP = os.path.join(tempfile.gettempdir(), 'FVS_Temp')

class StateTransfer:
    """
    Handles saving and loading session state across different applications in the suite.
    Persistent until explicitly cleared.
    """
    @staticmethod
    def get_session_file():
        path = os.path.join(SharedPaths.TEMP, "fvs_session_state.json")
        os.makedirs(os.path.dirname(path), exist_ok=True)
        return path
    @staticmethod
    def save_state(state_data: dict):
        """
        Overwrites the current session state with new data.
        """
        try:
            with open(StateTransfer.get_session_file(), 'w') as f:
                json.dump(state_data, f, indent=2)
            logging.getLogger("StateTransfer").info(f"Session state saved to {StateTransfer.get_session_file()}")
        except Exception as e:
            logging.getLogger("StateTransfer").error(f"Failed to save session state: {e}")
    @staticmethod
    def update_state(updates: dict):
        """
        Updates the existing session state with new keys/values.
        """
        current = StateTransfer.load_state()
        current.update(updates)
        StateTransfer.save_state(current)
    @staticmethod
    def load_state() -> dict:
        """
        Loads the session state without deleting it.
        Returns empty dict if no session file is found.
        """
        path = StateTransfer.get_session_file()
        if not os.path.exists(path):
            return {}
        try:
            with open(path, 'r') as f:
                data = json.load(f)
            logging.getLogger("StateTransfer").info("Session state loaded.")
            return data
        except Exception as e:
            logging.getLogger("StateTransfer").error(f"Failed to load session state: {e}")
            return {}
    @staticmethod
    def clear_state():
        """Explicitly clears the session file."""
        path = StateTransfer.get_session_file()
        if os.path.exists(path):
            try:
                os.remove(path)
                logging.getLogger("StateTransfer").info("Session state cleared.")
            except Exception as e:
                logging.getLogger("StateTransfer").error(f"Failed to clear session state: {e}")
