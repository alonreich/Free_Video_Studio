import json

class ConfigManager:

    def __init__(self, file_path):
        self.file_path = file_path
        self.config = self.load_config()

    def load_config(self):
        try:
            with open(self.file_path, 'r') as f:
                return json.load(f)
        except (FileNotFoundError, json.JSONDecodeError):
            return {}

    def save_config(self, config_data):
        self.config = config_data
        try:
            import os
            config_dir = os.path.dirname(self.file_path)
            if not os.path.exists(config_dir):
                os.makedirs(config_dir)
            with open(self.file_path, 'w') as f:
                json.dump(config_data, f, indent=4)
        except Exception as e:
            print(f"Error saving config file: {e}")