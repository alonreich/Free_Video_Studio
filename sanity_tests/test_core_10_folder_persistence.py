from sanity_tests._pending import pending_test

def test_core_10_folder_persistence() -> None:
    pending_test(
        "CORE-10",
        "custom_mp3_dir is prioritized over default music path at launch.",
    )

