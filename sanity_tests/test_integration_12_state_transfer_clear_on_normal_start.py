from __future__ import annotations
from sanity_tests._ai_sanity_helpers import assert_all_present, read_source

def test_integration_12_state_transfer_clear_on_normal_start() -> None:
    """Main window bootstrap should clear stale state-transfer session data."""
    src = read_source("ui/main_window.py")
    assert_all_present(
        src,
        [
            "StateTransfer.clear_state()",
            "except Exception as state_err:",
            "self.logger.debug(\"Could not clear startup session state: %s\", state_err)",
        ],
    )
