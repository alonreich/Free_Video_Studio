from sanity_tests._pending import pending_test

def test_challenge_01_granular_speed_sync() -> None:
    pending_test(
        "CHALLENGE-01",
        "Wall-clock math remains correct across 0.5x, 2.0x, and 1.1x video segments with constant-tempo music.",
    )

