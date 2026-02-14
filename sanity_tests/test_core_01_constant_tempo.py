from sanity_tests._pending import pending_test

def test_core_01_constant_tempo() -> None:
    pending_test(
        "CORE-01",
        "Background music always stays at 1.0x speed regardless of video speed changes.",
    )

