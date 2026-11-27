using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FreeVideoStudio.App.Infrastructure;
using FreeVideoStudio.App.Services;
using FreeVideoStudio.App.ViewModels;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;

namespace FreeVideoStudio.App.Controls;

public sealed class MemePickerRow : ViewModelBase
{
    private readonly bool _portrait;
    private bool _fullScreen = true;
    private bool _isFavorite;
    private bool _isSelected;

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
        SelectCommand = new RelayCommand(() => SelectRequested?.Invoke(this));
    }

    public MemeItem Item { get; }
    public bool IsMissing { get; }
    public int RecentRank { get; }
    public string FileName => IsMissing ? $"{Item.FileName}  (file missing)" : Item.FileName;
    public string KindIcon => Item.IsImage ? "🖼" : "🎬";

    public string Title => (IsMissing ? "⚠ " : "") + TitleFor(Item.FileName);

    public static string TitleFor(string fileName)
    {
        string name = System.IO.Path.GetFileNameWithoutExtension(fileName ?? "").Replace('_', ' ').Replace('-', ' ').Trim();
        while (name.Contains("  ", StringComparison.Ordinal)) name = name.Replace("  ", " ", StringComparison.Ordinal);
        return name.Length == 0 ? fileName ?? "" : name;
    }

    public string BadgeText => Item.IsImage ? "🖼 PICTURE" : "🎬 " + FormatLength(Item.DurationSec);

    public static string FormatLength(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds <= 0) return "VIDEO";
        int s = (int)Math.Round(seconds);
        if (s < 1) return seconds.ToString("0.0", CultureInfo.InvariantCulture) + " s";
        return $"{s / 60}:{s % 60:00}";
    }

    public event Action<MemePickerRow>? FavoriteToggled;
    public event Action<MemePickerRow>? SelectRequested;

    public ICommand ToggleFavoriteCommand { get; }
    public ICommand SelectCommand { get; }

    public bool IsFavorite
    {
        get => _isFavorite;
        private set { if (SetProperty(ref _isFavorite, value)) OnPropertyChanged(nameof(StarText)); }
    }

    public string StarText => _isFavorite ? "★" : "☆";

    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }

    public void SetFullScreen(bool fullScreen)
    {
        if (_fullScreen == fullScreen) return;
        _fullScreen = fullScreen;
        OnPropertyChanged(nameof(WarningText));
        OnPropertyChanged(nameof(HasWarning));
        OnPropertyChanged(nameof(RatioWarning));
        OnPropertyChanged(nameof(Tip));
    }

    public bool RatioWarning => _fullScreen && _portrait && Item.AspectRatio > 0.85f;

    public bool TallWarning => _fullScreen && !_portrait && Item.AspectRatio > 0 && Item.AspectRatio < 0.85f;

    public bool HasWarning => RatioWarning || TallWarning;

    public string WarningText => RatioWarning
        ? "⚠ Landscape — will be letterboxed in portrait"
        : TallWarning ? "⚠ Portrait — will have black side bars in landscape" : "";

    public string Details =>
        (Item.IsImage ? "Picture" : "Video") +
        (Item.Width > 0 && Item.Height > 0 ? $"  ·  {Item.Width}×{Item.Height}" : "") +
        (RecentRank != int.MaxValue ? "  ·  used recently" : "");

    public string Tip => (RatioWarning ? "⚠ Landscape — it will be letterboxed in a portrait video.\n"
        : TallWarning ? "⚠ Portrait — it will have black bars at the sides in a landscape video.\n" : "")
        + FileName;

    private Bitmap? _thumbnail;
    public Bitmap? Thumbnail
    {
        get => _thumbnail;
        set { if (SetProperty(ref _thumbnail, value)) OnPropertyChanged(nameof(HasThumbnail)); }
    }
    public bool HasThumbnail => _thumbnail != null;
}

public abstract class MemeGalleryLine
{
}

public sealed class MemeGalleryHeader : MemeGalleryLine
{
    public MemeGalleryHeader(string text) => Text = text;
    public string Text { get; }
}

public sealed class MemeGalleryTiles : MemeGalleryLine
{
    public MemeGalleryTiles(IReadOnlyList<MemePickerRow> tiles, int firstIndex)
    {
        Tiles = tiles;
        FirstIndex = firstIndex;
    }

    public IReadOnlyList<MemePickerRow> Tiles { get; }
    public int FirstIndex { get; }
}

public static class MemeGallery
{
    public const double TileWidth = 168;
    public const double TileGap = 10;

    public static int ColumnsFor(double width) =>
        Math.Max(2, (int)Math.Floor((Math.Max(0, width - 24) + TileGap) / (TileWidth + TileGap)));

