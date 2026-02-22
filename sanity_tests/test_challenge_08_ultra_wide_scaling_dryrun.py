from sanity_tests._ai_sanity_helpers import assert_all_present, read_source

def test_challenge_08_ultra_wide_scaling_dryrun() -> None:
    src = read_source("processing/filter_builder.py")
    assert_all_present(
        src,
        [
            "scale=1280:1920:force_original_aspect_ratio=increase:flags=bilinear,crop=1280:1920",
            "[vpreout]scale=1080:-2,pad=1080:1920:(ow-iw)/2:(oh-ih)/2:black,format=nv12",
            "overlay=x={lx}:y={ly}",
        ],
    )
