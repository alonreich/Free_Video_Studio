import cv2
import numpy as np
import os
anchor_dir = os.path.join(os.path.dirname(__file__), 'anchors')
anchors = {
    'ref_keybind_1.png': (50, 30),
    'ref_keybind_5.png': (50, 30),
    'ref_minimap_border.png': (100, 100),
    'ref_hp_icon.png': (40, 40)
}
for filename, size in anchors.items():
    path = os.path.join(anchor_dir, filename)
    img = np.zeros((size[1], size[0], 3), dtype=np.uint8)
    if 'keybind' in filename:
        img[:, :] = [0, 255, 0]
    elif 'minimap' in filename:
        img[:, :] = [255, 0, 0]
    elif 'hp' in filename:
        img[:, :] = [0, 0, 255]
    cv2.putText(img, filename, (5, 20), cv2.FONT_HERSHEY_SIMPLEX, 0.5, (255, 255, 255), 1)
    cv2.imwrite(path, img)
    print(f"Created placeholder: {filename}")
print("Placeholder anchor images created successfully.")