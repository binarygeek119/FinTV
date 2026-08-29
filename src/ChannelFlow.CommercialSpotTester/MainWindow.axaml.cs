using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using ChannelFlow.CommercialDetect;

namespace ChannelFlow.CommercialSpotTester;

public partial class MainWindow : Window
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly CommercialSpotDetector _detector = new();
    private readonly ObservableCollection<SpotRow> _rows = [];
    private bool _busy;

    public MainWindow()
    {
        InitializeComponent();
        ResultsGrid.ItemsSource = _rows;
        ApplySettings(new CommercialBreakScanSettings());
        StatusText.Text = "Pick a video, then Scan.";
    }

    private async void OnBrowse(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Pick a video",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Video")
                {
                    Patterns = ["*.mkv", "*.mp4", "*.m4v", "*.avi", "*.ts", "*.m2ts", "*.webm", "*.mov"]
                },
                FilePickerFileTypes.All
            ]
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path)
        {
            VideoPathBox.Text = path;
        }
    }

    private async void OnLoadSettings(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Load commercial-break settings",
            AllowMultiple = false,
            FileTypeFilter = [JsonFileType()]
        });
        if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path)
        {
            return;
        }

        try
        {
            var json = await File.ReadAllTextAsync(path);
            var settings = JsonSerializer.Deserialize<CommercialBreakScanSettings>(json, JsonOptions)
                ?? new CommercialBreakScanSettings();
            ApplySettings(settings);
            StatusText.Text = "Loaded " + Path.GetFileName(path);
        }
        catch (Exception ex)
        {
            StatusText.Text = "Could not load settings: " + ex.Message;
        }
    }

    private async void OnSaveSettings(object? sender, RoutedEventArgs e)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save commercial-break settings",
            SuggestedFileName = "commercial-break-settings.json",
            FileTypeChoices = [JsonFileType()]
        });
        if (file?.TryGetLocalPath() is not { } path)
        {
            return;
        }

        try
        {
            var json = JsonSerializer.Serialize(ReadSettings(), JsonOptions);
            await File.WriteAllTextAsync(path, json);
            StatusText.Text = "Saved " + Path.GetFileName(path);
        }
        catch (Exception ex)
        {
            StatusText.Text = "Could not save settings: " + ex.Message;
        }
    }

    private async void OnScan(object? sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        var video = (VideoPathBox.Text ?? "").Trim();
        if (string.IsNullOrWhiteSpace(video) || !File.Exists(video))
        {
            StatusText.Text = "Choose a video file that exists on this machine.";
            return;
        }

        var ffmpeg = FindOnPath("ffmpeg") ?? FindOnPath("ffmpeg.exe");
        var ffprobe = FindOnPath("ffprobe") ?? FindOnPath("ffprobe.exe");
        if (ffmpeg is null || ffprobe is null)
        {
            StatusText.Text = "ffmpeg and ffprobe must be on PATH.";
            return;
        }

        _busy = true;
        RunButton.IsEnabled = false;
        StatusText.Text = "Scanning (one ffmpeg pass)…";
        _rows.Clear();
        WindowsText.Text = "";
        try
        {
            var settings = ReadSettings();
            var result = await _detector.DetectAsync(video, ffmpeg, ffprobe, settings);
            WindowsText.Text = Describe(result);
            foreach (var candidate in result.Candidates.OrderBy(row => row.AtSeconds))
            {
                _rows.Add(new SpotRow
                {
                    Name = candidate.Accepted ? candidate.Name : "",
                    At = TimeFormat.Seconds(candidate.AtSeconds),
                    Black = TimeFormat.Range(candidate.Black),
                    Silence = TimeFormat.Range(candidate.Silence),
                    Confidence = candidate.Confidence.ToString("0.0", CultureInfo.InvariantCulture) + "%",
                    Window = candidate.InEligibleWindow ? "eligible" : "skipped",
                    Result = candidate.Accepted ? "pass" : "fail"
                });
            }

            StatusText.Text = result.Disposition == FileDisposition.SkipFile
                ? "File skipped: it has chapters that are not introskip / break names."
                : "Accepted " + result.Accepted.Count + " of " + result.Candidates.Count + " candidate(s).";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Scan failed: " + ex.Message;
        }
        finally
        {
            _busy = false;
            RunButton.IsEnabled = true;
        }
    }

    private CommercialBreakScanSettings ReadSettings()
    {
        var settings = new CommercialBreakScanSettings
        {
            SilenceDb = SilenceDb.Value is decimal db ? (double)db : -40,
            SilenceMinSeconds = SilenceMin.Value is decimal silence ? (double)silence : 0.3,
            BlackPixThreshold = BlackPix.Value is decimal pix ? (double)pix : 0.10,
            BlackPictureRatio = BlackRatio.Value is decimal ratio ? (double)ratio : 0.95,
            BlackMinFrames = BlackFrames.Value is decimal frames ? (int)frames : 6,
            ConfidencePercent = Confidence.Value is decimal confidence ? (int)confidence : 70
        };
        settings.Clamp();
        return settings;
    }

    private void ApplySettings(CommercialBreakScanSettings settings)
    {
        settings.Clamp();
        SilenceDb.Value = (decimal)settings.SilenceDb;
        SilenceMin.Value = (decimal)settings.SilenceMinSeconds;
        BlackPix.Value = (decimal)settings.BlackPixThreshold;
        BlackRatio.Value = (decimal)settings.BlackPictureRatio;
        BlackFrames.Value = settings.BlackMinFrames;
        Confidence.Value = settings.ConfidencePercent;
    }

    private static string Describe(DetectResult result)
    {
        if (result.Disposition == FileDisposition.SkipFile)
        {
            var names = string.Join(", ", result.Probe.Chapters.Select(chapter => chapter.Name).Where(name => !string.IsNullOrWhiteSpace(name)));
            return "Skipped file. Existing chapters: " + (string.IsNullOrWhiteSpace(names) ? "(unnamed)" : names);
        }

        if (result.EligibleWindows.Count == 0)
        {
            return result.Disposition == FileDisposition.NoChapters
                ? "No existing chapters. Whole file is eligible (small head/tail pad)."
                : "No eligible windows between introskip chapters.";
        }

        var windows = string.Join("  ·  ", result.EligibleWindows.Select(TimeFormat.Range));
        var prefix = result.Disposition == FileDisposition.NoChapters
            ? "No chapters. Eligible after pad: "
            : "Eligible windows (after intro/preview/recap, before outro, skipping introskip ranges): ";
        return prefix + windows;
    }

    private static FilePickerFileType JsonFileType()
        => new("JSON") { Patterns = ["*.json"] };

    private static string? FindOnPath(string fileName)
    {
        var env = Environment.GetEnvironmentVariable(fileName.StartsWith("ffprobe", StringComparison.OrdinalIgnoreCase)
            ? "FFPROBE_PATH"
            : "FFMPEG_PATH");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env))
        {
            return env;
        }

        var paths = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator);
        foreach (var dir in paths)
        {
            if (string.IsNullOrWhiteSpace(dir))
            {
                continue;
            }

            var candidate = Path.Combine(dir, fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return File.Exists(fileName) ? Path.GetFullPath(fileName) : null;
    }
}

public sealed class SpotRow
{
    public string Name { get; set; } = "";

    public string At { get; set; } = "";

    public string Black { get; set; } = "";

    public string Silence { get; set; } = "";

    public string Confidence { get; set; } = "";

    public string Window { get; set; } = "";

    public string Result { get; set; } = "";
}
