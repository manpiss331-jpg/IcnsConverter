using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using ImageMagick;

namespace IcnsConverter;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<IcnsFile> _files = new();
    private readonly Dictionary<int, ToggleButton> _sizeChecks = new();
    private string[] _startupPaths = Array.Empty<string>();

    public MainWindow()
    {
        InitializeComponent();

        FileList.ItemsSource = _files;
        FormatBox.ItemsSource = new[] { "PNG", "JPG", "WEBP", "ICO", "BMP", "TIFF", "GIF" };
        FormatBox.SelectedIndex = 0;
        BgBox.ItemsSource = new[] { "White", "Black", "Gray" };
        BgBox.SelectedIndex = 0;
        MissingBox.ItemsSource = new[]
        {
            "Skip that size",
            "Use the largest size available",
            "Upscale the largest size"
        };
        MissingBox.SelectedIndex = 0;

        FormatBox.SelectionChanged += (_, _) => UpdateOptionVisibility();
        QualitySlider.PropertyChanged += (_, e) =>
        {
            if (e.Property == RangeBase.ValueProperty)
                QualityLabel.Text = ((int)QualitySlider.Value).ToString();
        };
        ThemeSwitch.IsCheckedChanged += (_, _) => ApplyTheme(ThemeSwitch.IsChecked == true);
        Opened += (_, _) => ApplyNativeTitleBarTheme(ThemeSwitch.IsChecked == true);

        DropZone.AddHandler(DragDrop.DragOverEvent, OnDragOver);
        DropZone.AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        DropZone.AddHandler(DragDrop.DropEvent, OnDrop);

        UpdateOptionVisibility();
        RebuildSizes();
        Log("Ready. Add ICNS files to begin.");

        var startupPaths = Environment.GetCommandLineArgs().Skip(1)
            .Where(path => !string.IsNullOrWhiteSpace(path) && !path.StartsWith("-", StringComparison.Ordinal))
            .ToArray();
        _startupPaths = startupPaths;
    }

    // ---------- Theme switching ----------
    private void ApplyTheme(bool dark)
    {
        if (Application.Current is { } app)
            app.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
        ApplyNativeTitleBarTheme(dark);
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        ApplyNativeTitleBarTheme(ThemeSwitch.IsChecked == true);
        Focus();

        if (_startupPaths.Length > 0)
        {
            var paths = _startupPaths;
            _startupPaths = Array.Empty<string>();
            Log($"Opening {paths.Length} startup path{(paths.Length == 1 ? "" : "s")}…");
            _ = AddPaths(paths);
        }
    }

    /// <summary>Keeps the native Windows title bar in sync with the app theme.</summary>
    private void ApplyNativeTitleBarTheme(bool dark)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var handle = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            if (handle == IntPtr.Zero) return;

            int value = dark ? 1 : 0;
            // 20 is the modern attribute; 19 is the pre-20H1 fallback. Set both
            // so every Windows build picks up the change.
            DwmSetWindowAttribute(handle, 20, ref value, 4);
            DwmSetWindowAttribute(handle, 19, ref value, 4);

            // Windows 11 lets us paint the caption to match the app exactly.
            // These calls are harmless no-ops on older Windows versions.
            int caption = dark ? 0x00201612 : 0x00FFFFFF;
            int text = dark ? 0x00FAF5F2 : 0x00291A13;
            int border = dark ? 0x00382A23 : 0x00F0E7E2;
            DwmSetWindowAttribute(handle, 35, ref caption, 4);
            DwmSetWindowAttribute(handle, 36, ref text, 4);
            DwmSetWindowAttribute(handle, 34, ref border, 4);

            SetWindowPos(handle, IntPtr.Zero, 0, 0, 0, 0,
                SwpNoMove | SwpNoSize | SwpNoZOrder | SwpFrameChanged);
        }
        catch
        {
            // Title-bar theming is cosmetic and platform dependent.
        }
    }

    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpFrameChanged = 0x0020;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    // ---------- Option visibility ----------
    private void UpdateOptionVisibility()
    {
        var format = (OutFormat)Math.Max(0, FormatBox.SelectedIndex);
        QualityPanel.IsVisible = format is OutFormat.Jpg or OutFormat.Webp;
        BgPanel.IsVisible = format == OutFormat.Jpg;
        IcoPanel.IsVisible = format == OutFormat.Ico;

        FormatHint.Text = format switch
        {
            OutFormat.Png => "Lossless output with transparency preserved.",
            OutFormat.Jpg => "Smaller files; transparency is filled with the chosen background.",
            OutFormat.Webp => "Modern compression with transparency support.",
            OutFormat.Ico => "Windows icon format; sizes up to 256 px.",
            OutFormat.Bmp => "Uncompressed bitmap.",
            OutFormat.Tiff => "High-quality archival image.",
            OutFormat.Gif => "256-colour image, one file per size.",
            _ => ""
        };
    }

    private void ModeChanged(object? sender, RoutedEventArgs e)
    {
        bool custom = SelectedRadio.IsChecked == true;
        SizesPanel.IsEnabled = custom;
        SizesPanel.IsVisible = custom;
        MissingPanel.IsEnabled = custom;
        MissingPanel.IsVisible = custom;
        SizesHint.IsVisible = custom && _sizeChecks.Count == 0;
    }

    // ---------- Adding files ----------
    private async void AddFiles_Click(object? sender, RoutedEventArgs e)
    {
        var picked = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose ICNS files",
            AllowMultiple = true,
            FileTypeFilter = new[] { new FilePickerFileType("ICNS icons") { Patterns = new[] { "*.icns" } } }
        });
        await AddPaths(picked.Select(f => f.TryGetLocalPath()).OfType<string>());
    }

    private async void AddFolder_Click(object? sender, RoutedEventArgs e)
    {
        var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose a folder with ICNS files",
            AllowMultiple = false
        });
        await AddPaths(picked.Select(f => f.TryGetLocalPath()).OfType<string>());
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        bool accepted = e.DataTransfer.Formats.Contains(DataFormat.File);
        e.DragEffects = accepted ? DragDropEffects.Copy : DragDropEffects.None;
        SetDragHighlight(accepted);
        e.Handled = true;
    }

    private void OnDragLeave(object? sender, DragEventArgs e)
    {
        SetDragHighlight(false);
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        SetDragHighlight(false);
        if (!e.DataTransfer.Formats.Contains(DataFormat.File)) return;

        var items = e.DataTransfer.TryGetFiles();
        if (items is null) return;
        var paths = items.Select(i => i.TryGetLocalPath()).OfType<string>().ToList();
        await AddPaths(paths);
    }

    private void SetDragHighlight(bool on)
    {
        if (on)
        {
            if (!DropZone.Classes.Contains("drag")) DropZone.Classes.Add("drag");
        }
        else
        {
            DropZone.Classes.Remove("drag");
        }
    }

    private async Task AddPaths(IEnumerable<string> paths)
    {
        var list = paths.ToList();
        var known = new HashSet<string>(_files.Select(f => f.FilePath), StringComparer.OrdinalIgnoreCase);

        List<IcnsFile> loaded;
        try
        {
            loaded = await Task.Run(() =>
            {
                var result = new List<IcnsFile>();
                foreach (var p in list)
                {
                    IEnumerable<string> found = Directory.Exists(p)
                        ? Directory.EnumerateFiles(p, "*.icns", SearchOption.AllDirectories)
                        : (p.EndsWith(".icns", StringComparison.OrdinalIgnoreCase) ? new[] { p } : Array.Empty<string>());

                    foreach (var path in found)
                        if (known.Add(path)) result.Add(IcnsFile.Load(path));
                }
                return result;
            });
        }
        catch (Exception ex)
        {
            Log($"[x] Could not read the selected path: {ex.Message}");
            return;
        }

        foreach (var file in loaded)
        {
            _files.Add(file);
            if (!file.IsReadable)
                Log($"[x] {Path.GetFileName(file.FilePath)}: {file.Error ?? "no images found"}");
        }

        if (loaded.Count > 0)
            Log($"Added {loaded.Count} file{(loaded.Count == 1 ? "" : "s")}.");

        RebuildSizes();
    }

    private void RemoveSelected_Click(object? sender, RoutedEventArgs e)
    {
        foreach (var file in FileList.SelectedItems?.OfType<IcnsFile>().ToList() ?? new List<IcnsFile>())
            _files.Remove(file);
        RebuildSizes();
    }

    private void Clear_Click(object? sender, RoutedEventArgs e)
    {
        _files.Clear();
        RebuildSizes();
    }

    // ---------- Size chips + status ----------
    private void RebuildSizes()
    {
        var previous = _sizeChecks.Where(kv => kv.Value.IsChecked == true).Select(kv => kv.Key).ToHashSet();
        var all = _files.SelectMany(f => f.Images.Select(i => i.Size))
                        .Distinct().OrderByDescending(s => s).ToList();

        SizesPanel.Children.Clear();
        _sizeChecks.Clear();
        foreach (int size in all)
        {
            var chip = new ToggleButton
            {
                Content = $"{size}px",
                IsChecked = previous.Contains(size) || (previous.Count == 0 && size == all[0])
            };
            chip.Classes.Add("chip");
            _sizeChecks[size] = chip;
            SizesPanel.Children.Add(chip);
        }

        bool custom = SelectedRadio.IsChecked == true;
        SizesPanel.IsVisible = custom;
        MissingPanel.IsVisible = custom;
        SizesHint.IsVisible = custom && all.Count == 0;
        EmptyHint.IsVisible = _files.Count == 0;
        int imageCount = _files.Sum(f => f.Images.Count);
        FileCountBadge.Text = _files.Count == 1 ? "1 file" : $"{_files.Count} files";
        SelectionSummary.Text = _files.Count == 0
            ? "No files loaded"
            : $"{imageCount} image{(imageCount == 1 ? "" : "s")} across {_files.Count} file{(_files.Count == 1 ? "" : "s")}";
        ConvertHint.Text = _files.Count == 0
            ? "Nothing to convert"
            : $"{imageCount} image{(imageCount == 1 ? "" : "s")} ready";
        ConvertLabel.Text = _files.Count == 0
            ? "Convert"
            : $"Convert {_files.Count} file{(_files.Count == 1 ? "" : "s")}";
        ConvertButton.IsEnabled = _files.Count > 0;
    }

    // ---------- Output folder ----------
    private async void BrowseOut_Click(object? sender, RoutedEventArgs e)
    {
        var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose output folder",
            AllowMultiple = false
        });
        var path = picked.Select(f => f.TryGetLocalPath()).OfType<string>().FirstOrDefault();
        if (path is not null) OutBox.Text = path;
    }

    // ---------- Convert ----------
    private async void Convert_Click(object? sender, RoutedEventArgs e)
    {
        if (_files.Count == 0)
        {
            Log("Add some ICNS files first.");
            return;
        }

        var mode = AllRadio.IsChecked == true ? SizeMode.All
                 : SelectedRadio.IsChecked == true ? SizeMode.Selected
                 : SizeMode.Largest;
        var sizes = _sizeChecks.Where(kv => kv.Value.IsChecked == true).Select(kv => kv.Key).ToHashSet();
        if (mode == SizeMode.Selected && sizes.Count == 0)
        {
            Log("Tick at least one size.");
            return;
        }

        MagickColor background = BgBox.SelectedIndex switch
        {
            1 => MagickColors.Black,
            2 => MagickColors.Gray,
            _ => MagickColors.White
        };

        var options = new ExportOptions(
            (OutFormat)Math.Max(0, FormatBox.SelectedIndex),
            mode,
            sizes,
            (MissingMode)Math.Max(0, MissingBox.SelectedIndex),
            string.IsNullOrWhiteSpace(OutBox.Text) ? null : OutBox.Text.Trim(),
            (int)QualitySlider.Value,
            background,
            CombineIcoBox.IsChecked == true);

        var files = _files.ToList();
        ConvertButton.IsEnabled = false;
        ConvertLabel.Text = "Converting...";
        ConvertHint.Text = "Working…";
        Log($"Converting {files.Count} file{(files.Count == 1 ? "" : "s")}...");

        int created = await Task.Run(() =>
        {
            int count = 0;
            foreach (var file in files)
                count += Exporter.Export(file, options, message => Dispatcher.UIThread.Post(() => Log(message)));
            return count;
        });

        Log($"Done. {created} file{(created == 1 ? "" : "s")} created.");
        RebuildSizes();
        ConvertHint.Text = $"{created} file{(created == 1 ? "" : "s")} created";
    }

    // ---------- Activity log ----------
    private void Log(string line)
    {
        LogBox.Text = (LogBox.Text ?? "") + line + Environment.NewLine;
        LogBox.CaretIndex = LogBox.Text.Length;
    }

    private void ClearLog_Click(object? sender, RoutedEventArgs e)
    {
        LogBox.Text = "";
    }

    private async void CopyLog_Click(object? sender, RoutedEventArgs e)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is not null)
            await clipboard.SetTextAsync(LogBox.Text ?? "");
    }
}
