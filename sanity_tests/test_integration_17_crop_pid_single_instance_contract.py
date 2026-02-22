from __future__ import annotations
from sanity_tests._ai_sanity_helpers import assert_all_present, read_source

def test_integration_17_crop_pid_single_instance_contract() -> None:
    """Crop Tool must enforce single-instance PID lock behavior."""
    src = read_source("developer_tools/crop_tools.py")
    assert_all_present(
        src,
        [
            "success, pid_handle = ProcessManager.acquire_pid_lock(\"fortnite_crop_tool\")",
            "QMessageBox.information(None, \"Already Running\", \"Crop Tool is already running.\")",
            "sys.exit(0)",
        ],
    )
