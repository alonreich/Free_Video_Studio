namespace FreeVideoStudio.App.Services;

internal enum UpgradeKind
{
    Install,
    Update,
    Recovery
}

internal enum UpgradeStep
{
    CloseOldVersion,
    CheckNothingOpen,
    Permission,
    Settings,
    Unpack,
    Install,
    Test,
    Shortcuts,
    Start
}

internal enum RunningAppChoice
{
    CloseAndContinue,
    Cancel
}

internal interface IUpgradeProgress
{
    void Begin(UpgradeKind kind, bool waitsForApp);

    void Show();

    void Step(UpgradeStep step, string? detail = null, double? fraction = null);

    Task<RunningAppChoice> AskToCloseRunningAppAsync(int openHere, bool openInOtherAccount);

    void Succeeded(string message);

    Task FailedAsync(string title, string message);
}

internal sealed class HeadlessUpgradeProgress : IUpgradeProgress
{
    public static readonly HeadlessUpgradeProgress Instance = new();

    public void Begin(UpgradeKind kind, bool waitsForApp) { }
    public void Show() { }
    public void Step(UpgradeStep step, string? detail = null, double? fraction = null) { }

    public Task<RunningAppChoice> AskToCloseRunningAppAsync(int openHere, bool openInOtherAccount)
    {
        bool yes = NativeDialog.ShowQuestion(UpgradeText.RunningAppQuestion(openHere, openInOtherAccount)
            + Environment.NewLine + Environment.NewLine
            + (openHere > 0 ? "Click Yes to close it now (you will be asked to save any work), or No to cancel the update."
                            : "Click Yes to check again, or No to cancel the update."),
            "Free Video Studio Update");
        return Task.FromResult(yes ? RunningAppChoice.CloseAndContinue : RunningAppChoice.Cancel);
    }

    public void Succeeded(string message) { }

    public Task FailedAsync(string title, string message)
    {
        NativeDialog.ShowError(message, title);
        return Task.CompletedTask;
    }
}

internal static class UpgradeText
{
    public static string Title(UpgradeKind kind) => kind switch
    {
        UpgradeKind.Install => "Installing Free Video Studio",
        UpgradeKind.Recovery => "Finishing an interrupted update",
        _ => "Updating Free Video Studio"
    };

    public static string StepTitle(UpgradeStep step, UpgradeKind kind) => step switch
    {
        UpgradeStep.CloseOldVersion => "Closing the old version",
        UpgradeStep.CheckNothingOpen => "Making sure Free Video Studio is closed",
        UpgradeStep.Permission => "Asking Windows for permission",
        UpgradeStep.Settings => "Keeping your settings",
        UpgradeStep.Unpack => kind == UpgradeKind.Install ? "Unpacking Free Video Studio" : "Unpacking the new version",
        UpgradeStep.Install => "Installing files",
        UpgradeStep.Test => "Testing that it works",
        UpgradeStep.Shortcuts => "Updating shortcuts",
        UpgradeStep.Start => kind == UpgradeKind.Install ? "Opening Free Video Studio" : "Opening the new version",
        _ => step.ToString()
    };

    public static string PermissionDetail(UpgradeKind kind) =>
        "Windows will now ask: \"Do you want to allow this app to make changes to your device?\"  Click YES. "
      + (kind == UpgradeKind.Recovery
            ? "This lets Free Video Studio finish putting its program files back in order."
            : "This lets Free Video Studio write its program files. Nothing else on your computer is changed.");

    public static string RunningAppQuestion(int openHere, bool openInOtherAccount) =>
        openHere > 0
            ? "Free Video Studio is still open" + (openHere > 1 ? $" ({openHere} windows)" : "") + ". It must be closed before it can be "
              + "updated." + (openInOtherAccount ? " It is also open in another Windows account — sign out of that account too." : "")
            : "Free Video Studio is open in another Windows account on this computer. Sign out of that account, then try again.";

    public static string UacDeclined(UpgradeKind kind) =>
        kind == UpgradeKind.Install
            ? "Installation cancelled. You clicked \"No\" when Windows asked for permission, so nothing was installed. Run the installer again whenever you are ready."
            : "Update cancelled. You clicked \"No\" when Windows asked for permission, so nothing was changed.";

    public static string WorkerProgressDetail(string phase) => phase switch
    {
        "switch" => "Switching over to the new files…",
        "shortcuts" => "Updating Start menu and desktop shortcuts…",
        "test" => "Starting a quick check of the video engine…",
        _ => string.Empty
    };
}
