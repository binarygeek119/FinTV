using System.Net.Http.Headers;
using System.Text;
using CliWrap;
using FinTv.Domain;
using FinTv.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FinTv.News;

public sealed class NewsTtsService
{
    public const int MaxChunkChars = 180;
    public const int AiMaxChunkChars = 4000;
    private static readonly TimeSpan RateLimitBackoff = TimeSpan.FromMinutes(10);

    private readonly IHttpClientFactory _http;
    private readonly IFfmpegLocator _ffmpeg;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<NewsTtsService> _logger;
    private DateTimeOffset _rateLimitedUntil = DateTimeOffset.MinValue;

    public NewsTtsService(
        IHttpClientFactory http,
        IFfmpegLocator ffmpeg,
        IServiceScopeFactory scopes,
        ILogger<NewsTtsService> logger)
    {
        _http = http;
        _ffmpeg = ffmpeg;
        _scopes = scopes;
        _logger = logger;
    }

    public Task<string?> SynthesizeAsync(
        string script,
        string voice,
        string newsDir,
        CancellationToken cancellationToken)
        => SynthesizeAsync(script, voice, newsDir, "google", cancellationToken);

    public async Task<string?> SynthesizeAsync(
        string script,
        string voice,
        string newsDir,
        string? engine,
        CancellationToken cancellationToken)
    {
        if (IsAiEngine(engine))
        {
            try
            {
                var aiPath = await SynthesizeAiAsync(script, voice, newsDir, cancellationToken);
                if (!string.IsNullOrWhiteSpace(aiPath))
                {
                    return aiPath;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "AI news TTS failed; falling back to Google TTS");
            }
        }

        return await SynthesizeGoogleAsync(script, voice, newsDir, cancellationToken);
    }

    public static bool IsAiEngine(string? engine)
        => string.Equals(engine?.Trim(), "ai", StringComparison.OrdinalIgnoreCase);

    private async Task<string?> SynthesizeAiAsync(
        string script,
        string voice,
        string newsDir,
        CancellationToken cancellationToken)
    {
        var chunks = Chunk(script, AiMaxChunkChars);
        if (chunks.Count == 0)
        {
            return null;
        }

        Directory.CreateDirectory(newsDir);
        var output = Path.Combine(newsDir, "speech.mp3");
        var partDir = Path.Combine(newsDir, "tts-ai");
        Directory.CreateDirectory(partDir);
        var parts = new List<string>();

        using var scope = _scopes.CreateScope();
        var llm = scope.ServiceProvider.GetRequiredService<LlmClientService>();
        for (var i = 0; i < chunks.Count; i++)
        {
            var bytes = await llm.SynthesizeSpeechAsync(chunks[i], voice, cancellationToken);
            var path = Path.Combine(partDir, $"part-{i:00}.mp3");
            await File.WriteAllBytesAsync(path, bytes, cancellationToken);
            parts.Add(path);
        }

        return await ConcatPartsAsync(parts, partDir, output, cancellationToken);
    }

