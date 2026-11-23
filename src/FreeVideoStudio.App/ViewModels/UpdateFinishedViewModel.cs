using System.Windows.Input;

namespace FreeVideoStudio.App.ViewModels;

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

    private string _whatsNew = string.Empty;

    public string WhatsNew { get => _whatsNew; private set { if (SetProperty(ref _whatsNew, value)) OnPropertyChanged(nameof(HasWhatsNew)); } }
    public bool HasWhatsNew => _whatsNew.Length > 0 && !_isFinishing;

    public void SetWhatsNew(IReadOnlyList<string> highlights) =>
        WhatsNew = string.Join(Environment.NewLine, highlights.Select(h => "• " + h));

    public ICommand OkCommand { get; }
    public event Action? CloseRequested;

    public void SetDone(string headline, string message)
    {
        Headline = headline;
        Message = message;
        IsFinishing = false;
        OnPropertyChanged(nameof(HasWhatsNew));
    }
}
