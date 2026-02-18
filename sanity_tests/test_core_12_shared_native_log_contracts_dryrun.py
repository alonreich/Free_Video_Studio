from sanity_tests._ai_sanity_helpers import assert_all_present, read_source

def test_core_12_main_and_crop_native_logging_contract_dryrun() -> None:
    src = read_source("system/utils.py")
    assert_all_present(
        src,
        [
            'vlc_log_path = os.path.join(log_dir, f"{app_prefix}_vlc.log")',
            'raw_log_path = os.path.join(log_dir, f"vlc_{source_tag}.raw.log")',
            "os.dup2(f.fileno(), sys.stdout.fileno())",
            "os.dup2(f.fileno(), sys.stderr.fileno())",
            "faulthandler.enable(f)",
            "NATIVE DEBUG LOGGING ACTIVE",
            "maxBytes=5*1024*1024",
            '"Main_App": "main_app"',
        ],
    )

def test_core_12_merger_native_logging_should_share_vlc_log_dryrun() -> None:
    src = read_source("utilities/merger_system.py")
    assert_all_present(
        src,
        [
            'vlc_log_path = os.path.join(log_dir, "vlc.log")',
            'raw_log_path = os.path.join(log_dir, f"vlc_{source_tag}.raw.log")',
            "os.dup2(f.fileno(), sys.stdout.fileno())",
            "os.dup2(f.fileno(), sys.stderr.fileno())",
            "faulthandler.enable(f)",
            "source_tag = \"video_merger\"",
            "maxBytes=5*1024*1024",
        ],
    )
