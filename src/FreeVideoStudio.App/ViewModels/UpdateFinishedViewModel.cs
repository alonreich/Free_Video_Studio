// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/05_SYSTEM_LIFECYCLE_STORAGE.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using System.Windows.Input;

namespace FreeVideoStudio.App.ViewModels;

/// <summary>
/// UPGRADEUX_05 — the small "Finishing the update… / ✓ Updated" card shown on the new app's first
/// launch. Before it existed the main window opened greyed-out and unclickable for several
/// seconds with no explanation (editing stays disabled until the commit, 05 SYS-UPGRADE), and
/// nothing ever confirmed that the update had worked.
/// </summary>
public sealed class UpdateFinishedViewModel : ViewModelBase
{
    private string _headline = "Finishing the update…";
    private string _message = "Making sure everything started correctly. This takes a few seconds — the window becomes usable as soon as it is done.";
    private bool _isFinishing = true;

    public UpdateFinishedViewModel()
    {
        OkCommand = new RelayCommand(() => CloseRequested?.Invoke());
    }

    public string Headline { get => _headline; private set => SetProperty(ref _headline, value); }
    public string Message { get => _message; private set => SetProperty(ref _message, value); }
    public bool IsFinishing { get => _isFinishing; private set { if (SetProperty(ref _isFinishing, value)) OnPropertyChanged(nameof(IsDone)); } }
    public bool IsDone => !_isFinishing;

    public ICommand OkCommand { get; }
    public event Action? CloseRequested;

    public void SetDone(string headline, string message)
    {
        Headline = headline;
        Message = message;
        IsFinishing = false;
    }
}
