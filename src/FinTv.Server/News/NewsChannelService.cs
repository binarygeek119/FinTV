using System.Globalization;
using CliWrap;
using FinTv.Data;
using FinTv.Domain;
using FinTv.Services;
using FinTv.Streaming;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FinTv.News;

public sealed class NewsChannelService
{
    private readonly FinTvDbContext _db;
    private readonly IFfmpegLocator _ffmpegLocator;
    private readonly FfmpegEncodingService _encoding;
    private readonly EbsService _ebs;
    private readonly JellyfinCatalogService _catalog;
    private readonly NewsHeadlineService _headlines;
    private readonly NewsTtsService _tts;
    private readonly NewsShowWriter _writer;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<NewsChannelService> _logger;

    public NewsChannelService(
        FinTvDbContext db,
        IFfmpegLocator ffmpegLocator,
        FfmpegEncodingService encoding,
        EbsService ebs,
        JellyfinCatalogService catalog,
        NewsHeadlineService headlines,
        NewsTtsService tts,
        NewsShowWriter writer,
        IHttpClientFactory http,
        ILogger<NewsChannelService> logger)
    {
        _db = db;
        _ffmpegLocator = ffmpegLocator;
        _encoding = encoding;
        _ebs = ebs;
        _catalog = catalog;
        _headlines = headlines;
        _tts = tts;
        _writer = writer;
        _http = http;
        _logger = logger;
    }

    public async Task StreamAsync(Channel channel, Stream output, CancellationToken cancellationToken)
    {
        var settings = await _db.NewsSettings.AsNoTracking().FirstOrDefaultAsync(cancellationToken) ?? new NewsSettings();
        var header = string.IsNullOrWhiteSpace(settings.HeaderText) ? "FlowWire News" : settings.HeaderText.Trim();
        var newsDir = FinTvRuntime.Current.NewsFolder;
        Directory.CreateDirectory(newsDir);
        var (width, height) = channel.AspectRatio == AspectRatioMode.FourThree ? (640, 480) : (1280, 720);
        var presentation = await BuildPresentationAsync(
            settings,
            header,
            await _headlines.GetAsync(force: false, cancellationToken),
            newsDir,
            width,
            height,
            channel,
            cancellationToken);

        var args = BuildAssEncodeArgs(width, height, presentation);
        AppendMux(args, presentation.Timeline.TotalSeconds, mpegts: true, filePath: null);
        var result = await RunFfmpegAsync(args, output, cancellationToken);
        if (result != 0 && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("News ffmpeg with ASS overlay exited {Code}; using drawtext fallback", result);
            var fallback = BuildDrawtextArgs(width, height, presentation);
            AppendMux(fallback, presentation.Timeline.TotalSeconds, mpegts: true, filePath: null);
            await RunFfmpegAsync(fallback, output, cancellationToken);
        }
    }

    public async Task<bool> RenderBulletinFileAsync(
        NewsSettings settings,
        IReadOnlyList<NewsArticle> articles,
        string header,
        string workDir,
        string outputMp4,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(workDir);
        Directory.CreateDirectory(Path.GetDirectoryName(outputMp4)!);
        const int width = 1280;
        const int height = 720;
        var presentation = await BuildPresentationAsync(
            settings,
            header,
            articles,
            workDir,
            width,
            height,
            channel: null,
            cancellationToken);

        var args = BuildAssEncodeArgs(width, height, presentation);
        AppendMux(args, presentation.Timeline.TotalSeconds, mpegts: false, filePath: outputMp4);
        var exit = await RunFfmpegAsync(args, output: null, cancellationToken);
        if (exit != 0 && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("News bulletin ASS encode exited {Code}; using drawtext fallback", exit);
            var fallback = BuildDrawtextArgs(width, height, presentation);
            AppendMux(fallback, presentation.Timeline.TotalSeconds, mpegts: false, filePath: outputMp4);
            exit = await RunFfmpegAsync(fallback, output: null, cancellationToken);
        }

        return exit == 0 && File.Exists(outputMp4) && new FileInfo(outputMp4).Length > 1024;
    }

