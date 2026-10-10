// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/05_SYSTEM_LIFECYCLE_STORAGE.md
// Invariants, constants, and threading models must match spec bit-for-bit.

namespace FreeVideoStudio.App.Services;

/// <summary>What the user is watching happen. Decides the window's title and wording.</summary>
internal enum UpgradeKind
{
    Install,
    Update,
    Recovery
}

/// <summary>
/// UPGRADEUX_01 — the steps the user sees, in order. Each one is a plain-English line in the
/// progress window; the broker moves through them and the elevated worker reports its share.
/// </summary>
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

/// <summary>The user's answer when another copy of the app is still open (UPGRADEUX_03).</summary>
internal enum RunningAppChoice
{
    CloseAndContinue,
    Cancel
}

/// <summary>
/// UPGRADEUX_01 — how <see cref="UpgradeCoordinator"/> tells the user what is happening.
///
/// <para>Before this existed the broker ran with no window at all: after the app closed, the user
/// saw a Windows permission prompt out of nowhere, then a minute or more of nothing while hundreds
/// of MB were unpacked, copied and hashed, and then the app reappeared — or did not, with no
/// explanation. Every call here is display-only. None of them may change what is installed, in
/// which order, or whether a failure rolls back.</para>
///
/// <para>Implementations must be safe to call from any thread and must never throw.</para>
/// </summary>
internal interface IUpgradeProgress
{
    /// <summary>Sets the title and which steps are listed. Does not show anything yet.</summary>
    void Begin(UpgradeKind kind, bool waitsForApp);

    /// <summary>Makes the window visible (it stays hidden while the user is still working).</summary>
    void Show();

    /// <summary>Marks <paramref name="step"/> as the current one; every earlier step is done.</summary>
    void Step(UpgradeStep step, string? detail = null, double? fraction = null);

    /// <summary>UPGRADEUX_03 — another copy of the app is open. Ask before Windows is asked.</summary>
    Task<RunningAppChoice> AskToCloseRunningAppAsync(int openHere, bool openInOtherAccount);

    /// <summary>Shows the finished state. The window closes itself shortly afterwards.</summary>
    void Succeeded(string message);

    /// <summary>Shows a failure in plain English and waits until the user has read it.</summary>
    Task FailedAsync(string title, string message);
}

/// <summary>
/// UPGRADEUX_01 — used when no window can be shown (the UI libraries could not be loaded).
/// Falls back to native Windows message boxes so a failure is still never silent.
/// </summary>
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

/// <summary>UPGRADEUX_01 — every user-facing sentence of the install/update window, in one place.</summary>
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

    /// <summary>UPGRADEUX_02 — said BEFORE the Windows prompt appears, so it is never a surprise.</summary>
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
