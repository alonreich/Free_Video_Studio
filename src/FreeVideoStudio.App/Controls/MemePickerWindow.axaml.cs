using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using FreeVideoStudio.App.Infrastructure;
using FreeVideoStudio.App.Services;
using FreeVideoStudio.App.ViewModels;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;

namespace FreeVideoStudio.App.Controls;

/// <summary>
/// MEMEPICK_01 — one row of the meme picker: the scanned <see cref="MemeItem"/> plus its preview
/// picture, which arrives later (<see cref="MemeThumbnailCache"/>), its ★ (MEMEFAV_01) and its shape
/// warning, which depends on the chosen mode (MEMEWARN_02: the letterbox warning matters only for
/// FULL SCREEN; a corner box fits any shape).
/// </summary>
public sealed class MemePickerRow : ViewModelBase
{
    private readonly bool _portrait;
    private bool _fullScreen = true;
    private bool _isFavorite;

    public MemePickerRow(MemeItem item, bool portrait, bool missing = false)
    {
        Item = item;
        _portrait = portrait;
        IsMissing = missing;
        _isFavorite = MemePickerPrefsStore.IsFavorite(item.FullPath);
        RecentRank = MemePickerPrefsStore.RecentRank(item.FullPath);
        ToggleFavoriteCommand = new RelayCommand(() =>
        {
            IsFavorite = !IsFavorite;
            MemePickerPrefsStore.SetFavorite(Item.FullPath, IsFavorite);
            FavoriteToggled?.Invoke(this);
        });
    }

    public MemeItem Item { get; }
    public bool IsMissing { get; }
    public int RecentRank { get; }
    public string FileName => IsMissing ? $"{Item.FileName}  (file missing)" : Item.FileName;
    public string KindIcon => Item.IsImage ? "🖼" : "🎬";

    /// <summary>Raised after ★ was pressed (the window re-sorts).</summary>
    public event Action<MemePickerRow>? FavoriteToggled;

    public ICommand ToggleFavoriteCommand { get; }

    public bool IsFavorite
    {
        get => _isFavorite;
        private set { if (SetProperty(ref _isFavorite, value)) OnPropertyChanged(nameof(StarText)); }
    }

    public string StarText => _isFavorite ? "★" : "☆";

    /// <summary>MEMEWARN_02 — the shape warnings apply to FULL SCREEN only.</summary>
    public void SetFullScreen(bool fullScreen)
    {
        if (_fullScreen == fullScreen) return;
        _fullScreen = fullScreen;
        OnPropertyChanged(nameof(WarningText));
        OnPropertyChanged(nameof(HasWarning));
        OnPropertyChanged(nameof(RatioWarning));
        OnPropertyChanged(nameof(Tip));
    }

    /// <summary>UI-MEMESELECT — wide meme on a portrait video (full screen): letterboxed.</summary>
    public bool RatioWarning => _fullScreen && _portrait && Item.AspectRatio > 0.85f;

    /// <summary>MEMEWARN_02 — tall meme on a landscape video (full screen): pillarboxed.</summary>
    public bool TallWarning => _fullScreen && !_portrait && Item.AspectRatio > 0 && Item.AspectRatio < 0.85f;

    public bool HasWarning => RatioWarning || TallWarning;

    public string WarningText => RatioWarning
        ? "⚠ Landscape — will be letterboxed in portrait"
        : TallWarning ? "⚠ Portrait — will have black side bars in landscape" : "";

    public string Details =>
        (Item.IsImage ? "Picture" : "Video") +
        (Item.Width > 0 && Item.Height > 0 ? $"  ·  {Item.Width}×{Item.Height}" : "") +
        (RecentRank != int.MaxValue ? "  ·  used recently" : "");

    public string Tip => RatioWarning ? "Fit for landscape — may be letterboxed in portrait mode"
        : TallWarning ? "Fit for portrait — black bars at the sides in a landscape video"
        : Item.FileName;