    private async Task<string?> SynthesizeGoogleAsync(
        string script,
        string voice,
        string newsDir,
        CancellationToken cancellationToken)
    {
        var chunks = Chunk(script);
        if (chunks.Count == 0)
        {
            return null;
        }

        Directory.CreateDirectory(newsDir);
        var output = Path.Combine(newsDir, "speech.mp3");
        if (DateTimeOffset.UtcNow < _rateLimitedUntil)
        {
            return File.Exists(output) ? output : null;
        }

        var partDir = Path.Combine(newsDir, "tts");
        Directory.CreateDirectory(partDir);
        var parts = new List<string>();
        var lang = ToGoogleLang(voice);

        try
        {
            var client = _http.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.UserAgent.Clear();
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (ChannelFlow news TTS)");
            client.DefaultRequestHeaders.Referrer = new Uri("https://translate.google.com/");
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("audio/mpeg"));

            for (var i = 0; i < chunks.Count; i++)
            {
                if (i > 0)
                {
                    await Task.Delay(250, cancellationToken);
                }

                var encoded = Uri.EscapeDataString(chunks[i]);
                var url = $"https://translate.google.com/translate_tts?ie=UTF-8&q={encoded}&tl={Uri.EscapeDataString(lang)}&client=tw-ob";
                var path = Path.Combine(partDir, $"part-{i:00}.mp3");
                using var response = await client.GetAsync(url, cancellationToken);
                if ((int)response.StatusCode == 429)
                {
                    _rateLimitedUntil = DateTimeOffset.UtcNow.Add(RateLimitBackoff);
                    _logger.LogWarning("News TTS rate-limited; backing off for {Minutes} minutes", RateLimitBackoff.TotalMinutes);
                    return File.Exists(output) ? output : null;
                }

                response.EnsureSuccessStatusCode();
                var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                if (bytes.Length < 64)
                {
                    throw new InvalidOperationException("TTS returned an empty clip.");
                }

                await File.WriteAllBytesAsync(path, bytes, cancellationToken);
                parts.Add(path);
            }

            return await ConcatPartsAsync(parts, partDir, output, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "News TTS failed");
            return File.Exists(output) ? output : null;
        }
    }

    private async Task<string?> ConcatPartsAsync(
        IReadOnlyList<string> parts,
        string partDir,
        string output,
        CancellationToken cancellationToken)
    {
        if (parts.Count == 0)
        {
            return File.Exists(output) ? output : null;
        }

        if (parts.Count == 1)
        {
            File.Copy(parts[0], output, overwrite: true);
            return output;
        }

        var listPath = Path.Combine(partDir, "concat.txt");
        var list = new StringBuilder();
        foreach (var part in parts)
        {
            list.Append("file '").Append(Path.GetFileName(part).Replace("'", @"'\''")).AppendLine("'");
        }

        await File.WriteAllTextAsync(listPath, list.ToString(), cancellationToken);
        await Cli.Wrap(_ffmpeg.EncoderPath)
            .WithWorkingDirectory(partDir)
            .WithArguments([
                "-hide_banner", "-loglevel", "error",
                "-f", "concat", "-safe", "0", "-i", "concat.txt",
                "-c:a", "libmp3lame", "-b:a", "64k", "-y", output
            ])
            .WithValidation(CommandResultValidation.ZeroExitCode)
            .ExecuteAsync(cancellationToken);

        return File.Exists(output) ? output : null;
    }

    public async Task<double> ProbeDurationSecondsAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            var stdout = new StringBuilder();
            var probe = ResolveFfprobe();
            await Cli.Wrap(probe)
                .WithArguments([
                    "-v", "error",
                    "-show_entries", "format=duration",
                    "-of", "default=noprint_wrappers=1:nokey=1",
                    path
                ])
                .WithStandardOutputPipe(PipeTarget.ToStringBuilder(stdout))
                .WithValidation(CommandResultValidation.None)
                .ExecuteAsync(cancellationToken);

            return double.TryParse(stdout.ToString().Trim(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var seconds)
                ? seconds
                : 0;
        }
        catch
        {
            return 0;
        }
    }

    public static IReadOnlyList<string> Chunk(string script, int maxChars = MaxChunkChars)
    {
        var text = script.Replace('\n', ' ').Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var chunks = new List<string>();
        var remaining = text;
        while (remaining.Length > 0)
        {
            if (remaining.Length <= maxChars)
            {
                chunks.Add(remaining.Trim());
                break;
            }

            var window = remaining[..maxChars];
            var split = Math.Max(window.LastIndexOf(". ", StringComparison.Ordinal), window.LastIndexOf(' '));
            if (split < 20)
            {
                split = maxChars;
            }

            chunks.Add(remaining[..split].Trim());
            remaining = remaining[split..].TrimStart('.', ' ');
        }

        return chunks.Where(c => c.Length > 0).ToList();
    }

    private string ResolveFfprobe()
    {
        var dir = Path.GetDirectoryName(_ffmpeg.EncoderPath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            var sibling = Path.Combine(dir, OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe");
            if (File.Exists(sibling))
            {
                return sibling;
            }
        }

        return "ffprobe";
    }

    private static string ToGoogleLang(string voice)
    {
        if (string.IsNullOrWhiteSpace(voice))
        {
            return "en";
        }

        var trimmed = voice.Trim();
        return trimmed.Contains('-', StringComparison.Ordinal) ? trimmed.Split('-')[0].ToLowerInvariant() : trimmed.ToLowerInvariant();
    }
}
