import os
import json
import tempfile
import logging

class StateTransfer:
    """
    Handles saving and loading session state across different applications in the suite.
    """
    @staticmethod
    def get_session_file():
        return os.path.join(tempfile.gettempdir(), "fortnite_video_suite_session.json")
    @staticmethod
    def save_state(state_data: dict):
        """
        Saves current application state to a temporary session file.
        """
        try:
            with open(StateTransfer.get_session_file(), 'w') as f:
                json.dump(state_data, f, indent=2)
            logging.getLogger("StateTransfer").info("Session state saved successfully.")
        except Exception as e:
            logging.getLogger("StateTransfer").error(f"Failed to save session state: {e}")
    @staticmethod
    def load_state() -> dict:
        """
        Loads the session state if it exists, then deletes the file to prevent stale data.
        Returns empty dict if no session file is found.
        """
        path = StateTransfer.get_session_file()
        if not os.path.exists(path):
            return {}
        try:
            with open(path, 'r') as f:
                data = json.load(f)
            try:
                os.remove(path)
            except:
                pass
            logging.getLogger("StateTransfer").info("Session state loaded successfully.")
            return data
        except Exception as e:
            logging.getLogger("StateTransfer").error(f"Failed to load session state: {e}")
            return {}
    @staticmethod
    def clear_state():
        """Clears the session file."""
        path = StateTransfer.get_session_file()
        if os.path.exists(path):
            try:
                os.remove(path)
            except Exception:
                pass
