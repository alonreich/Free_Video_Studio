import json
import os
from typing import Any, Dict, cast

class ConfigManager:
    def __init__(self, file_path: str):
        self.file_path = file_path
        self.config: Dict[str, Any] = {}
        self._ensure_config_dir()
        self.config = self.load_config()
        if not os.path.exists(self.file_path):
            self.save_config(self.config)

    def _ensure_config_dir(self) -> None:
        config_dir = os.path.dirname(self.file_path)
        if config_dir:
            os.makedirs(config_dir, exist_ok=True)

    def load_config(self) -> Dict[str, Any]:
        self._ensure_config_dir()
        try:
            with open(self.file_path, 'r', encoding='utf-8') as f:
                data = json.load(f)
                if isinstance(data, dict):
                    return cast(Dict[str, Any], data)
                return {}
        except (FileNotFoundError, json.JSONDecodeError, OSError):
            return {}

    def save_config(self, config_data: Dict[str, Any]) -> None:
        self.config = dict(config_data)
        try:
            self._ensure_config_dir()
            with open(self.file_path, 'w', encoding='utf-8') as f:
                json.dump(self.config, f, indent=4)
        except Exception as e:
            print(f"Error saving config file: {e}")
