// [SPEC CONTRACT] STRICT GOVERNANCE:
// CO-GOVERNED FILE - bound by EVERY spec below simultaneously.
// Reading one is NOT compliance (SPEC_GOVERNANCE.md section 2).
// Forbidden to modify without reading: docs/04_UI_UX_AVALONIA_SPEC.md
// Forbidden to modify without reading: docs/05_SYSTEM_LIFECYCLE_STORAGE.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using System.Windows.Input;
using FreeVideoStudio.App.Controls;

namespace FreeVideoStudio.App.ViewModels;

/// <summary>
/// The "a new version is available" question (UI-SETTINGS-ABOUT). Bound, not looked up (MVVM_01).
///
/// <para>UPDATEUX_01 — "Remind me later" is a real button. Before, the only way to say "not now"
/// was Escape or the title-bar X, which nobody finds; people picked "Never tell me again" by
/// mistake and then never received another fix.</para>
/// <para>UPDATEUX_02 — the download size and a rough time are shown BEFORE the user agrees.</para>
/// <para>UPDATEUX_05 — when this copy cannot verify an update by itself (unsigned build), the
/// window says so up front and the green button opens the download page instead of downloading
/// hundreds of MB that would then be refused.</para>
/// </summary>
public sealed class UpdateAvailableViewModel : ViewModelBase
{
    public UpdateAvailableViewModel(Version local, string remoteTag, string? releaseNotes, string? downloadSizeText, bool canInstallItself)
    {
        YourVersion = $"Your version:  {local}";
        NewVersion = $"New version:  {remoteTag.TrimStart('v', 'V')}";
        HasNotes = !string.IsNullOrWhiteSpace(releaseNotes);
        ReleaseNotes = HasNotes ? releaseNotes!.Trim() : "No release notes were provided for this release.";
        CanInstallItself = canInstallItself;
        DownloadSizeText = canInstallItself
            ? downloadSizeText ?? string.Empty
            : "This copy of Free Video Studio cannot install updates by itself because it is not digitally signed. "
              + "The green button opens the download page: download the new version there and run it once. "
              + "After that, updates install automatically.";
        UpdateButtonText = canInstallItself
            ? "Yes — download and install the new version now."
            : "Open the download page.";

        UpdateNowCommand = new RelayCommand(() => Choose(UpdateChoice.UpdateNow));
        LaterCommand = new RelayCommand(() => Choose(UpdateChoice.NotNow));
        SkipCommand = new RelayCommand(() => Choose(UpdateChoice.SkipThisVersion));
        NeverCommand = new RelayCommand(() => Choose(UpdateChoice.NeverTellMeAgain));
    }

    public string YourVersion { get; }
    public string NewVersion { get; }
    public string ReleaseNotes { get; }
    public bool HasNotes { get; }
    public string DownloadSizeText { get; }
    public bool HasDownloadSizeText => !string.IsNullOrWhiteSpace(DownloadSizeText);
    public bool CanInstallItself { get; }
    public bool CannotInstallItself => !CanInstallItself;
    public string UpdateButtonText { get; }

    public UpdateChoice Choice { get; private set; } = UpdateChoice.Dismissed;

    public ICommand UpdateNowCommand { get; }
    public ICommand LaterCommand { get; }
    public ICommand SkipCommand { get; }
    public ICommand NeverCommand { get; }

    public event Action? CloseRequested;

    private void Choose(UpdateChoice choice)
    {
        Choice = choice;
        CloseRequested?.Invoke();
    }
}

/// <summary>
/// The download / verify / ready window (UpdateDownloadWindow). Bound, not looked up (MVVM_01).
///
/// <para>UPDATEUX_03 — the window opens the instant the user says yes and shows every stage: the
/// check of what needs downloading, the download with speed and time left, the safety check, and
/// the final choice. Before, the first stage ran with no window at all and the safety check sat at
/// "100%" with a Cancel button that did nothing.</para>
/// <para>UPDATEUX_04 — the end of the download is a choice ("Restart &amp; update now" / "Update
/// when I close the app") instead of a message telling the user to close the app themselves.</para>
/// </summary>
public sealed class UpdateDownloadViewModel : ViewModelBase
{
    private string _headline = "Getting the update ready…";
    private string _status = "Checking which parts of the update you need…";
    private double _percent;
    private bool _isIndeterminate = true, _canCancel = true, _isReady;

    public UpdateDownloadViewModel()
    {
        CancelCommand = new RelayCommand(() => CancelRequested?.Invoke(), () => CanCancel);
        RestartNowCommand = new RelayCommand(() => ReadyChoice?.Invoke(true));
        LaterCommand = new RelayCommand(() => ReadyChoice?.Invoke(false));
    }

    public string Headline { get => _headline; set => SetProperty(ref _headline, value); }
    public string Status { get => _status; set => SetProperty(ref _status, value); }
    public double Percent { get => _percent; set => SetProperty(ref _percent, value); }
    public bool IsIndeterminate { get => _isIndeterminate; set => SetProperty(ref _isIndeterminate, value); }

    public bool CanCancel
    {
        get => _canCancel;
        set
        {
            if (!SetProperty(ref _canCancel, value)) return;
            ((RelayCommand)CancelCommand).NotifyCanExecuteChanged();
        }
    }

    /// <summary>The download is verified and the installer is waiting: show the two choices.</summary>
    public bool IsReady
    {
        get => _isReady;
        set { if (SetProperty(ref _isReady, value)) OnPropertyChanged(nameof(IsWorking)); }
    }

    public bool IsWorking => !_isReady;

    public ICommand CancelCommand { get; }
    public ICommand RestartNowCommand { get; }
    public ICommand LaterCommand { get; }

    /// <summary>Cancel pressed (or the window closed) while still working.</summary>
    public event Action? CancelRequested;

    /// <summary>true = restart now, false = update when the app is closed.</summary>
    public event Action<bool>? ReadyChoice;

    public void Stage(string headline, string status, double? fraction, bool canCancel)
    {
        Headline = headline;
        Status = status;
        IsIndeterminate = fraction is null;
        Percent = Math.Clamp((fraction ?? 0) * 100, 0, 100);
        CanCancel = canCancel;
    }

    public void Ready()
    {
        Headline = "The update is ready to install";
        Status = "Free Video Studio will close, install the update and open again by itself — usually within a minute or two. "
               + "If you have unsaved work, you will be asked to save it first.";
        IsIndeterminate = false;
        Percent = 100;
        CanCancel = false;
        IsReady = true;
    }
}
