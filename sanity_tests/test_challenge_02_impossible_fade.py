from sanity_tests._pending import pending_test

def test_challenge_02_impossible_fade() -> None:
    pending_test(
        "CHALLENGE-02",
        "min(1.0, dur/3) fade logic avoids division-by-zero and negative durations for 0.1s clips.",
    )