    public static (List<MemePickerRow> Order, List<MemeGalleryLine> Lines) Build(IReadOnlyList<MemePickerRow> shown, int columns)
    {
        columns = Math.Max(1, columns);
        var order = new List<MemePickerRow>();
        var lines = new List<MemeGalleryLine>();
        void Section(string title, List<MemePickerRow> rows)
        {
            if (rows.Count == 0) return;
            lines.Add(new MemeGalleryHeader($"{title}  ·  {rows.Count}"));
            for (int i = 0; i < rows.Count; i += columns)
            {
                var chunk = rows.Skip(i).Take(columns).ToList();
                lines.Add(new MemeGalleryTiles(chunk, order.Count));
                order.AddRange(chunk);
            }
        }
        Section("🎬 VIDEO MEMES", shown.Where(r => !r.Item.IsImage).ToList());
        Section("🖼 PICTURE MEMES", shown.Where(r => r.Item.IsImage).ToList());
        return (order, lines);
    }
}

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
    FreeVideoStudio.Core.Media.MemeTransition Transition = FreeVideoStudio.Core.Media.MemeTransition.Cut,
    bool? MusicContinues = null,
    bool AtStart = false,
    double? AnchorSec = null)
{
    public FreeVideoStudio.Core.Media.MemePlacement ToPlacement(double at, double durationSec) =>
        new(Item.FullPath, at, durationSec, "", Mode, Corner, Size, PlaySound,
            Item.IsImage ? 0 : Math.Max(0, ClipInSec), PosX, PosY, Animation, Transition, MusicContinues);
}

public partial class MemePickerWindow : Window
{
    public MemeItem? Result { get; private set; }

    public MemeChoiceViewModel Choice { get; } = new();

    private MemePickerContext? _context;
    private bool _landingOk = true;
    private string? _preselectPath;
    private FreeVideoStudio.Core.Media.MemePlacement? _existing;
    private int _durationGeneration;

    public IReadOnlyList<MemeItem> LatestItems { get; private set; } = Array.Empty<MemeItem>();

    private readonly CancellationTokenSource _cts = new();
    private List<MemePickerRow> _rows = new();
    private List<MemePickerRow> _shown = new();
    private List<MemePickerRow> _order = new();
    private List<MemeGalleryLine> _lines = new();
    private int _columns = 3;
    private MemePickerRow? _selected;
    private readonly Dictionary<Control, object[]> _tileKeys = new();

    private readonly ViewportThumbnailLoader<MemePickerRow, Bitmap> _thumbs = new(
        (row, ct) => Task.Run(() => MemeThumbnailCache.AcquireAsync(row.Item.FullPath, row.Item.IsImage, ct), ct),
        (row, bitmap) => row.Thumbnail = bitmap,
        action => Dispatcher.UIThread.Post(action),
        action => Dispatcher.UIThread.Post(action, DispatcherPriority.Background));
    private bool _portrait;
    private int _scanGeneration;
    private bool _downloading;
    private ListBox? _list;
    private Button? _useBtn;

