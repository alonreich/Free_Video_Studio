from sanity_tests._pending import pending_test

def test_challenge_06_worker_race() -> None:
    pending_test(
        "CHALLENGE-06",
        "Rapid BACK after song start cleanly kills FFmpeg process tree and prevents zombie binaries.",
    )

