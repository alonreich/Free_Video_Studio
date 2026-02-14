from sanity_tests._pending import pending_test

def test_core_03_scrub_protection() -> None:
    pending_test(
        "CORE-03",
        "VLC scrubbing is throttled to 20fps (50ms) to prevent native deadlocks.",
    )