    private async Task<NewsPresentation> BuildPresentationAsync(
        NewsSettings settings,
        string header,
        IReadOnlyList<NewsArticle> incoming,
        string workDir,
        int width,
        int height,
        Channel? channel,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(workDir);
        var show = await PrepareShowAsync(header, incoming, settings, cancellationToken);
        var articles = show.Stories;

        string? speechPath = null;
        if (settings.TtsEnabled && articles.Count > 0)
        {
            var script = BuildScript(header, articles, settings, show);
            speechPath = await _tts.SynthesizeAsync(script, settings.Voice, workDir, settings.TtsEngine, cancellationToken);
        }

        var speechWindow = DurationForSpeech(0, settings);
        if (HasAudioFile(speechPath))
        {
            var speechSeconds = await _tts.ProbeDurationSecondsAsync(speechPath!, cancellationToken);
            speechWindow = DurationForSpeech(speechSeconds, settings);
        }

        var musicPath = ResolveNewsMusicPath(settings);
        var intro = await ResolveBumperAsync("intro", musicPath, fromStart: true, cancellationToken);
        var outro = await ResolveBumperAsync("outro", musicPath, fromStart: false, cancellationToken);
        var timeline = new NewsTimeline(
            intro,
            speechWindow,
            outro,
            musicPath,
            HasAudioFile(speechPath) ? speechPath : null,
            await ResolveNewsLogoPathAsync(channel, cancellationToken));

        var imageFiles = await DownloadArticleImagesAsync(articles, workDir, cancellationToken);
        var beats = BuildSpokenBeats(header, articles, settings, imageFiles, speechWindow, show, timeline.IntroSeconds);
        var imageWindows = ImageWindows(beats);
        var assPath = Path.Combine(workDir, "news.ass");
        await File.WriteAllTextAsync(
            assPath,
            NewsAssBuilder.BuildSpoken(
                width,
                height,
                beats,
                settings.AiRewrite ? NewsShowWriter.AnchorName : null,
                timeline.IntroSeconds,
                timeline.OutroStart),
            cancellationToken);

        return new NewsPresentation(header, articles, beats, imageWindows, assPath, timeline, workDir);
    }

    private List<string> BuildAssEncodeArgs(int width, int height, NewsPresentation presentation)
    {
        var assFilter = NewsAssBuilder.EscapeAssFilterPath(presentation.AssPath);
        return BuildEncodeArgs(
            width,
            height,
            presentation,
            $"ass='{assFilter}'");
    }

    private List<string> BuildEncodeArgs(
        int width,
        int height,
        NewsPresentation presentation,
        string overlayFilter)
    {
        var timeline = presentation.Timeline;
        var args = new List<string>
        {
            "-hide_banner", "-loglevel", "warning", "-y"
        };
        args.AddRange(_encoding.HardwareDeviceArgs);
        args.AddRange(["-f", "lavfi", "-i", $"color=c=0x101010:s={width}x{height}:r=30"]);
        var nextIndex = 1;
        foreach (var image in presentation.ImageWindows)
        {
            args.AddRange(["-loop", "1", "-framerate", "30", "-i", image.Path]);
            nextIndex++;
        }

        int? logoIndex = null;
        if (timeline.ShowLogo)
        {
            args.AddRange(["-loop", "1", "-framerate", "30", "-i", timeline.LogoPath!]);
            logoIndex = nextIndex++;
        }

        int? bedIndex = null;
        var bedIsSilence = false;
        if (HasAudioFile(timeline.MusicPath) || timeline.SpeechPath is null)
        {
            bedIndex = nextIndex++;
            if (HasAudioFile(timeline.MusicPath))
            {
                args.AddRange(["-stream_loop", "-1", "-i", timeline.MusicPath!]);
            }
            else
            {
                bedIsSilence = true;
                args.AddRange(["-f", "lavfi", "-i", "anullsrc=r=48000:cl=stereo"]);
            }
        }

        int? speechIndex = null;
        if (HasAudioFile(timeline.SpeechPath))
        {
            args.AddRange(["-i", timeline.SpeechPath!]);
            speechIndex = nextIndex++;
        }

        int? introIndex = null;
        if (timeline.Intro is not null)
        {
            args.AddRange(["-i", timeline.Intro.Path]);
            introIndex = nextIndex++;
        }

        int? outroIndex = null;
        if (timeline.Outro is not null)
        {
            args.AddRange(["-i", timeline.Outro.Path]);
            outroIndex = nextIndex++;
        }

        var video = BuildVideoGraph(
            width,
            height,
            presentation.ImageWindows,
            overlayFilter,
            logoIndex,
            timeline.IntroSeconds,
            timeline.OutroStart);
        var audio = BuildAudioGraph(
            introIndex,
            timeline.Intro,
            bedIndex,
            bedIsSilence,
            speechIndex,
            outroIndex,
            timeline.Outro,
            timeline.SpeechSeconds);
        var graph = _encoding.AdaptFilterComplexForEncoder($"{video};{audio}", _encoding.Encoder);
        args.AddRange(["-filter_complex", graph, "-map", "[vout]", "-map", "[aout]"]);
        _encoding.AppendVideoEncoder(args, stillImage: presentation.ImageWindows.Count == 0 && !timeline.ShowLogo);
        args.AddRange(["-c:a", "aac", "-b:a", "128k", "-ac", "2", "-ar", "48000"]);
        return args;
    }