    private Bitmap? _thumbnail;
    public Bitmap? Thumbnail
    {
        get => _thumbnail;
        set { if (SetProperty(ref _thumbnail, value)) OnPropertyChanged(nameof(HasThumbnail)); }
    }
    public bool HasThumbnail => _thumbnail != null;
}

/// <summary>
/// MEMEMODE_01 — what the popup returns: the meme, how it plays and which part of it is used.
/// <see cref="DurationSec"/> is the length that plays (a picture's chosen 1–10 s, a video's chosen
/// part), or null when a video's length could not be read (the caller probes it).
/// </summary>
public sealed record MemePickResult(
    MemeItem Item,
    double? DurationSec,
    FreeVideoStudio.Core.Media.MemePresentationMode Mode,
    FreeVideoStudio.Core.Media.MemeOverlayCorner Corner,
    FreeVideoStudio.Core.Media.MemeOverlaySize Size,
    bool PlaySound,
    double ClipInSec = 0,
    double PosX = -1,
    double PosY = -1,
    FreeVideoStudio.Core.Media.MemeOverlayAnimation Animation = FreeVideoStudio.Core.Media.MemeOverlayAnimation.None,
    FreeVideoStudio.Core.Media.MemeTransition Transition = FreeVideoStudio.Core.Media.MemeTransition.Cut)
{
    /// <summary>The placement this choice describes, at <paramref name="at"/> (clip-relative source seconds).</summary>
    public FreeVideoStudio.Core.Media.MemePlacement ToPlacement(double at, double durationSec) =>
        new(Item.FullPath, at, durationSec, "", Mode, Corner, Size, PlaySound,
            Item.IsImage ? 0 : Math.Max(0, ClipInSec), PosX, PosY, Animation, Transition);
}

/// <summary>
/// MEME_06 — "which meme goes here?", asked at the moment ADD MEME is pressed.
///
/// <para>
/// Deliberately a themed Avalonia window and not a Win32 file dialog. The memes a user actually
/// wants live in the app's own meme folder, already scanned and dimension-probed by
/// <see cref="MemeCatalog"/>; a file browser would drop them somewhere on their disk with no
/// guarantee the file is even a video, and would look like a different program interrupting the
/// editor — the same complaint DIALOG_01 records about NativeDialog.
/// </para>
/// <para>
/// MEMEPICK_01 — preview pictures, a name search, the shape warnings (UI-MEMESELECT) and the two
/// "download more" buttons live here too, so the editor never has to be closed to find or fetch a
/// meme. MEMEPICK_02 — the list handed over is shown at once, then RE-SCANNED in the background.
/// MEMEPICK_03 — a live preview shows the meme playing where it will play (and lets a corner meme be
/// dragged anywhere, MEMEPOS_01). MEMEFAV_01 — favourites first, then recently used.
/// MEMEPICK_05 — a double-click only SELECTS; the meme is used with USE THIS MEME (a double-click used
/// to place it straight away as FULL SCREEN, skipping every choice below the list).
/// </para>
/// <para>
/// Returns the chosen <see cref="MemeItem"/> in <see cref="Result"/>, or null for cancel, and the
/// newest scan in <see cref="LatestItems"/> so the owner can keep its own copy current.
/// </para>
/// </summary>
public partial class MemePickerWindow : Window
{
    public MemeItem? Result { get; private set; }

    /// <summary>MEMEMODE_01 — the "how should it play?" half, bound (MVVM_01).</summary>
    public MemeChoiceViewModel Choice { get; } = new();

    private string? _preselectPath;
    private FreeVideoStudio.Core.Media.MemePlacement? _existing;
    private int _durationGeneration;

    /// <summary>MEMEPICK_02 — the newest list this window knows about (real memes only).</summary>
    public IReadOnlyList<MemeItem> LatestItems { get; private set; } = Array.Empty<MemeItem>();

    private readonly CancellationTokenSource _cts = new();
    private List<MemePickerRow> _rows = new();

