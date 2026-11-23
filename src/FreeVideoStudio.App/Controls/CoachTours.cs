using System.Collections.Generic;

namespace FreeVideoStudio.App.Controls;

public static class CoachTours
{
    public const string MainAppKey = "mainapp";
    public const string MergerKey = "merger";
    public const string CropToolKey = "croptool";
    public const string GranularKey = "granular";
    public const string MusicWizardKey = "musicwizard";
    public const string VoiceOverKey = "voiceover";

    public static readonly IReadOnlyList<CoachStep> MainApp = new[]
    {
        new CoachStep(
            "Start with a clip",
            "Drop a gameplay video straight onto this box, or press the button to browse your computer. Everything else on this screen wakes up once a video is loaded.",
            "UploadOverlay", CoachGesture.DropIn),

        new CoachStep(
            "Scrub through your clip",
            "Drag along this bar to move through the video and find the moment you care about. The arrow keys nudge it one frame at a time.",
            "TimelineOverlay", CoachGesture.DragHorizontal),

        new CoachStep(
            "Cut off the boring start",
            "Park the playhead where the good part begins and press this. Everything before that point is thrown away in the finished video.",
            "MarkStartButton", CoachGesture.Click),

        new CoachStep(
            "Cut off the boring end",
            "Same idea at the other end: move to where you want it to stop and press this. What is left between the two marks is your clip.",
            "MarkEndButton", CoachGesture.Click),

        new CoachStep(
            "Speed up or slow down parts",
            "Open this to make one specific stretch slow motion, freeze on a frame, or zoom into the action. It only changes the piece you pick, not the whole clip.",
            "GranularButton", CoachGesture.Click),

        new CoachStep(
            "Put a song behind it",
            "Pick a track and line it up with your clip. You choose which part of the song plays and how loud it sits under the game sounds.",
            "AddMusicButton", CoachGesture.Click),

        new CoachStep(
            "Save the finished video",
            "This is the last button you press. Slide the dial next to it left for a smaller file or right for better picture, then press it and wait.",
            "ProcessButton", CoachGesture.Click)
    };

    public static readonly IReadOnlyList<CoachStep> Merger = new[]
    {
        new CoachStep(
            "Drop your clips in here",
            "Drag as many videos as you like onto this list, or use the add button. They will be joined together in the order you see here.",
            "VideoListFrame", CoachGesture.DropIn),

        new CoachStep(
            "Put them in the right order",
            "Drag a clip up or down in the list, or use these arrows under it. Top of the list plays first. Right-clicking a clip gives you the same moves plus remove.",
            "MoveUpButton", CoachGesture.Click),

        new CoachStep(
            "Preview before you commit",
            "Click any clip in the list to watch it here, so you can check you queued the right ones.",
            "VideoAreaBorder", CoachGesture.Point),

        new CoachStep(
            "Check the size before you wait",
            "This is how big the finished file will be and how long it will run, for the whole list at once. Move the quality dial beside it and both numbers follow.",
            "EstimatedSizeText", CoachGesture.Point),

        new CoachStep(
            "Join them into one file",
            "When the order looks right, press this. The clips become a single video saved to your output folder.",
            "MergeButton", CoachGesture.Click),

        new CoachStep(
            "Going back to the editor",
            "This closes the merger and brings the main editor back. Anything you were editing there is still waiting for you.",
            "ReturnToMainAppButton", CoachGesture.Click)
    };

    public static readonly IReadOnlyList<CoachStep> CropTool = new[]
    {
        new CoachStep(
            "This sets up phone-shaped videos",
            "You are teaching the app which pieces of your screen matter, so it can rebuild them into a tall video for phones. You only have to do this once.",
            "GoalLabel", CoachGesture.Point),

        new CoachStep(
            "Step one: grab a frame",
            "Load a video and pause on a frame that shows your whole game screen clearly. That still picture becomes your working canvas.",
            "OpenVideoButton", CoachGesture.Click),

        new CoachStep(
            "Draw a box round each piece",
            "Drag a box around something you want to keep — your health bar, your ammo, the mini-map. Give it a name and add it.",
            "SourceCanvas", CoachGesture.DrawBox),

        new CoachStep(
            "Arrange them on the phone screen",
            "Every piece you added appears here on a tall phone-shaped canvas. Drag them into the layout you want viewers to see.",
            "PortraitCanvas", CoachGesture.Point),

        new CoachStep(
            "Your pieces are listed here",
            "Each box you draw becomes a layer. Select one to move it, push it behind another, or delete it.",
            "LayerList", CoachGesture.Point),

        new CoachStep(
            "Save the layout",
            "This stores your layout so the main editor can use it every time you make a phone-shaped video. Come back any time to change it.",
            "SaveButton", CoachGesture.Click)
    };

