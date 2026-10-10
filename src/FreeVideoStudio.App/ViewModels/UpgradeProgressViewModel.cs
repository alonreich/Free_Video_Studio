// [SPEC CONTRACT] STRICT GOVERNANCE:
// Forbidden to modify without reading: docs/05_SYSTEM_LIFECYCLE_STORAGE.md
// Invariants, constants, and threading models must match spec bit-for-bit.

using System.Collections.ObjectModel;
using System.Windows.Input;
using FreeVideoStudio.App.Services;

namespace FreeVideoStudio.App.ViewModels;

/// <summary>One line of the install/update checklist (UPGRADEUX_01).</summary>
public sealed class UpgradeStepItem : ViewModelBase
{
    private string _mark = "○";
    private bool _isActive, _isDone, _isFailed;

    internal UpgradeStepItem(UpgradeStep step, string title)
    {
        Step = step;
        Title = title;
    }

    internal UpgradeStep Step { get; }
    public string Title { get; }

    /// <summary>○ waiting, ▶ now, ✓ done, ✕ stopped here.</summary>
    public string Mark { get => _mark; private set => SetProperty(ref _mark, value); }
    public bool IsActive { get => _isActive; private set => SetProperty(ref _isActive, value); }
    public bool IsDone { get => _isDone; private set => SetProperty(ref _isDone, value); }
    public bool IsFailed { get => _isFailed; private set => SetProperty(ref _isFailed, value); }
    public bool IsPending => !_isActive && !_isDone && !_isFailed;

    internal void SetState(bool active, bool done, bool failed)
    {
        IsActive = active;
        IsDone = done;
        IsFailed = failed;
        Mark = failed ? "✕" : done ? "✓" : active ? "▶" : "○";
        OnPropertyChanged(nameof(IsPending));
    }
}

/// <summary>
/// UPGRADEUX_01 — the state behind <c>UpgradeProgressWindow</c>. Bound, not looked up (MVVM_01).
/// Every mutation happens on the UI thread; <c>UpgradeBrokerHost</c> marshals to it.
/// </summary>
public sealed class UpgradeProgressViewModel : ViewModelBase
{
    private string _title = "Updating Free Video Studio";
    private string _detail = "Getting ready…";
    private double _percent;
    private bool _isIndeterminate = true;
    private string _percentText = string.Empty;
    private bool _isWorking = true, _isAsking, _isFinished, _isFailed, _isSucceeded;
    private string _question = string.Empty, _continueText = "Close it and continue";
    private string _resultTitle = string.Empty, _resultMessage = string.Empty;

    public UpgradeProgressViewModel()
    {
        ContinueCommand = new RelayCommand(() => Answered?.Invoke(RunningAppChoiceValue.Continue));
        CancelCommand = new RelayCommand(() => Answered?.Invoke(RunningAppChoiceValue.Cancel));
        CloseCommand = new RelayCommand(() => CloseRequested?.Invoke());
    }

    /// <summary>Public mirror of the internal choice enum so the window can stay public.</summary>
    public enum RunningAppChoiceValue { Continue, Cancel }

    public ObservableCollection<UpgradeStepItem> Steps { get; } = new();

    public string Title { get => _title; set => SetProperty(ref _title, value); }
    public string Detail { get => _detail; set => SetProperty(ref _detail, value); }
    public double Percent { get => _percent; set => SetProperty(ref _percent, value); }
    public bool IsIndeterminate { get => _isIndeterminate; set => SetProperty(ref _isIndeterminate, value); }
    public string PercentText { get => _percentText; set => SetProperty(ref _percentText, value); }

    /// <summary>Work is running: progress bar visible, no buttons, the window cannot be closed.</summary>
    public bool IsWorking { get => _isWorking; set => SetProperty(ref _isWorking, value); }
    public bool IsAsking { get => _isAsking; set => SetProperty(ref _isAsking, value); }
    public bool IsFinished { get => _isFinished; set => SetProperty(ref _isFinished, value); }
    public bool IsFailed { get => _isFailed; set => SetProperty(ref _isFailed, value); }
    public bool IsSucceeded { get => _isSucceeded; set => SetProperty(ref _isSucceeded, value); }

    public string Question { get => _question; set => SetProperty(ref _question, value); }
    public string ContinueText { get => _continueText; set => SetProperty(ref _continueText, value); }
    public string ResultTitle { get => _resultTitle; set => SetProperty(ref _resultTitle, value); }
    public string ResultMessage { get => _resultMessage; set => SetProperty(ref _resultMessage, value); }

    public ICommand ContinueCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand CloseCommand { get; }

    public event Action<RunningAppChoiceValue>? Answered;
    public event Action? CloseRequested;

    internal void SetSteps(UpgradeKind kind, bool waitsForApp)
    {
        Title = UpgradeText.Title(kind);
        Steps.Clear();
        IEnumerable<UpgradeStep> steps = kind == UpgradeKind.Recovery
            ? [UpgradeStep.Permission, UpgradeStep.Settings, UpgradeStep.Start]
            : Enum.GetValues<UpgradeStep>().Where(s =>
                (s != UpgradeStep.CloseOldVersion || waitsForApp) &&
                (s != UpgradeStep.Settings || kind != UpgradeKind.Install));
        foreach (UpgradeStep step in steps) Steps.Add(new UpgradeStepItem(step, UpgradeText.StepTitle(step, kind)));
    }

    internal void MoveTo(UpgradeStep step, string? detail, double? fraction)
    {
        int index = Steps.ToList().FindIndex(s => s.Step == step);
        if (index >= 0)
        {
            for (int i = 0; i < Steps.Count; i++)
                Steps[i].SetState(active: i == index, done: i < index, failed: false);
            if (string.IsNullOrWhiteSpace(detail)) detail = Steps[index].Title + "…";
        }

        if (!string.IsNullOrWhiteSpace(detail)) Detail = detail!;
        IsIndeterminate = fraction is null;
        Percent = Math.Clamp((fraction ?? 0) * 100, 0, 100);
        PercentText = fraction is null ? string.Empty : $"{Percent:0}%";
    }

    internal void Ask(string question, bool canClose)
    {
        Question = question;
        ContinueText = canClose ? "CLOSE IT AND CONTINUE" : "TRY AGAIN";
        IsWorking = false;
        IsAsking = true;
    }

    internal void StopAsking()
    {
        IsAsking = false;
        IsWorking = true;
    }

    internal void Succeed(string message)
    {
        foreach (UpgradeStepItem s in Steps) s.SetState(false, true, false);
        IsIndeterminate = false;
        Percent = 100;
        PercentText = "100%";
        ResultTitle = "✓ All done";
        ResultMessage = message;
        Detail = message;
        IsWorking = false;
        IsAsking = false;
        IsSucceeded = true;
        IsFinished = true;
    }

    internal void Fail(string title, string message)
    {
        UpgradeStepItem? current = Steps.FirstOrDefault(s => s.IsActive);
        current?.SetState(false, false, true);
        IsIndeterminate = false;
        PercentText = string.Empty;
        ResultTitle = title;
        ResultMessage = message;
        Detail = title;
        IsWorking = false;
        IsAsking = false;
        IsFailed = true;
        IsFinished = true;
    }
}
