namespace FreeVideoStudio.App;

public partial class VideoMergerWindow
{
    public VideoMergerWindow(Services.MergerLaunchContext? launchContext) : this()
    {
        LaunchContext = launchContext;
    }

    public Services.MergerLaunchContext? LaunchContext { get; }

    public Services.ToolNavigationResult? NavigationResult { get; private set; }
}