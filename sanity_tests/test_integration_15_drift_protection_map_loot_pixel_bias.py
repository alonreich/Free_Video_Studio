from __future__ import annotations
from sanity_tests._ai_sanity_helpers import assert_all_present, read_source

def test_integration_15_drift_protection_map_loot_pixel_bias() -> None:
    """Map/Stats and Loot drift-protection pixel bias contract should stay intact."""
    cfg_src = read_source("developer_tools/config.py")
    handler_src = read_source("developer_tools/app_handlers.py")
    assert_all_present(
        cfg_src,
        [
            "HUD_SAFE_PADDING = {",
            '"stats": {"left": -1},',
            '"loot": {"right": 1}',
        ],
    )
    assert_all_present(
        handler_src,
        [
            "padding = HUD_SAFE_PADDING.get(tk, {})",
            "if \"left\" in padding:",
            "if \"right\" in padding:",
        ],
    )
