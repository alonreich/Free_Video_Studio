from sanity_tests._pending import pending_test

def test_core_09_native_logs() -> None:
    pending_test(
        "CORE-09",
        "Native C++ VLC crashes are captured through faulthandler into main_app.log.",
    )

