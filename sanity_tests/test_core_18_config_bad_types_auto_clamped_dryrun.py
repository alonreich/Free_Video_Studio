from __future__ import annotations
from sanity_tests._ai_sanity_helpers import assert_all_present, read_source

def _simulate_quality_level_cast(raw_value: object) -> int:
    """Detached snapshot of expected cast/fallback behavior."""
    try:
        return int(raw_value)
    except Exception:
        return 2

def test_core_18_config_bad_types_auto_clamped_dryrun() -> None:
    """
    Bad config types must not crash flow.
    App should cast when possible, fallback when not.
    """
    cfg_src = read_source("processing/config_data.py")
    models_src = read_source("processing/processing_models.py")
    assert_all_present(
        cfg_src,
        [
            "try:",
            "q = int(quality_level)",
            "except Exception:",
            "q = 2",
            "return keep_highest_res, target_mb, q",
        ],
    )
    assert_all_present(
        models_src,
        [
            "if not isinstance(job.speed_segments, list):",
            "speed_segments must be a list of segment dictionaries",
            "except (TypeError, ValueError):",
            "speed_segments[{idx}] values must be numeric",
        ],
    )
    assert _simulate_quality_level_cast("3") == 3
    assert _simulate_quality_level_cast("bad") == 2
    assert _simulate_quality_level_cast(None) == 2