    /// <summary>
    /// CACHEBOUND_03 — pictures for the realized (on-screen) rows plus a few either side only; a row
    /// scrolled far away, filtered out or replaced by a re-scan cancels its request and gives its
    /// picture back to <see cref="MemeThumbnailCache"/> (CACHEBOUND_02), which disposes it when evicted.
    /// </summary>
    private readonly ViewportThumbnailLoader<MemePickerRow, Bitmap> _thumbs = new(
        (row, ct) => Task.Run(() => MemeThumbnailCache.AcquireAsync(row.Item.FullPath, row.Item.IsImage, ct), ct),
        (row, bitmap) => row.Thumbnail = bitmap,
        action => Dispatcher.UIThread.Post(action),
        action => Dispatcher.UIThread.Post(action, DispatcherPriority.Background));
    private bool _portrait;
    private int _scanGeneration;
    private bool _downloading;
    private ListBox? _list;

    public MemePickerWindow()
    {
        InitializeComponent();
        DataContext = Choice;

        var list = this.FindControl<ListBox>("MemeList");
        _list = list;
        var useBtn = this.FindControl<Button>("UseBtn");
        var cancelBtn = this.FindControl<Button>("CancelBtn");
        var search = this.FindControl<TextBox>("SearchBox");
        var moreVideos = this.FindControl<Button>("DownloadVideosBtn");
        var morePictures = this.FindControl<Button>("DownloadPicturesBtn");
        var soundBtn = this.FindControl<Button>("OpenWithSoundBtn");

        if (list != null)
        {
            list.ContainerPrepared += (_, e) => _thumbs.Realized(e.Container, e.Index);
            list.ContainerIndexChanged += (_, e) => _thumbs.IndexChanged(e.Container, e.NewIndex);
            list.ContainerClearing += (_, e) => _thumbs.Cleared(e.Container);
        }

        if (list != null && useBtn != null)
        {
            list.SelectionChanged += (_, _) =>
            {
                useBtn.IsEnabled = list.SelectedItem is MemePickerRow;
                OnMemeSelected(list.SelectedItem as MemePickerRow);
            };
            useBtn.Click += (_, _) =>
            {
                if (list.SelectedItem is MemePickerRow picked)
                {
                    Result = picked.Item;
                    MemePickerPrefsStore.RecordUse(picked.IsMissing ? "" : picked.Item.FullPath, Choice.ToLastChoice());
                    Close();
                }
            };
        }

        if (soundBtn != null) soundBtn.Click += (_, _) => OpenWithSound();
        if (cancelBtn != null) cancelBtn.Click += (_, _) => { Result = null; Close(); };
        if (search != null) search.TextChanged += (_, _) => ApplyFilter();
        if (moreVideos != null) moreVideos.Click += async (_, _) => await DownloadAsync(MemeCategory.Video);
        if (morePictures != null) morePictures.Click += async (_, _) => await DownloadAsync(MemeCategory.Image);

        Choice.ModeChanged += () => { foreach (var r in _rows) r.SetFullScreen(Choice.IsFullScreen); };

        MemeDirectory.Changed += OnMemeDirectoryChanged;
        Opened += (_, _) => _ = RescanAsync();
        Closed += (_, _) =>
        {
            MemeDirectory.Changed -= OnMemeDirectoryChanged;
            _cts.Cancel();
            _thumbs.Dispose();
        };
    }

    private void OnMemeDirectoryChanged() => Dispatcher.UIThread.Post(() => _ = RescanAsync());

    public void SetItems(IEnumerable<MemeItem> items, bool portrait = false)
    {
        _portrait = portrait;
        Choice.PreviewPortrait = portrait;
        ApplyItems(items);
    }

