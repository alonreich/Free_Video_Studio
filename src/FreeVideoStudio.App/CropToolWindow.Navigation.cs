namespace FreeVideoStudio.App;

public partial class CropToolWindow
{
    public CropToolWindow(Services.CropToolLaunchContext? launchContext) : this((string?)null)
    {
        LaunchContext = launchContext;
    }

    public Services.CropToolLaunchContext? LaunchContext { get; }

    public Services.ToolNavigationResult? NavigationResult { get; private set; }
}