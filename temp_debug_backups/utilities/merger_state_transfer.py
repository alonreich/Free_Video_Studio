import json
import os
import tempfile

class MergerStateTransfer:
    @staticmethod
    def save_state(data):
        state_file = os.path.join(tempfile.gettempdir(), "fvs_merger_state.json")
        try:
            with open(state_file, 'w', encoding='utf-8') as f:
                json.dump(data, f)
        except Exception:
            pass
    @staticmethod
    def load_state():
        state_file = os.path.join(tempfile.gettempdir(), "fvs_merger_state.json")
        if os.path.exists(state_file):
            try:
                with open(state_file, 'r', encoding='utf-8') as f:
                    return json.load(f)
            except Exception:
                pass
        return None