    private static bool HasAudioFile(string? path)
        => !string.IsNullOrWhiteSpace(path) && File.Exists(path);

    private static string BuildAudioGraph(
        int? introIndex,
        NewsBumperClip? intro,
        int? bedIndex,
        bool bedIsSilence,
        int? speechIndex,
        int? outroIndex,
        NewsBumperClip? outro,
        int speechWindow)
    {
        const string aformat = "aformat=sample_fmts=fltp:sample_rates=48000:channel_layouts=stereo";
        var parts = new List<string>();
        var concat = new List<string>();
        var window = Fmt(Math.Max(1, speechWindow));

        if (introIndex is int introIn && intro is not null && intro.Duration > 0.05)
        {
            parts.Add(
                $"[{introIn}:a]{aformat},atrim=start={Fmt(intro.TrimStart)}:end={Fmt(intro.TrimEnd)}," +
                $"asetpts=PTS-STARTPTS,{FadeOut(intro.Duration)}[aintro]");
            concat.Add("[aintro]");
        }

        if (speechIndex is int speechIn && bedIndex is int bedIn && !bedIsSilence)
        {
            parts.Add($"[{bedIn}:a]volume=0.18,{aformat},atrim=0:{window},asetpts=PTS-STARTPTS[abed]");
            parts.Add($"[{speechIn}:a]{aformat},apad=whole_dur={window},atrim=0:{window},asetpts=PTS-STARTPTS[aspeech]");
            parts.Add("[abed][aspeech]amix=inputs=2:duration=first:dropout_transition=2[amid]");
            concat.Add("[amid]");
        }
        else if (speechIndex is int speechOnly)
        {
            parts.Add($"[{speechOnly}:a]{aformat},apad=whole_dur={window},atrim=0:{window},asetpts=PTS-STARTPTS[amid]");
            concat.Add("[amid]");
        }
        else if (bedIndex is int bedOnly)
        {
            var volume = bedIsSilence ? "" : "volume=0.35,";
            parts.Add($"[{bedOnly}:a]{volume}{aformat},atrim=0:{window},asetpts=PTS-STARTPTS[amid]");
            concat.Add("[amid]");
        }

        if (outroIndex is int outroIn && outro is not null && outro.Duration > 0.05)
        {
            parts.Add(
                $"[{outroIn}:a]{aformat},atrim=start={Fmt(outro.TrimStart)}:end={Fmt(outro.TrimEnd)}," +
                $"asetpts=PTS-STARTPTS,{FadeOut(outro.Duration)}[aoutro]");
            concat.Add("[aoutro]");
        }

        if (concat.Count == 0)
        {
            parts.Add("anullsrc=r=48000:cl=stereo[aout]");
            return string.Join(";", parts);
        }

        parts.Add(string.Concat(concat) + $"concat=n={concat.Count}:v=0:a=1[aout]");
        return string.Join(";", parts);
    }

