from sanity_tests._ai_sanity_helpers import assert_all_present, read_source

def test_challenge_08_ultra_wide_scaling_dryrun() -> None:
    src = read_source("processing/filter_mobile.py")
    assert_all_present(
        src,
        [
            "scale={INTERNAL_W}:{INTERNAL_H}:force_original_aspect_ratio=increase:flags=lanczos,crop={INTERNAL_W}:{INTERNAL_H}",
            "scale={CONTENT_AREA_W}:{CONTENT_AREA_H}:flags=lanczos,pad={FINAL_W}:{FINAL_H}",
        ],
    )
