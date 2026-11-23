using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using FreeVideoStudio.App.Abstractions;
using FreeVideoStudio.App.Services;
using FreeVideoStudio.Core.Abstractions;
using FreeVideoStudio.Core.Infrastructure;

namespace FreeVideoStudio.App.Infrastructure;

public sealed class AppServices
{
    private static AppServices? _current;

    public static AppServices Current
        => _current ?? throw new InvalidOperationException(
            "AppServices.Current was read before Initialize() ran. The composition root is built "
          + "once in Program.RunUiAsync, before Avalonia starts. If you are seeing this from a "
          + "test, call AppServices.InitializeForTests(...) in the fixture.");

    public ApplicationPaths Paths { get; }

    public IClock Clock { get; }

    public IActiveWindowProvider Windows { get; }

    public IUserNotifier Notifier { get; }

    public IFaultSink Faults { get; }

    public IProjectStore Projects { get; }

    public IFilePickerService FilePicker { get; }

    public SettingsWindowServices SettingsWindow { get; }

    private AppServices(
        ApplicationPaths paths,
        IClock clock,
        IActiveWindowProvider windows,
        IUserNotifier notifier,
        IFaultSink faults,
        IProjectStore projects,
        IFilePickerService filePicker,
        SettingsWindowServices settingsWindow)
    {
        Paths = paths;
        Clock = clock;
        Windows = windows;
        Notifier = notifier;
        Faults = faults;
        Projects = projects;
        FilePicker = filePicker;
        SettingsWindow = settingsWindow;
    }

    public static AppServices Initialize(ApplicationPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        if (_current is not null)
        {
            RuntimeLog.Fail("COMPOSITION",
                "AppServices.Initialize called twice. The second call is ignored; the first graph stands.");
            return _current;
        }

        var windows  = new AvaloniaWindowProvider();
        var notifier = new AvaloniaUserNotifier(windows);
        var faults   = new UserFacingFaultSink(notifier);

        _current = new AppServices(
            paths:      paths,
            clock:      SystemClock.Instance,
            windows:    windows,
            notifier:   notifier,
            faults:     faults,
            projects:   FileProjectStore.Instance,
            filePicker: new StorageProviderFilePicker(windows, faults),
            settingsWindow: SettingsWindowServices.CreateDefault(paths));

        Core.Abstractions.Faults.Install(faults);

        RuntimeLog.Info("COMPOSITION", "Application service graph constructed.");
        return _current;
    }

    public static AppServices InitializeForTests(
        ApplicationPaths paths,
        IClock clock,
        IActiveWindowProvider windows,
        IUserNotifier notifier,
        IFaultSink faults,
        IProjectStore projects,
        IFilePickerService filePicker,
        SettingsWindowServices? settingsWindow = null)
    {
        _current = new AppServices(paths, clock, windows, notifier, faults, projects, filePicker,
            settingsWindow ?? SettingsWindowServices.CreateDefault(paths));

        Core.Abstractions.Faults.ResetForTests();
        Core.Abstractions.Faults.Install(faults);

        return _current;
    }

    public static void ResetForTests()
    {
        _current = null;
        Core.Abstractions.Faults.ResetForTests();
    }
}

public sealed class AvaloniaWindowProvider : IActiveWindowProvider
{
    public Window? ActiveWindow
    {
        get
        {
            try
            {
                if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
                    return null;

                foreach (Window w in desktop.Windows)
                    if (w.IsActive) return w;

                if (desktop.MainWindow is { } main) return main;

                foreach (Window w in desktop.Windows)
                    return w;

                return null;
            }
            catch (Exception ex)
            {
                RuntimeLog.WarnThrottled("COMPOSITION", $"Active window lookup failed: {ex.Message}");
                return null;
            }
        }
    }
}