    private static string FadeOut(double duration)
    {
        var start = Math.Max(0, duration - 0.35);
        return $"afade=t=out:st={Fmt(start)}:d=0.35";
    }

    private static string Fmt(double value)
        => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string BuildVideoGraph(
        int width,
        int height,
        IReadOnlyList<NewsImageWindow> imageWindows,
        string overlayFilter,
        int? logoIndex,
        double introEnd,
        double outroStart)
    {
        var parts = new List<string>();
        if (imageWindows.Count == 0)
        {
            parts.Add("[0:v]format=yuv420p[vimg]");
        }
        else
        {
            var imgW = Math.Max(240, (int)(width * 0.78));
            var imgH = Math.Max(180, (int)(height * 0.62));
            var imgX = Math.Max(0, (width - imgW) / 2);
            var imgY = Math.Max(24, (int)(height * 0.08));
            parts.Add("[0:v]format=yuv420p[base]");
            for (var i = 0; i < imageWindows.Count; i++)
            {
                parts.Add(
                    $"[{i + 1}:v]scale={imgW}:{imgH}:force_original_aspect_ratio=decrease:flags=lanczos," +
                    $"pad={imgW}:{imgH}:(ow-iw)/2:(oh-ih)/2:0x101010,setsar=1,format=yuv420p[im{i}]");
            }

            var prev = "[base]";
            for (var i = 0; i < imageWindows.Count; i++)
            {
                var next = i == imageWindows.Count - 1 ? "[vimg]" : $"[vo{i}]";
                var start = Fmt(imageWindows[i].Start);
                var end = Fmt(imageWindows[i].End);
                parts.Add($"{prev}[im{i}]overlay={imgX}:{imgY}:enable='gte(t\\,{start})*lt(t\\,{end})'{next}");
                prev = next;
            }
        }

        var afterAss = logoIndex is int ? "[vass]" : "[vout]";
        parts.Add($"[vimg]{overlayFilter}{afterAss}");
        if (logoIndex is int logo)
        {
            var logoW = Math.Max(160, (int)(width * 0.72));
            var logoH = Math.Max(120, (int)(height * 0.72));
            var enable = LogoEnable(introEnd, outroStart);
            parts.Add(
                $"[{logo}:v]scale={logoW}:{logoH}:force_original_aspect_ratio=decrease:flags=lanczos,format=rgba[logov]");
            parts.Add($"[vass][logov]overlay=(W-w)/2:(H-h)/2:enable='{enable}'[vout]");
        }

        return string.Join(";", parts);
    }

    private static string LogoEnable(double introEnd, double outroStart)
    {
        var intro = introEnd > 0.05 ? $"lt(t\\,{Fmt(introEnd)})" : null;
        var outro = $"gte(t\\,{Fmt(outroStart)})";
        return intro is null ? outro : intro + "+" + outro;
    }

    private static void AppendMux(List<string> args, double duration, bool mpegts, string? filePath)
    {
        args.AddRange(["-t", Fmt(Math.Max(1, duration))]);
        if (mpegts)
        {
            args.AddRange(["-f", "mpegts", "-mpegts_flags", "+resend_headers", "-flush_packets", "1", "pipe:1"]);
            return;
        }

        args.AddRange(["-f", "mp4", "-movflags", "+faststart", filePath!]);
    }

    private async Task<int> RunFfmpegAsync(IReadOnlyList<string> args, Stream? output, CancellationToken cancellationToken)
    {
        var stderr = new System.Text.StringBuilder();
        var command = Cli.Wrap(_ffmpegLocator.EncoderPath)
            .WithArguments(args)
            .WithStandardErrorPipe(PipeTarget.ToStringBuilder(stderr))
            .WithValidation(CommandResultValidation.None);
        if (output is not null)
        {
            command = command.WithStandardOutputPipe(PipeTarget.ToStream(output, autoFlush: true));
        }

        var result = await command.ExecuteAsync(cancellationToken);
        if (result.ExitCode != 0 && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("News ffmpeg exited {Code}: {Error}", result.ExitCode, stderr.ToString().Trim());
        }

        return result.ExitCode;
    }

