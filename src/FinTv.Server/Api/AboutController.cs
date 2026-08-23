using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using CliWrap;
using FinTv.Domain;
using FinTv.Services;
using FinTv.Streaming;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FinTv.Api;

[ApiController]
[Route("api/about")]
[Authorize(Policy = "admin")]
public class AboutController : ControllerBase
{
    private const string Homepage = "https://github.com/FlowMeadow01/ChannelFlow";
    private const string Author = "binarygeek119";
    private const string AuthorUrl = "https://github.com/binarygeek119";

    private readonly IWebHostEnvironment _env;
    private readonly FfmpegEncodingService _encoding;
    private readonly IFfmpegLocator _ffmpeg;
    private readonly StreamService _streams;

    public AboutController(
        IWebHostEnvironment env,
        FfmpegEncodingService encoding,
        IFfmpegLocator ffmpeg,
        StreamService streams)
    {
        _env = env;
        _encoding = encoding;
        _ffmpeg = ffmpeg;
        _streams = streams;
    }

    [HttpGet]
    public async Task<ActionResult<object>> Get(CancellationToken cancellationToken)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "1.0.0";
        var (version, build) = SplitVersion(informational);
        var envVersion = AppEnvironment.Get("VERSION");
        if (!string.IsNullOrWhiteSpace(envVersion))
        {
            version = envVersion.Trim();
        }

        var revision = AppEnvironment.Get("REVISION") ?? build;
        var docker = RunsInDocker();
        var process = Process.GetCurrentProcess();
        var uptime = DateTime.Now - process.StartTime;
        var encoding = _encoding.Describe();
        var saved = FinTvRuntime.Current?.Configuration.Transcode;
        var transcodeSource = HasSavedTranscode(saved) ? "saved" : "environment";
        var ffmpegVersion = await ReadFfmpegVersionAsync(_ffmpeg.EncoderPath, cancellationToken);
        var viewers = _streams.GetActiveStreams().Sum(item => item.ViewerCount);
        var gc = GC.GetGCMemoryInfo();

        return Ok(new
        {
            app = new
            {
                name = "ChannelFlow-Server",
                author = Author,
                authorUrl = AuthorUrl,
                version,
                informationalVersion = informational,
                revision = string.IsNullOrWhiteSpace(revision) ? null : revision,
                packaging = docker ? "docker" : "native",
                packagingLabel = docker ? "Docker" : "Non-Docker",
                image = AppEnvironment.Get("IMAGE"),
                homepage = Homepage,
                framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription
            },
            system = new
            {
                os = System.Runtime.InteropServices.RuntimeInformation.OSDescription.Trim(),
                architecture = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(),
                processArchitecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
                machineName = Environment.MachineName,
                environmentName = _env.EnvironmentName,
                processorCount = Environment.ProcessorCount,
                workingSet = FormatBytes(process.WorkingSet64),
                gcHeap = FormatBytes(gc.HeapSizeBytes),
                uptime = FormatUptime(uptime),
                timeZone = TimeZoneInfo.Local.Id,
                serverTime = DateTimeOffset.Now.ToString("yyyy-MM-dd h:mm:ss tt zzz", CultureInfo.InvariantCulture),
                utcTime = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture),
                configFolder = FinTvRuntime.Current?.DataFolder,
                contentRoot = _env.ContentRootPath,
                listenPort = Environment.GetEnvironmentVariable("PORT") ?? "8097",
                postgresHost = Environment.GetEnvironmentVariable("POSTGRES_HOST"),
                activeViewers = viewers
            },
            transcode = new
            {
                ffmpegPath = _ffmpeg.EncoderPath,
                ffmpegVersion,
                hardwareAcceleration = encoding.HardwareAcceleration,
                encoder = encoding.Encoder,
                vaapiDevice = encoding.VaapiDevice,
                vaapiDeviceExists = encoding.VaapiDeviceExists,
                useVaapi = encoding.UseVaapi,
                source = transcodeSource,
                environmentAcceleration = _encoding.EnvironmentHardwareAcceleration,
                environmentEncoder = _encoding.EnvironmentVideoEncoder
            }
        });
    }

    private static bool RunsInDocker()
    {
        if (string.Equals(Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"), "true", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(AppEnvironment.Get("PACKAGING"), "docker", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return System.IO.File.Exists("/.dockerenv");
    }

    private static bool HasSavedTranscode(FinTv.Configuration.TranscodeSettings? saved)
        => saved is not null
            && (!string.IsNullOrWhiteSpace(saved.HardwareAcceleration)
                || !string.IsNullOrWhiteSpace(saved.VideoEncoder)
                || !string.IsNullOrWhiteSpace(saved.VaapiDevice));

    private static (string Version, string? Build) SplitVersion(string informational)
    {
        var plus = informational.IndexOf('+');
        if (plus < 0)
        {
            return (informational, null);
        }

        return (informational[..plus], informational[(plus + 1)..]);
    }

    private static async Task<string> ReadFfmpegVersionAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(4));
            var stdout = new StringBuilder();
            await Cli.Wrap(path)
                .WithArguments("-version")
                .WithValidation(CommandResultValidation.None)
                .WithStandardOutputPipe(PipeTarget.ToStringBuilder(stdout))
                .WithStandardErrorPipe(PipeTarget.ToStringBuilder(new StringBuilder()))
                .ExecuteAsync(timeout.Token);
            var line = stdout.ToString()
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault();
            return string.IsNullOrWhiteSpace(line) ? "Unknown" : line.Trim();
        }
        catch (Exception)
        {
            return "Unavailable";
        }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes} B";
        }

        double value = bytes;
        var units = new[] { "KB", "MB", "GB", "TB" };
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value.ToString("0.#", CultureInfo.InvariantCulture)} {units[unit]}";
    }

    private static string FormatUptime(TimeSpan value)
    {
        if (value.TotalDays >= 1)
        {
            return $"{(int)value.TotalDays}d {value.Hours}h {value.Minutes}m";
        }

        if (value.TotalHours >= 1)
        {
            return $"{value.Hours}h {value.Minutes}m";
        }

        return $"{Math.Max(0, value.Minutes)}m {value.Seconds}s";
    }
}