    public MemePickerWindow()
    {
        InitializeComponent();
        DialogFit.Apply(this, capFraction: 0.80, drag: true, sizeKey: "MemePickerSize");
        DataContext = Choice;

        var list = this.FindControl<ListBox>("MemeList");
        _list = list;
        var useBtn = this.FindControl<Button>("UseBtn");
        _useBtn = useBtn;
        var cancelBtn = this.FindControl<Button>("CancelBtn");
        var search = this.FindControl<TextBox>("SearchBox");
        var soundBtn = this.FindControl<Button>("OpenWithSoundBtn");

        if (list != null)
        {
            list.ContainerPrepared += (_, e) => RealizeLine(e.Container, e.Index);
            list.ContainerIndexChanged += (_, e) => RealizeLine(e.Container, e.NewIndex);
            list.ContainerClearing += (_, e) => ClearLine(e.Container);
            list.PropertyChanged += (_, e) =>
            {
                if (e.Property != BoundsProperty) return;
                int cols = MemeGallery.ColumnsFor(list.Bounds.Width);
                if (cols != _columns) { _columns = cols; Rebuild(); }
            };
        }

        if (useBtn != null)
        {
            useBtn.Click += (_, _) =>
            {
                if (_selected is not MemePickerRow picked || !_landingOk || Choice.LengthSeconds <= 0) return;
                if (!picked.IsMissing && !System.IO.File.Exists(picked.Item.FullPath))
                {
                    ShowStatus("That meme file is no longer available. Pick another meme.");
                    return;
                }
                Result = picked.Item;
                if (!picked.IsMissing)
                {
                    MemePickerPrefsStore.RecordUse(picked.Item.FullPath, Choice.ToLastChoice());
                    if (Choice.IsFullScreen) MemePickerPrefsStore.SetMusic(picked.Item.FullPath, Choice.MusicContinues);
                }
                Close();
            };
        }

        ConfigureResultPreview(soundBtn);
        if (cancelBtn != null) cancelBtn.Click += (_, _) => { Result = null; Close(); };
        if (search != null) search.TextChanged += (_, _) => ApplyFilter();
        if (search != null)
        {
            AddHandler(TextInputEvent, (_, e) =>
            {
                if (!TypingGoesToSearch(e.Source, e.Text)) return;
                search.Focus();
                search.Text = (search.Text ?? "") + e.Text;
                search.CaretIndex = search.Text.Length;
                e.Handled = true;
            }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
            search.KeyDown += (_, e) =>
            {
                int step = e.Key switch
                {
                    Avalonia.Input.Key.Down => _columns,
                    Avalonia.Input.Key.Up => -_columns,
                    Avalonia.Input.Key.Right when string.IsNullOrEmpty(search.Text) => 1,
                    Avalonia.Input.Key.Left when string.IsNullOrEmpty(search.Text) => -1,
                    _ => 0,
                };
                if (step == 0 || _order.Count == 0) return;
                int at = _selected == null ? -1 : _order.IndexOf(_selected);
                int next = at < 0 ? 0 : Math.Clamp(at + step, 0, _order.Count - 1);
                SelectRow(_order[next]);
                e.Handled = true;
            };
            Opened += (_, _) => search.Focus();
        }
        Choice.DownloadRequested += () => _ = DownloadAsync();
        Choice.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MemeChoiceViewModel.Placement) or nameof(MemeChoiceViewModel.Mode) or nameof(MemeChoiceViewModel.LengthSec)) UpdateLanding();
        };

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

    public static bool TypingGoesToSearch(object? source, string? text) =>
        !string.IsNullOrEmpty(text) && !char.IsControl(text[0])
        && source is not TextBox && source is not NumericUpDown && source is not MemePartRange
        && (source as Avalonia.Visual)?.FindAncestorOfType<TextBox>() == null;

    private void RealizeLine(Control container, int lineIndex)
    {
        ClearLine(container);
        if (lineIndex < 0 || lineIndex >= _lines.Count || _lines[lineIndex] is not MemeGalleryTiles tiles) return;
        var keys = new object[tiles.Tiles.Count];
        for (int i = 0; i < keys.Length; i++)
        {
            keys[i] = new object();
            _thumbs.Realized(keys[i], tiles.FirstIndex + i);
        }
        _tileKeys[container] = keys;
    }

    private void ClearLine(Control container)
    {
        if (!_tileKeys.Remove(container, out var keys)) return;
        foreach (var k in keys) _thumbs.Cleared(k);
    }

    private void OnMemeDirectoryChanged() => Dispatcher.UIThread.Post(() => _ = RescanAsync());

    public void SetItems(IEnumerable<MemeItem> items, bool portrait = false)
    {
        _portrait = portrait;
        Choice.PreviewPortrait = portrait;
        ApplyItems(items);
    }

    private void ApplyItems(IEnumerable<MemeItem> items)
    {
        var real = items.Where(i => !i.IsDownloadAction && !string.IsNullOrWhiteSpace(i.FullPath)).ToList();
        LatestItems = real;

        string? selectedPath = _selected?.Item.FullPath ?? _preselectPath;
        _preselectPath = null;

        foreach (var old in _rows)
        {
            old.FavoriteToggled -= OnFavoriteToggled;
            old.SelectRequested -= SelectRow;
        }
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
            r.SelectRequested += SelectRow;
        }
        _selected = null;
        SortRows();
        ApplyFilter();

        if (selectedPath != null)
        {
            var again = _shown.FirstOrDefault(r => string.Equals(r.Item.FullPath, selectedPath, StringComparison.OrdinalIgnoreCase));
            if (again != null) SelectRow(again);
        }
    }

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
        SortRows();
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        string query = this.FindControl<TextBox>("SearchBox")?.Text?.Trim() ?? "";
        _shown = string.IsNullOrEmpty(query)
            ? _rows
            : _rows.Where(r => r.FileName.Contains(query, StringComparison.OrdinalIgnoreCase)
                            || r.Title.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        Rebuild();

        var empty = this.FindControl<TextBlock>("EmptyText");
        if (empty != null) empty.IsVisible = _shown.Count == 0;
    }

    private void Rebuild()
    {
        var (order, lines) = MemeGallery.Build(_shown, _columns);
        _order = order;
        _lines = lines;
        _thumbs.SetRows(_order);
        if (_list != null) _list.ItemsSource = _lines;
        if (_selected != null && !_order.Contains(_selected)) SelectRow(null);
        else if (_selected != null) ScrollTo(_selected);
    }

    private void SelectRow(MemePickerRow? row)
    {
        if (ReferenceEquals(row, _selected)) return;
        if (_selected != null) _selected.IsSelected = false;
        _selected = row;
        if (row != null)
        {
            row.IsSelected = true;
            ScrollTo(row);
        }
        if (_useBtn != null) _useBtn.IsEnabled = row != null && _landingOk;
        OnMemeSelected(row);
    }

    private void ScrollTo(MemePickerRow row)
    {
        if (_list == null) return;
        int line = _lines.FindIndex(l => l is MemeGalleryTiles t && t.Tiles.Contains(row));
        if (line >= 0) _list.ScrollIntoView(line);
    }

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

    private async Task DownloadAsync()
    {
        if (_downloading) return;
        _downloading = true;
        try
        {
            var (count, error) = await MemeManagementService.DownloadAllMemesAsync(this);
            if (error != null)
                await ErrorReporter.ShowAsync(this, "Problem getting more memes", error,
                    "See the log entries tagged [Memes] for the per-file detail.");
            else
                ShowStatus(MemeManagementService.DownloadStatus(count));
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("MEME", $"Meme download from the picker failed: {ex.Message}");
        }
        finally { _downloading = false; }
    }

    private void OnMemeSelected(MemePickerRow? row)
    {
        var item = row?.Item;
        Choice.HasMeme = item != null;
        Choice.FileMissing = row?.IsMissing == true;
        int generation = Interlocked.Increment(ref _durationGeneration);
        if (item == null) return;
        bool keepPart = string.Equals(Choice.MemePath, item.FullPath, StringComparison.OrdinalIgnoreCase)
            || (_existing != null && string.Equals(_existing.FilePath, item.FullPath, StringComparison.OrdinalIgnoreCase));
        if (Choice.IsMainScreen && _existing == null && !row!.IsMissing)
            Choice.IsAtStart = Infrastructure.MemePlacementStore.Get(item.FullPath) == Infrastructure.MemePlacement.Start;

        if (item.IsImage || row!.IsMissing)
        {
            Choice.SetMeme(row!.IsMissing ? null : item.FullPath, item.IsImage, keepPart ? Choice.ClipInSeconds + Choice.LengthSeconds : null, keepPart);
            return;
        }
        if (item.DurationSec > 0.01)
        {
            Choice.SetMeme(item.FullPath, false, item.DurationSec, keepPart);
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

    private void ShowStatus(string text)
    {
        var status = this.FindControl<TextBlock>("StatusText");
        if (status == null) return;
        status.Text = text;
        status.IsVisible = true;
    }

    public static async Task<MemePickResult?> PickAsync(Window owner, IEnumerable<MemeItem> items,
        bool portrait = false, Action<IReadOnlyList<MemeItem>>? onLatest = null,
        FreeVideoStudio.Core.Media.MemePlacement? existing = null, MemePickerContext? context = null)
    {
        try
        {
            var dlg = new MemePickerWindow();
            dlg._context = context;
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
            dlg.UpdateLanding();
            await dlg.ShowDialog(owner);
            onLatest?.Invoke(dlg.LatestItems);
            return dlg.BuildResult();
        }
        catch (Exception ex)
        {
            RuntimeLog.Fail("MEME", $"Meme picker failed to open: {ex.Message}");
            return null;
        }
    }

    public static Task<MemePickResult?> PickForMainScreenAsync(Window owner, IEnumerable<MemeItem> items,
        bool portrait, string? currentPath, Action<IReadOnlyList<MemeItem>>? onLatest = null,
        FreeVideoStudio.Core.Media.MemePlacement? existing = null, MemePickerContext? context = null)
        => PickAsync(owner, items, portrait, onLatest, existing, context);
    private void UpdateLanding()
    {
        var landing = _context?.Locate(Choice);
        _landingOk = landing?.Ok ?? true;
        Choice.LandingText = landing?.Message ?? "";
        if (_useBtn != null) _useBtn.IsEnabled = _selected != null && _landingOk && Choice.LengthSeconds > 0;
    }
    private MemePickResult? BuildResult()
    {
        if (Result is not MemeItem picked) return null;
        var c = Choice;
        double? length = c.LengthSeconds > 0.01 ? c.LengthSeconds : c.DurationSec;
        return new MemePickResult(picked, length, c.Mode, c.Corner, c.Size, c.PlaySound,
            picked.IsImage ? 0 : c.ClipInSeconds, c.PosX, c.PosY, c.Animation, c.Transition,
            c.IsFullScreen ? c.MusicContinues : null, c.IsAtStart, _context?.Locate(c).SourceSeconds);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
