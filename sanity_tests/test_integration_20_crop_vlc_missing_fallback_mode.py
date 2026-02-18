from __future__ import annotations
from sanity_tests._ai_sanity_helpers import assert_all_present, read_source

def test_integration_20_crop_vlc_missing_fallback_mode() -> None:
    """Crop Tool should expose screenshot fallback UI when VLC is unavailable."""
    src = read_source("developer_tools/crop_tools.py")
    assert_all_present(
        src,
        [
            "if not self.media_processor.vlc_instance:",
            "self.vlc_error_label.setVisible(True)",
            "self.open_image_button.setVisible(True)",
            "self.open_image_button.setText(\"📷 UPLOAD SCREENSHOT (VLC MISSING)\")",
        ],
    )
