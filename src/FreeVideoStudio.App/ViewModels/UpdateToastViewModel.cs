using System.Windows.Input;

namespace FreeVideoStudio.App.ViewModels;

public sealed class UpdateToastViewModel : ViewModelBase
{
    private string _headline = "Updating Free Video Studio";
    private string _status = "Getting the update ready…";
    private double _percent;
    private bool _isIndeterminate = true;
    private ToastState _state = ToastState.Working;
    private string _whatsNew = string.Empty;

    public enum ToastState { Working, Ready, Failed, Paused }

    public UpdateToastViewModel()
    {
        RestartCommand = new RelayCommand(() => RestartRequested?.Invoke());
        LaterCommand = new RelayCommand(() => LaterRequested?.Invoke());
        SkipCommand = new RelayCommand(() => SkipRequested?.Invoke());
        PauseCommand = new RelayCommand(() => PauseRequested?.Invoke());
        RetryCommand = new RelayCommand(() => RetryRequested?.Invoke());
        CloseCommand = new RelayCommand(() => CloseRequested?.Invoke());
    }

    public string Headline { get => _headline; private set => SetProperty(ref _headline, value); }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public double Percent { get => _percent; private set => SetProperty(ref _percent, value); }
    public bool IsIndeterminate { get => _isIndeterminate; private set => SetProperty(ref _isIndeterminate, value); }

    public string WhatsNew { get => _whatsNew; private set { if (SetProperty(ref _whatsNew, value)) OnPropertyChanged(nameof(HasWhatsNew)); } }
    public bool HasWhatsNew => _whatsNew.Length > 0 && _state is ToastState.Working or ToastState.Ready;

    public void SetReleaseNotes(IReadOnlyList<string> highlights) =>
        WhatsNew = string.Join(Environment.NewLine, highlights.Select(h => "• " + h));

    public ToastState State
    {
        get => _state;
        private set
        {
            if (!SetProperty(ref _state, value)) return;
            OnPropertyChanged(nameof(IsWorking));
            OnPropertyChanged(nameof(IsReady));
            OnPropertyChanged(nameof(IsFailed));
            OnPropertyChanged(nameof(IsPaused));
            OnPropertyChanged(nameof(IsFinished));
            OnPropertyChanged(nameof(HasWhatsNew));
        }
    }

    public bool IsWorking => _state == ToastState.Working;
    public bool IsReady => _state == ToastState.Ready;
    public bool IsFailed => _state == ToastState.Failed;
    public bool IsPaused => _state == ToastState.Paused;
    public bool IsFinished => _state is ToastState.Failed or ToastState.Paused;

    public ICommand RestartCommand { get; }
    public ICommand LaterCommand { get; }
    public ICommand SkipCommand { get; }
    public ICommand PauseCommand { get; }
    public ICommand RetryCommand { get; }
    public ICommand CloseCommand { get; }

    public event Action? RestartRequested;
    public event Action? LaterRequested;
    public event Action? SkipRequested;
    public event Action? PauseRequested;
    public event Action? RetryRequested;
    public event Action? CloseRequested;

    public void Working(string version, string status, double? fraction)
    {
        Headline = $"Updating to version {version}";
        Status = status;
        IsIndeterminate = fraction is null;
        Percent = Math.Clamp((fraction ?? 0) * 100, 0, 100);
        State = ToastState.Working;
    }

    public void Ready(string version, string summary)
    {
        Headline = $"Version {version} is ready to install";
        Status = summary + Environment.NewLine
               + "Restart now to install it (about a minute). Or choose Later: it requests administrator permission (UAC) and installs by itself the next time you close Free Video Studio.";
        IsIndeterminate = false;
        Percent = 100;
        State = ToastState.Ready;
    }

    public void Failed(string reason)
    {
        Headline = "The update could not be downloaded";
        Status = reason + Environment.NewLine + "Your current version keeps working. Nothing was changed.";
        IsIndeterminate = false;
        State = ToastState.Failed;
    }

    public void Paused()
    {
        Headline = "Update paused";
        Status = "What was already downloaded is kept. It continues the next time Free Video Studio starts, or when you check for updates.";
        IsIndeterminate = false;
        State = ToastState.Paused;
    }
}
