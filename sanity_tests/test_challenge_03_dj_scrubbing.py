from sanity_tests._pending import pending_test

def test_challenge_03_dj_scrubbing() -> None:
    pending_test(
        "CHALLENGE-03",
        "50ms scrub throttle prevents VLC buffer overflow/crash under rapid timeline wiggle.",
    )

