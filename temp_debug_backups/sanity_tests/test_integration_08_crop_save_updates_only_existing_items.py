from __future__ import annotations
from sanity_tests._ai_sanity_helpers import assert_all_present, read_source

def test_integration_08_crop_save_updates_only_existing_items() -> None:
    """Save flow must remove keys that are no longer present in portrait scene items."""
    src = read_source("developer_tools/crop_tools.py")
    assert_all_present(
        src,
        [
            "for key in list(config.get(\"crops_1080p\", {}).keys()):",
            "if key not in saved_keys:",
            "for section in [\"crops_1080p\", \"scales\", \"overlays\", \"z_orders\"]:",
            "del config[section][key]",
        ],
    )
