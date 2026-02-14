from sanity_tests._pending import pending_test

def test_core_04_smart_fading() -> None:
    pending_test(
        "CORE-04",
        "afade durations automatically scale down for clips shorter than 3 seconds.",
    )

