using Jellyfin.Plugin.FinTV.Domain;
using Jellyfin.Plugin.FinTV.Streaming;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;

namespace Jellyfin.Plugin.FinTV.Services;

public class WeatherStarChannelService
{
    private readonly ILogger<WeatherStarChannelService> _logger;
    private readonly FfmpegCommandBuilder _ffmpegBuilder;

    public WeatherStarChannelService(ILogger<WeatherStarChannelService> logger, FfmpegCommandBuilder ffmpegBuilder)
    {
        _logger = logger;
        _ffmpegBuilder = ffmpegBuilder;
    }

    public async Task StreamAsync(Channel channel, Stream output, CancellationToken cancellationToken)
    {
        var lat = channel.WeatherLatitude ?? 40.7128;
        var lon = channel.WeatherLongitude ?? -74.0060;
        var plugin = Plugin.Instance ?? throw new InvalidOperationException("Plugin not initialized.");
        Directory.CreateDirectory(plugin.WeatherStarFolder);

        var weatherUrl = await EnsureWeatherStarAsync(plugin.WeatherStarFolder, lat, lon, cancellationToken);

        try
        {
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
            var page = await browser.NewPageAsync(new BrowserNewPageOptions
            {
                ViewportSize = new ViewportSize { Width = 640, Height = 480 }
            });

            await page.GotoAsync(weatherUrl, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 60000 });
            await page.WaitForTimeoutAsync(5000);

            var ffmpegPath = GetFfmpegPath();
            var args = BuildWeatherCaptureCommand(weatherUrl, channel);
            await RunCaptureLoopAsync(ffmpegPath, args, output, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "WeatherStar capture failed, using offline slate");
            var ffmpegPath = GetFfmpegPath();
            var offline = _ffmpegBuilder.BuildOfflineSlateCommand(channel);
            await CliWrap.Cli.Wrap(ffmpegPath)
                .WithArguments(offline)
                .WithStandardOutputPipe(CliWrap.PipeTarget.ToStream(output))
                .WithValidation(CliWrap.CommandResultValidation.None)
                .ExecuteAsync(cancellationToken);
        }
    }

    private static async Task<string> EnsureWeatherStarAsync(string folder, double lat, double lon, CancellationToken cancellationToken)
    {
        var indexPath = Path.Combine(folder, "index.html");
        if (!File.Exists(indexPath))
        {
            var template = $$"""
<!DOCTYPE html>
<html>
<head><meta charset="utf-8"><title>WeatherStar</title></head>
<body style="margin:0;background:#000">
<iframe src="https://weather.jmthornton.net?lat={{lat}}&lon={{lon}}" style="border:0;width:640px;height:480px"></iframe>
</body>
</html>
""";
            await File.WriteAllTextAsync(indexPath, template, cancellationToken);
        }

        return new Uri(indexPath).AbsoluteUri;
    }

    private static IReadOnlyList<string> BuildWeatherCaptureCommand(string pageUrl, Channel channel)
    {
        var (width, height) = channel.AspectRatio == AspectRatioMode.FourThree ? (640, 480) : (854, 480);
        return new List<string>
        {
            "-hide_banner",
            "-f", "lavfi",
            "-i", $"color=c=black:s={width}x{height}:r=30",
            "-t", "3600",
            "-vf", $"drawtext=text='WeatherStar 4000':fontcolor=cyan:fontsize=28:x=(w-text_w)/2:y=(h-text_h)/2",
            "-c:v", "libx264",
            "-preset", "veryfast",
            "-f", "mpegts",
            "pipe:1"
        };
    }

    private static string GetFfmpegPath()
    {
        return OperatingSystem.IsWindows() ? "ffmpeg" : "ffmpeg";
    }

    private static async Task RunCaptureLoopAsync(string ffmpegPath, IReadOnlyList<string> args, Stream output, CancellationToken cancellationToken)
    {
        await CliWrap.Cli.Wrap(ffmpegPath)
            .WithArguments(args)
            .WithStandardOutputPipe(CliWrap.PipeTarget.ToStream(output))
            .WithValidation(CliWrap.CommandResultValidation.None)
            .ExecuteAsync(cancellationToken);
    }
}
