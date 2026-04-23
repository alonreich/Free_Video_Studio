import os, sys, pytest, tempfile, shutil
sys.dont_write_bytecode = True

from sanity_tests._real_sanity_harness import install_qt_mpv_stubs, DummyMediaPlayer, DummySpinBox, DummySlider, DummyButton, DummyLogger
install_qt_mpv_stubs()

from processing.encoders import EncoderManager

def test_gpu_to_cpu_fallback():
    """
    Test if the EncoderManager correctly provides a fallback list when a GPU encoder fails.
    Success: If h264_nvenc fails, libx264 is the ultimate fallback.
    """
    logger = DummyLogger()
    manager = EncoderManager(logger=logger)
    manager.available_encoders = {"h264_nvenc", "h264_qsv"}
    fallback_1 = manager.get_fallback_list("h264_nvenc")
    assert "h264_amf" in fallback_1
    assert "h264_qsv" in fallback_1
    assert "libx264" in fallback_1
    manager.attempted_encoders.add("h264_amf")
    manager.attempted_encoders.add("h264_qsv")
    fallback_last = manager.get_fallback_list("h264_qsv")
    assert fallback_last == ["libx264"]
    manager_cpu = EncoderManager(logger=logger, hardware_strategy="CPU")
    assert manager_cpu.forced_cpu == True
    assert manager_cpu.get_initial_encoder() == "libx264"
    flags, label = manager_cpu.get_codec_flags("libx264", 5000, 10.0)
    assert "libx264" in flags
    assert "CPU" in label
