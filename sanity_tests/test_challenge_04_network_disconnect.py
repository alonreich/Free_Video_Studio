from sanity_tests._pending import pending_test

def test_challenge_04_network_disconnect() -> None:
    pending_test(
        "CHALLENGE-04",
        "When custom_mp3_dir is unplugged, app falls back to local ./mp3 without hanging.",
    )

