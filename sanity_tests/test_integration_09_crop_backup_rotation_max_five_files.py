from __future__ import annotations
from sanity_tests._ai_sanity_helpers import assert_all_present, read_source

def test_integration_09_crop_backup_rotation_max_five_files() -> None:
    """Crop save worker should use the 5-name rotating backup contract."""
    src = read_source("developer_tools/crop_tools.py")
    assert_all_present(
        src,
        [
            "def _create_rotation_backup(self):",
            "backup_names = [",
            '"old_crops_coordinations.conf",',
            '"old1_crops_coordinations.conf",',
            '"old2_crops_coordinations.conf",',
            '"old3_crops_coordinations.conf",',
            '"old4_crops_coordinations.conf",',
        ],
    )