    /// <summary>
    /// MEME_06 — fills the list. Download-action rows are filtered out: they are a main-screen
    /// affordance, and picking one here would place a meme with no file behind it. MEMEPICK_04 — a meme
    /// being EDITED whose file is gone keeps a "(file missing)" row, so its mode can still change.
    /// </summary>
    private void ApplyItems(IEnumerable<MemeItem> items)
    {
        var real = items.Where(i => !i.IsDownloadAction && !string.IsNullOrWhiteSpace(i.FullPath)).ToList();
        LatestItems = real;

        string? selectedPath = (_list?.SelectedItem as MemePickerRow)?.Item.FullPath ?? _preselectPath;
        _preselectPath = null;

        foreach (var old in _rows) old.FavoriteToggled -= OnFavoriteToggled;
        _rows = real.Select(i => new MemePickerRow(i, _portrait)).ToList();
        if (_existing != null && !_rows.Any(r => string.Equals(r.Item.FullPath, _existing.FilePath, StringComparison.OrdinalIgnoreCase)))
        {
            bool missing = !System.IO.File.Exists(_existing.FilePath);
            var item = new MemeItem
            {
                FileName = System.IO.Path.GetFileName(_existing.FilePath),
                FullPath = _existing.FilePath,
                IsImage = CornerMemeFrames.IsStillImage(_existing.FilePath),
            };
            _rows.Insert(0, new MemePickerRow(item, _portrait, missing));
        }
        foreach (var r in _rows)
        {
            r.SetFullScreen(Choice.IsFullScreen);
            r.FavoriteToggled += OnFavoriteToggled;
        }
        SortRows();
        ApplyFilter();

        if (_list != null && selectedPath != null)
        {
            var again = (_list.ItemsSource as IEnumerable<MemePickerRow>)?.FirstOrDefault(r =>
                string.Equals(r.Item.FullPath, selectedPath, StringComparison.OrdinalIgnoreCase));
            if (again != null) _list.SelectedItem = again;
        }
    }