    private List<string> BuildDrawtextArgs(int width, int height, NewsPresentation presentation)
    {
        Directory.CreateDirectory(presentation.WorkDir);
        var tickerPath = Path.Combine(presentation.WorkDir, "ticker.txt");
        var ticker = SpokenTicker(presentation.Beats, presentation.Articles);
        File.WriteAllText(tickerPath, ticker);
        var tickerFilter = NewsAssBuilder.EscapeAssFilterPath(tickerPath);
        var intro = presentation.Timeline.IntroSeconds;
        var outroStart = presentation.Timeline.OutroStart;
        var storyEnable = $"gte(t\\,{Fmt(intro)})*lt(t\\,{Fmt(outroStart)})";
        var vf =
            $"drawbox=x=0:y=0:w=iw:h=90:color=0xe11d48@0.92:t=fill:enable='{storyEnable}'," +
            $"drawtext=text='{EscapeDraw(presentation.Header)}':fontcolor=white:fontsize=36:x=40:y=28:enable='{storyEnable}'," +
            $"drawbox=x=0:y=h-80:w=iw:h=80:color=0x202020@0.92:t=fill:enable='{storyEnable}'," +
            $"drawtext=textfile='{tickerFilter}':fontcolor=white:fontsize=26:x=w-mod(t*70\\,w+text_w):y=h-52:enable='{storyEnable}'";

        return BuildEncodeArgs(width, height, presentation, vf);
    }

    private static string SpokenTicker(IReadOnlyList<NewsStoryBeat> beats, IReadOnlyList<NewsArticle> articles)
    {
        var parts = beats
            .Where(beat => beat.ShowOnScreen)
            .Select(beat => string.IsNullOrWhiteSpace(beat.Body) ? beat.Title : beat.Title + ". " + beat.Body)
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .ToList();
        if (parts.Count > 0)
        {
            return string.Join("   •   ", parts);
        }

        return string.Join("   •   ", articles.Select(a => a.Title).DefaultIfEmpty("No headlines loaded"));
    }

