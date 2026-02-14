from sanity_tests._pending import pending_test

def test_challenge_07_bitrate_exhaustion() -> None:
    pending_test(
        "CHALLENGE-07",
        "1-second video + 320kbps MP3 clamps computed video bitrate to a safe non-negative minimum.",
    )

