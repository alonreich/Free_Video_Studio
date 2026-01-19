from config import HUD_ELEMENT_MAPPINGS

GUIDANCE_TEXT = {
    1: {
        "title": "STEP 1: LOAD VIDEO",
        "instruction": "Click '1. OPEN VIDEO' to select a gameplay video (MP4, AVI, MKV)."
    },
    2: {
        "title": "STEP 2: FIND A CLEAR FRAME",
        "instruction": "Use the Play/Pause button and the timeline slider to find a frame where all HUD elements are clearly visible."
    },
    3: {
        "title": "STEP 3: CROP HUD ELEMENTS",
        "instruction": "Click and drag to draw a box around a HUD element. Then, select the element's name from the context menu."
    },
    4: {
        "title": "STEP 4: ADJUST IN PORTRAIT EDITOR",
        "instruction": "Fine-tune the element's size and position in the portrait editor. Changes are saved automatically."
    },
    5: {
        "title": "STEP 5: COMPLETE AND SAVE",
        "instruction": "All elements are configured. Click 'FINISH & SAVE' to export the coordinates for the main application."
    }
}
HINT_TEXT = {
    HUD_ELEMENT_MAPPINGS["loot"]: "HINT: The Loot Area is usually at the bottom-center, showing weapons and items.",
    HUD_ELEMENT_MAPPINGS["stats"]: "HINT: The Map is in the top-right, with stats like kills and materials nearby.",
    HUD_ELEMENT_MAPPINGS["normal_hp"]: "HINT: Your Health and Shield bars are at the bottom-left.",
    HUD_ELEMENT_MAPPINGS["boss_hp"]: "HINT: Boss Health appears above your own health bar during boss fights.",
    HUD_ELEMENT_MAPPINGS["team"]: "HINT: Teammate health bars are displayed on the left side of the screen."
}