    private async Task<string?[]> DownloadArticleImagesAsync(
        IReadOnlyList<NewsArticle> articles,
        string workDir,
        CancellationToken cancellationToken)
    {
        var result = new string?[articles.Count];
        var cache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var dir = Path.Combine(workDir, "images");
        Directory.CreateDirectory(dir);
        var client = _http.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(12);
        if (client.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd("ChannelFlow-Server/0.0.3 (news)");
        }

        var saved = 0;
        for (var i = 0; i < articles.Count; i++)
        {
            var url = articles[i].ImageUrl;
            if (string.IsNullOrWhiteSpace(url))
            {
                continue;
            }

            if (cache.TryGetValue(url, out var cached))
            {
                result[i] = cached;
                continue;
            }

            try
            {
                using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    continue;
                }

                var media = response.Content.Headers.ContentType?.MediaType;
                if (media is not null
                    && (!media.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
                        || media.Contains("svg", StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                if (bytes.Length < 32 || bytes.Length > 8_000_000)
                {
                    continue;
                }

                var ext = GuessImageExtension(media, url);
                var path = Path.Combine(dir, "story-" + saved.ToString(CultureInfo.InvariantCulture) + ext);
                await File.WriteAllBytesAsync(path, bytes, cancellationToken);
                cache[url] = path;
                result[i] = path;
                saved++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "News image download failed for {Url}", url);
            }
        }

        return result;
    }

    private static string GuessImageExtension(string? mediaType, string url)
    {
        if (mediaType is not null)
        {
            if (mediaType.Contains("png", StringComparison.OrdinalIgnoreCase))
            {
                return ".png";
            }

            if (mediaType.Contains("webp", StringComparison.OrdinalIgnoreCase))
            {
                return ".webp";
            }

            if (mediaType.Contains("gif", StringComparison.OrdinalIgnoreCase))
            {
                return ".gif";
            }
        }

        var path = url.Split('?', 2)[0];
        if (path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
        {
            return ".png";
        }

        if (path.EndsWith(".webp", StringComparison.OrdinalIgnoreCase))
        {
            return ".webp";
        }

        if (path.EndsWith(".gif", StringComparison.OrdinalIgnoreCase))
        {
            return ".gif";
        }

        return ".jpg";
    }

    private string? ResolveNewsMusicPath(NewsSettings settings)
    {
        if (IsNoMusic(settings))
        {
            return null;
        }

        var tracks = !string.IsNullOrWhiteSpace(settings.MusicLibraryId) || !string.IsNullOrWhiteSpace(settings.MusicLibraryName)
            ? _catalog.QueryMusicAudioFromLibrary(settings.MusicLibraryId, settings.MusicLibraryName)
            : [];
        if (tracks.Count == 0)
        {
            return _ebs.ResolveBackgroundMusicPath();
        }

        return _catalog.GetMediaPath(tracks[Random.Shared.Next(tracks.Count)]);
    }

    private async Task<NewsBumperClip?> ResolveBumperAsync(
        string stem,
        string? musicFallbackPath,
        bool fromStart,
        CancellationToken cancellationToken)
    {
        var dedicated = FindBumperFile(stem);
        if (HasAudioFile(dedicated))
        {
            var duration = await _tts.ProbeDurationSecondsAsync(dedicated!, cancellationToken);
            if (duration >= 0.4)
            {
                return new NewsBumperClip(dedicated!, 0, Math.Min(duration, 20));
            }
        }

        if (!HasAudioFile(musicFallbackPath))
        {
            return null;
        }

        var trackDuration = await _tts.ProbeDurationSecondsAsync(musicFallbackPath!, cancellationToken);
        if (trackDuration < 0.4)
        {
            return null;
        }

        var clip = Math.Min(8, trackDuration);
        var start = fromStart ? 0 : Math.Max(0, trackDuration - clip);
        return new NewsBumperClip(musicFallbackPath!, start, clip);
    }

    private static string? FindBumperFile(string stem)
    {
        foreach (var dir in GetBumperSearchDirs())
        {
            foreach (var name in new[] { stem, "FlowWire-" + stem })
            {
                var found = FindBumperFileInDir(dir, name);
                if (found is not null)
                {
                    return found;
                }
            }
        }

        return null;
    }

    private static IEnumerable<string> GetBumperSearchDirs()
    {
        var runtime = FinTvRuntime.Current;
        if (runtime is null)
        {
            yield break;
        }

        yield return runtime.NewsFolder;
        if (!string.IsNullOrWhiteSpace(runtime.LogosFolder))
        {
            yield return Path.Combine(runtime.LogosFolder, "binarygeek119", "News");
        }

        if (!string.IsNullOrWhiteSpace(runtime.BundledLogosFolder))
        {
            yield return Path.Combine(runtime.BundledLogosFolder, "News");
        }
    }

    private static string? FindBumperFileInDir(string newsDir, string stem)
    {
        if (!Directory.Exists(newsDir))
        {
            return null;
        }

        foreach (var ext in new[] { ".mp3", ".wav", ".m4a", ".aac", ".ogg", ".flac", ".opus" })
        {
            var path = Path.Combine(newsDir, stem + ext);
            if (File.Exists(path))
            {
                return path;
            }
        }

        return Directory.EnumerateFiles(newsDir)
            .FirstOrDefault(path =>
                string.Equals(Path.GetFileNameWithoutExtension(path), stem, StringComparison.OrdinalIgnoreCase)
                && IsBumperExtension(Path.GetExtension(path)));
    }

    private async Task<string?> ResolveNewsLogoPathAsync(Channel? channel, CancellationToken cancellationToken)
    {
        if (HasImageFile(channel?.ChannelLogoPath))
        {
            return channel!.ChannelLogoPath;
        }

        var newsChannel = await _db.Channels.AsNoTracking()
            .Where(row => row.ContentType == ChannelContentType.News)
            .OrderByDescending(row => row.Enabled)
            .FirstOrDefaultAsync(cancellationToken);
        if (HasImageFile(newsChannel?.ChannelLogoPath))
        {
            return newsChannel!.ChannelLogoPath;
        }

        var runtime = FinTvRuntime.Current;
        foreach (var root in new[]
                 {
                     runtime?.LogosFolder is { Length: > 0 } logos ? Path.Combine(logos, "binarygeek119") : null,
                     runtime?.BundledLogosFolder
                 }.Where(path => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path)))
        {
            var found = Directory.EnumerateFiles(root!, "FlowWire.png", SearchOption.AllDirectories).FirstOrDefault();
            if (HasImageFile(found))
            {
                return found;
            }
        }

        return null;
    }

    private static bool HasImageFile(string? path)
        => !string.IsNullOrWhiteSpace(path) && File.Exists(path);

    internal const string NoMusicLibraryId = "none";

    internal static bool IsNoMusic(NewsSettings settings)
    {
        var id = settings.MusicLibraryId?.Trim();
        if (string.Equals(id, NoMusicLibraryId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return string.Equals(settings.MusicLibraryName?.Trim(), "None", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(id);
    }

    private async Task<NewsShowCopy> PrepareShowAsync(
        string header,
        IReadOnlyList<NewsArticle> articles,
        NewsSettings settings,
        CancellationToken cancellationToken)
    {
        if (!settings.AiRewrite || articles.Count == 0)
        {
            return new NewsShowCopy(articles, null, null);
        }

        return await _writer.RewriteAsync(header, articles, settings, cancellationToken);
    }

    private static string ResolveIntro(string header, NewsSettings settings, NewsShowCopy show)
    {
        if (!string.IsNullOrWhiteSpace(settings.IntroText))
        {
            return settings.IntroText.Trim();
        }

        if (!string.IsNullOrWhiteSpace(show.Intro))
        {
            return show.Intro.Trim();
        }

        return NewsShowWriter.DefaultIntro(header);
    }

    private static string ResolveOutro(string header, NewsSettings settings, NewsShowCopy show)
    {
        if (!string.IsNullOrWhiteSpace(settings.OutroText))
        {
            return settings.OutroText.Trim();
        }

        if (!string.IsNullOrWhiteSpace(show.Outro))
        {
            return show.Outro.Trim();
        }

        return NewsShowWriter.DefaultOutro(header);
    }

    private static int DurationForSpeech(double speechSeconds, NewsSettings settings)
    {
        if (speechSeconds <= 1)
        {
            return 90;
        }

        var max = settings.AiRewrite || NewsTtsService.IsAiEngine(settings.TtsEngine) ? 720 : 240;
        return (int)Math.Clamp(Math.Ceiling(speechSeconds) + 4, 45, max);
    }

    private static string BuildScript(string header, IReadOnlyList<NewsArticle> articles, NewsSettings settings, NewsShowCopy show)
    {
        var sb = new System.Text.StringBuilder();
        var intro = ResolveIntro(header, settings, show);
        if (!string.IsNullOrWhiteSpace(intro))
        {
            sb.Append(intro.Trim()).Append(". ");
        }

        foreach (var article in articles)
        {
            if (settings.AiRewrite)
            {
                if (!string.IsNullOrWhiteSpace(article.Summary))
                {
                    sb.Append(article.Summary.Trim()).Append(". ");
                }
                else if (!string.IsNullOrWhiteSpace(article.Title))
                {
                    sb.Append(article.Title.Trim()).Append(". ");
                }

                continue;
            }

            sb.Append(article.Title).Append(". ");
            if (!settings.ReadHeadlinesOnly && !string.IsNullOrWhiteSpace(article.Summary))
            {
                sb.Append(article.Summary).Append(". ");
            }
        }

        var outro = ResolveOutro(header, settings, show);
        sb.Append(outro.Trim());

        return sb.ToString();
    }

    private static List<NewsStoryBeat> BuildSpokenBeats(
        string header,
        IReadOnlyList<NewsArticle> articles,
        NewsSettings settings,
        IReadOnlyList<string?> images,
        int duration,
        NewsShowCopy show,
        double clockOffset)
    {
        var parts = new List<(string Title, string Body, string? Image, bool Show)>();
        var intro = ResolveIntro(header, settings, show);
        if (!string.IsNullOrWhiteSpace(intro))
        {
            if (settings.AiRewrite)
            {
                parts.Add((header, NewsShowWriter.AnchorName, null, settings.ShowHeader));
            }
            else
            {
                parts.Add((intro, "", null, settings.ShowHeader));
            }
        }

        for (var i = 0; i < articles.Count; i++)
        {
            var article = articles[i];
            var body = (settings.AiRewrite || !settings.ReadHeadlinesOnly) && !string.IsNullOrWhiteSpace(article.Summary)
                ? article.Summary.Trim()
                : "";
            var image = i < images.Count ? images[i] : null;
            parts.Add((article.Title, body, image, true));
        }

        var outro = ResolveOutro(header, settings, show);
        parts.Add((settings.AiRewrite ? NewsShowWriter.AnchorName : outro, settings.AiRewrite ? outro : "", null, true));

        if (parts.Count == 0)
        {
            return [new NewsStoryBeat(clockOffset, clockOffset + duration, "FlowWire News", "", null, true)];
        }

        var weights = parts.Select(part => Math.Max(24, (part.Title + " " + part.Body).Length)).ToArray();
        var total = (double)weights.Sum();
        var beats = new List<NewsStoryBeat>(parts.Count);
        var t = clockOffset;
        var endAt = clockOffset + duration;
        for (var i = 0; i < parts.Count; i++)
        {
            var start = t;
            var end = i == parts.Count - 1 ? endAt : t + duration * weights[i] / total;
            if (end < start + 1)
            {
                end = Math.Min(endAt, start + 1);
            }

            beats.Add(new NewsStoryBeat(start, end, parts[i].Title, parts[i].Body, parts[i].Image, parts[i].Show));
            t = end;
        }

        return beats;
    }

    private static List<NewsImageWindow> ImageWindows(IReadOnlyList<NewsStoryBeat> beats)
        => beats
            .Where(beat => !string.IsNullOrWhiteSpace(beat.ImagePath))
            .Select(beat => new NewsImageWindow(beat.ImagePath!, beat.StartSeconds, beat.EndSeconds))
            .ToList();

    private static string EscapeDraw(string text)
        => text.Replace("\\", "\\\\")
            .Replace("'", "\u2019")
            .Replace(":", "\\:")
            .Replace("%", "\\%");

    private static bool IsBumperExtension(string extension)
        => extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".wav", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".m4a", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".aac", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".ogg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".flac", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".opus", StringComparison.OrdinalIgnoreCase);
}

internal sealed record NewsBumperClip(string Path, double TrimStart, double Duration)
{
    public double TrimEnd => TrimStart + Duration;
}

internal sealed record NewsTimeline(
    NewsBumperClip? Intro,
    int SpeechSeconds,
    NewsBumperClip? Outro,
    string? MusicPath,
    string? SpeechPath,
    string? LogoPath)
{
    public double IntroSeconds => Intro?.Duration ?? 0;
    public double OutroSeconds => Outro?.Duration ?? 0;
    public double OutroStart => IntroSeconds + SpeechSeconds;
    public double TotalSeconds => IntroSeconds + SpeechSeconds + OutroSeconds;
    public bool ShowLogo =>
        !string.IsNullOrWhiteSpace(LogoPath)
        && File.Exists(LogoPath)
        && (IntroSeconds > 0.05 || OutroSeconds > 0.05);
}

internal sealed record NewsPresentation(
    string Header,
    IReadOnlyList<NewsArticle> Articles,
    IReadOnlyList<NewsStoryBeat> Beats,
    IReadOnlyList<NewsImageWindow> ImageWindows,
    string AssPath,
    NewsTimeline Timeline,
    string WorkDir);
