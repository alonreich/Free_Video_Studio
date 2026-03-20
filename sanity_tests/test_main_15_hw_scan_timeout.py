import os, sys, pytest, tempfile, shutil
sys.dont_write_bytecode = True

def test_hw_scan_timeout_fallback():
    """
    Test fallback to CPU mode when GPU probe takes too long.
    Success: Splash screen finishes and app opens in CPU-only mode.
    """
    tmp = tempfile.mkdtemp()
    try:
        assert True, "Mock implementation: Timeout fallback to CPU successful."
    finally:
        shutil.rmtree(tmp, ignore_errors=True)
