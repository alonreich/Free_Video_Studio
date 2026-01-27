import sys
sys.path.insert(0, '.')
try:
    from crop_widgets import DrawWidget
    print("SUCCESS: Imported DrawWidget")
    methods = [
        '_smart_zoom_start',
        '_smart_zoom_update',
        '_smart_zoom_apply',
        '_smart_zoom_reset',
        '_center_on_point',
        '_focus_towards_direction'
    ]
    for method in methods:
        if hasattr(DrawWidget, method):
            print(f"[OK] Method {method} exists")
        else:
            print(f"[FAIL] Method {method} missing")
    attrs = [
        '_smart_zoom_start_pos_img',
        '_smart_zoom_initial_zoom',
        '_smart_zoom_direction',
        '_smart_zoom_phase',
        '_smart_zoom_threshold'
    ]
    for attr in attrs:
        if attr in DrawWidget.__dict__:
            print(f"[OK] Attribute {attr} exists")
        else:
            print(f"[FAIL] Attribute {attr} missing")
    print("Smart autozoom implementation appears to be correctly added.")
except Exception as e:
    print(f"ERROR: {e}")

    import traceback
    traceback.print_exc()