    public static readonly IReadOnlyList<CoachStep> Granular = new[]
    {
        new CoachStep(
            "Pick the piece to change",
            "Move to where the effect should begin and press MARK START, then move to where it should end and press MARK END. That stretch becomes a coloured block.",
            "MarkStartBtn", CoachGesture.Click),

        new CoachStep(
            "Your blocks live on this strip",
            "Each coloured block is one speed change. Drag its edges to make it longer or shorter, or drag the whole block to move it.",
            "GranularLanes", CoachGesture.DragHorizontal),

        new CoachStep(
            "Choose how fast it plays",
            "With a block selected, slide this or tap a number. Below 1x is slow motion, above 1x is fast forward.",
            "PendingSpeedSlider", CoachGesture.DragHorizontal),

        new CoachStep(
            "Freeze on a single frame",
            "This holds one frame still for a moment, like a photo in the middle of the action. Pick how many seconds it holds.",
            "FreezeImageToggle", CoachGesture.Click),

        new CoachStep(
            "Zoom into the action",
            "Turn this on and drag a box on the video around what you want filled to the screen. You can also choose whether the zoom snaps in or glides.",
            "ZoomSegmentBtn", CoachGesture.Click),

        new CoachStep(
            "Keep your changes",
            "Nothing here is applied until you accept. There is no undo in this window, so the delete buttons will always ask you first.",
            "AcceptGranularBtn", CoachGesture.Click)
    };

    public static readonly IReadOnlyList<CoachStep> MusicWizard = new[]
    {
        new CoachStep(
            "Step one: pick a song",
            "Click a song to hear it. When you like it, press NEXT → (or double-click the song).",
            "MusicListBox", CoachGesture.Point),

        new CoachStep(
            "Go on",
            "NEXT → is always here, bottom right. BACK is always bottom left. The ✕ at the top closes without changing your video.",
            "NextBtn", CoachGesture.Click),

        new CoachStep(
            "Step two: where the song starts",
            "Your video and the song are drawn to scale. Move the red line to the moment you like, then press MARK START. A long song sticks out past the video's end; that part is not in your video."),

        new CoachStep(
            "Step three: check and finish",
            "Watch your video with the music, set the two volumes, then press DONE ✓.")
    };

    public static readonly IReadOnlyList<CoachStep> VoiceOver = new[]
    {
        new CoachStep(
            "Talk over your clip",
            "This records your voice while your video plays, so you can commentate on what is happening.",
            "VideoAreaBorder", CoachGesture.Point),

        new CoachStep(
            "Choose your microphone first",
            "Pick the microphone you actually want to use here before recording. Getting this wrong is the usual reason a take comes out silent.",
            "MicDeviceComboBox", CoachGesture.Click),

        new CoachStep(
            "Press to start recording",
            "The video plays while you talk. Press it again to stop, and your take appears on the strip below.",
            "MicRecordButton", CoachGesture.Click),

        new CoachStep(
            "See where your voice sits",
            "Your take appears as a red block on the film strip, with your voice drawn inside it. Click a block to select it and show edit controls.",
            "ThumbnailLaneGrid", CoachGesture.DragHorizontal),

        new CoachStep(
            "Keep your voice on top",
            "Leave these on and the gameplay sound and any background music both drop away during your takes, so what you said stays clear.",
            "DuckAudioCb", CoachGesture.Click),

        new CoachStep(
            "Keep your recording",
            "This hands your voice track back to the main editor. It is mixed in when you save your video.",
            "ApplyButton", CoachGesture.Click)
    };
}
