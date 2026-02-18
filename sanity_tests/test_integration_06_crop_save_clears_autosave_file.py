from __future__ import annotations
from sanity_tests._ai_sanity_helpers import assert_all_present, read_source

def test_integration_06_crop_save_clears_autosave_file() -> None:
    """After successful save, autosave recovery file should be removed."""
    src = read_source("developer_tools/crop_tools.py")
    assert_all_present(
        src,
        [
            "def _on_save_finished(self, success, configured, unchanged, error_message):",
            "if os.path.exists(self._autosave_file):",
            "os.unlink(self._autosave_file)",
        ],
    )