    /// <summary>MEMEFAV_01 — ★ first, then the most recently used, then by name.</summary>
    private void SortRows()
    {
        _rows = _rows
            .OrderByDescending(r => r.IsMissing)
            .ThenByDescending(r => r.IsFavorite)
            .ThenBy(r => r.RecentRank)
            .ThenBy(r => r.Item.FileName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void OnFavoriteToggled(MemePickerRow row)
    {
        var selected = _list?.SelectedItem;
        SortRows();
        ApplyFilter();
        if (_list != null && selected != null) _list.SelectedItem = selected;
    }

    private void ApplyFilter()
    {
        string query = this.FindControl<TextBox>("SearchBox")?.Text?.Trim() ?? "";
        var shown = string.IsNullOrEmpty(query)
            ? _rows
            : _rows.Where(r => r.FileName.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();

        _thumbs.SetRows(shown);
        if (_list != null) _list.ItemsSource = shown;

        var empty = this.FindControl<TextBlock>("EmptyText");
        if (empty != null) empty.IsVisible = shown.Count == 0;
    }

    /// <summary>MEMEPICK_02 — re-scan the meme folder; only the newest scan may write.</summary>
    private async Task RescanAsync()
    {
        int generation = Interlocked.Increment(ref _scanGeneration);
        try
        {
            List<MemeItem> scanned = await MemeManagementService.ScanMemesAsync();
            if (generation != Volatile.Read(ref _scanGeneration) || _cts.IsCancellationRequested) return;
            ApplyItems(scanned);
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("MEME", $"Meme picker re-scan failed: {ex.Message}");
        }
    }

    private async Task DownloadAsync(MemeCategory category)
    {
        if (_downloading) return;
        _downloading = true;
        try
        {
            var (count, error) = await MemeManagementService.DownloadCloudMemesAsync(this, category);
            string label = MemeCatalog.LabelFor(category);
            if (error != null)
                await ErrorReporter.ShowAsync(this, $"Problem downloading {label}", error,
                    "See the log entries tagged [Memes] for the per-file detail.");
            else
                ShowStatus(count == 0
                    ? $"You already have all available {label}."
                    : $"✓ Downloaded {count} new {label}.");
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("MEME", $"Meme download from the picker failed: {ex.Message}");
        }
        finally { _downloading = false; }
    }

    /// <summary>
    /// MEMEMODE_01 / MEMETRIM_01 — a meme was chosen: show the cards and the preview, and read a
    /// video's length (off the UI thread, newest selection wins) for the part controls and
    /// "+X.X sec longer". Re-selecting the meme being edited keeps its chosen part.
    /// </summary>
    private void OnMemeSelected(MemePickerRow? row)
    {
        var item = row?.Item;
        Choice.HasMeme = item != null;
        Choice.FileMissing = row?.IsMissing == true;
        int generation = Interlocked.Increment(ref _durationGeneration);
        if (item == null) return;
        bool keepPart = _existing != null && string.Equals(_existing.FilePath, item.FullPath, StringComparison.OrdinalIgnoreCase);

        if (item.IsImage || row!.IsMissing)
        {
            Choice.SetMeme(row!.IsMissing ? null : item.FullPath, item.IsImage, keepPart ? _existing!.ClipInSec + _existing.DurationSec : null, keepPart);
            return;
        }
        Choice.SetMeme(item.FullPath, false, null, keepPart);
        _ = Task.Run(async () =>
        {
            try
            {
                string ffprobe = FreeVideoStudio.Core.Infrastructure.BinaryPathResolver.Resolve("ffprobe.exe", "backend", "binaries");
                double d = await new FreeVideoStudio.Core.Media.MediaProber(ffprobe, item.FullPath).GetDurationAsync();
                Dispatcher.UIThread.Post(() =>
                {
                    if (generation == Volatile.Read(ref _durationGeneration)) Choice.SetFileDuration(d > 0.01 ? d : null, keepPart);
                });
            }
            catch (Exception ex) { RuntimeLog.Swallowed(ex); }
        });
    }

    /// <summary>MEMEPICK_03 — the preview is silent; this opens the meme in the computer's own player to hear it.</summary>
    private void OpenWithSound()
    {
        string? path = Choice.MemePath;
        if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("MEME", $"Could not open '{System.IO.Path.GetFileName(path)}' in the system player: {ex.Message}");
            ShowStatus("Windows could not open that meme in a player.");
        }
    }

    private void ShowStatus(string text)
    {
        var status = this.FindControl<TextBlock>("StatusText");
        if (status == null) return;
        status.Text = text;
        status.IsVisible = true;
    }

    /// <summary>
    /// MEMEMODE_01 — shows the popup and returns the chosen meme with how it plays, or null for
    /// cancel. <paramref name="existing"/> reopens it for a placed meme (MEMEPICK_04): that meme is
    /// preselected, its properties loaded, and the result describes the change. A NEW meme starts from
    /// the last choice used (MEMELAST_01).
    /// </summary>
    public static async Task<MemePickResult?> PickAsync(Window owner, IEnumerable<MemeItem> items,
        bool portrait = false, Action<IReadOnlyList<MemeItem>>? onLatest = null,
        FreeVideoStudio.Core.Media.MemePlacement? existing = null)
    {
        try
        {
            var dlg = new MemePickerWindow();
            if (existing != null)
            {
                dlg._existing = existing;
                dlg._preselectPath = existing.FilePath;
                dlg.Choice.LoadFrom(existing);
            }
            else
            {
                dlg.Choice.LoadLast(MemePickerPrefsStore.Last);
            }
            dlg.SetItems(items, portrait);
            await dlg.ShowDialog(owner);
            onLatest?.Invoke(dlg.LatestItems);
            if (dlg.Result is not MemeItem picked) return null;
            var c = dlg.Choice;
            double? length = c.LengthSeconds > 0.01 ? c.LengthSeconds : c.DurationSec;
            return new MemePickResult(picked, length, c.Mode, c.Corner, c.Size, c.PlaySound,
                picked.IsImage ? 0 : c.ClipInSeconds, c.PosX, c.PosY, c.Animation, c.Transition);
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("MEME", $"Meme picker failed to open: {ex.Message}");
            return null;
        }
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
