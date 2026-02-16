import vlc
import os
import sys
print("Testing VLC instance creation...")
plugin_path = os.path.join(os.getcwd(), 'binaries', 'plugins').replace('\\', '/')
print(f"Plugin path: {plugin_path}")
print(f"Exists: {os.path.exists(plugin_path)}")
print("\n--- Test 1: No arguments ---")
try:
    instance = vlc.Instance()
    print(f"Instance (no args): {instance}")
    if instance:
        player = instance.media_player_new()
        print(f"Player created: {player}")
    else:
        print("Instance is None")
except Exception as e:
    print(f"Error: {e}")
print("\n--- Test 2: With plugin path ---")
vlc_args = ['--verbose=0', '--no-osd', '--ignore-config', f'--plugin-path={plugin_path}', '--user-agent=TEST', '--aout=waveout']
print(f"Args: {vlc_args}")
try:
    instance = vlc.Instance(vlc_args)
    print(f"Instance (with args): {instance}")
    if instance:
        player = instance.media_player_new()
        print(f"Player created: {player}")
    else:
        print("Instance is None")
except Exception as e:
    print(f"Error: {e}")
print("\n--- Test 3: Minimal args ---")
vlc_args_min = ['--verbose=0', '--no-osd', '--ignore-config']
try:
    instance = vlc.Instance(vlc_args_min)
    print(f"Instance (minimal): {instance}")
    if instance:
        player = instance.media_player_new()
        print(f"Player created: {player}")
    else:
        print("Instance is None")
except Exception as e:
    print(f"Error: {e}")
print("\nDone.")