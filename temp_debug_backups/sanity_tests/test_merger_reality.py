import os, sys, pytest, types
sys.dont_write_bytecode = True

from sanity_tests._real_sanity_harness import install_qt_mpv_stubs, DummyLogger
install_qt_mpv_stubs()

from processing.filter_builder import FilterBuilder
from processing.processing_models import ProcessingJob, validate_job

def test_merger_audio_chain_logic():
    """
    Test if the FilterBuilder correctly constructs the complex audio ducking chain.
    Success: Chain contains sidechain compress/gate and volume split filters.
    """
    fb = FilterBuilder(logger=DummyLogger())
    music_config = {
        "path": "song.mp3",
        "timeline_start_sec": 0.0,
        "timeline_end_sec": 10.0,
        "file_offset_sec": 5.0,
        "volume": 0.8,
        "main_vol": 1.0
    }
    chain_tuple = fb.build_audio_chain(
        music_config=music_config,
        video_start_time=0.0,
        video_end_time=10.0,
        speed_factor=1.0,
        disable_fades=False,
        vfade_in_d=1.0,
        audio_filter_cmd="anull",
        sample_rate=48000
    )
    chain = chain_tuple[0]
    full_filter = "".join(chain)
    assert "sidechain" in full_filter.lower() or "amix" in full_filter.lower()
    assert "volume" in full_filter.lower()
    assert "48000" in full_filter

def test_merger_job_validation():
    """
    Test if the ProcessingJob validation catches impossible scenarios.
    """
    job = ProcessingJob(
        input_path="test.mp4",
        start_time=10.0,
        end_time=5.0,
        original_resolution="1920x1080",
        is_mobile_format=True
    )
    res = validate_job(job)
    assert res["valid"] is False
    assert any("duration" in err.lower() for err in res["errors"])
    job.input_path = "non_existent_file_12345.mp4"
    res = validate_job(job)
    assert res["valid"] is False
    assert any("not found" in err.lower() for err in res["errors"])
