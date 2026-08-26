using Jellyfin.Plugin.FinTV.Domain;
using Jellyfin.Plugin.FinTV.Streaming;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.FinTV.Services;

public class WeatherStarChannelService
{
    public const string DefaultWeatherStarBaseUrl = "https://weather.jmthornton.net";

    public const string DefaultWeatherLocationQuery = "50317, Des Moines, IA, USA";

    public const string DefaultWeatherStarPermalinkQuery =
        "hazards=true&current-weather=true&latest-observations=true&hourly=true&hourly-graph=true&travel=true&regional-forecast=true&local-forecast=true&extended-forecast=true&almanac=true&spc-outlook=true&radar=true&stickyKiosk=true&customTextEnable=false&speed=1.00&viewMode=standard&units=us&customText=&mediaVolume=0.75&wide=false&portrait=false&enhanced=false&scanLines=false";

    private static readonly HashSet<string> LocationQueryKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "latLonQuery",
        "latLon",
        "txtLocation",
        "lat",
        "lon"
    };

    private static readonly HashSet<string> CaptureTimeQueryKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "kiosk",
        "wide"
    };

    private readonly ILogger<WeatherStarChannelService> _logger;
    private readonly FfmpegCommandBuilder _ffmpegBuilder;
    private readonly EbsService _ebs;
    private readonly IMediaEncoder _mediaEncoder;

    public WeatherStarChannelService(
        ILogger<WeatherStarChannelService> logger,
        FfmpegCommandBuilder ffmpegBuilder,
        EbsService ebs,
        IMediaEncoder mediaEncoder)
    {
        _logger = logger;
        _ffmpegBuilder = ffmpegBuilder;
        _ebs = ebs;
        _mediaEncoder = mediaEncoder;
    }

    public async Task StreamAsync(Domain.Channel channel, Stream output, CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "WeatherStar capture in the Jellyfin plugin is retired. Tune weather through ChannelFlow-Server IPTV for the native compositor.");
        await WriteEbsFallbackAsync(channel, _mediaEncoder.EncoderPath, output, cancellationToken);
    }

    internal static string BuildWeatherPageUrl(
        string locationQuery,
        string? baseUrl = null,
        string? permalinkQuery = null,
        bool autoWideForSixteenNine = false,
        AspectRatioMode aspectRatio = AspectRatioMode.SixteenNine)
    {
        var root = NormalizeWeatherStarBaseUrl(baseUrl);
        var parameters = ParseQueryParameters(permalinkQuery ?? DefaultWeatherStarPermalinkQuery);

        foreach (var key in LocationQueryKeys)
        {
            parameters.Remove(key);
        }

        parameters["kiosk"] = "true";
        if (autoWideForSixteenNine)
        {
            parameters["wide"] = aspectRatio == AspectRatioMode.FourThree ? "false" : "true";
        }

        var trimmedLocation = locationQuery.Trim();
        parameters["latLonQuery"] = trimmedLocation;
        parameters["txtLocation"] = trimmedLocation;
        if (WeatherLocationParser.TryParseLatLon(trimmedLocation, out var latitude, out var longitude))
        {
            parameters["lat"] = latitude.ToString(System.Globalization.CultureInfo.InvariantCulture);
            parameters["lon"] = longitude.ToString(System.Globalization.CultureInfo.InvariantCulture);
            parameters["latLon"] =
                $"{{\"lat\":{latitude.ToString(System.Globalization.CultureInfo.InvariantCulture)},\"lon\":{longitude.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}";
        }

        return $"{root}?{FormatQueryParameters(parameters)}";
    }

    internal static (string BaseUrl, string Query) SplitPermalink(string permalink)
    {
        if (string.IsNullOrWhiteSpace(permalink))
        {
            return (DefaultWeatherStarBaseUrl, DefaultWeatherStarPermalinkQuery);
        }

        var trimmed = permalink.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            return (DefaultWeatherStarBaseUrl, NormalizePermalinkQuery(trimmed));
        }

        var query = NormalizePermalinkQuery(uri.Query);
        var baseUrl = uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        return (string.IsNullOrWhiteSpace(baseUrl) ? DefaultWeatherStarBaseUrl : baseUrl, query);
    }

    internal static string NormalizePermalinkQuery(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return DefaultWeatherStarPermalinkQuery;
        }

        var trimmed = query.Trim();
        if (trimmed.StartsWith("?", StringComparison.Ordinal))
        {
            trimmed = trimmed[1..];
        }

        var parameters = ParseQueryParameters(trimmed);
        foreach (var key in LocationQueryKeys)
        {
            parameters.Remove(key);
        }

        foreach (var key in CaptureTimeQueryKeys)
        {
            parameters.Remove(key);
        }

        return parameters.Count == 0
            ? DefaultWeatherStarPermalinkQuery
            : FormatQueryParameters(parameters);
    }

    internal static string NormalizeWeatherStarBaseUrl(string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return DefaultWeatherStarBaseUrl;
        }

        var trimmed = baseUrl.Trim();
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            return uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        }

        var queryIndex = trimmed.IndexOf('?', StringComparison.Ordinal);
        return queryIndex < 0 ? trimmed.TrimEnd('/') : trimmed[..queryIndex].TrimEnd('/');
    }

    private static Dictionary<string, string> ParseQueryParameters(string? query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(query))
        {
            return result;
        }

        var trimmed = query.Trim();
        if (trimmed.StartsWith("?", StringComparison.Ordinal))
        {
            trimmed = trimmed[1..];
        }

        foreach (var segment in trimmed.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separatorIndex = segment.IndexOf('=');
            if (separatorIndex < 0)
            {
                result[Uri.UnescapeDataString(segment)] = string.Empty;
                continue;
            }

            var key = Uri.UnescapeDataString(segment[..separatorIndex]);
            var value = Uri.UnescapeDataString(segment[(separatorIndex + 1)..]);
            result[key] = value;
        }

        return result;
    }

    private static string FormatQueryParameters(IEnumerable<KeyValuePair<string, string>> parameters)
    {
        return string.Join(
            "&",
            parameters.Select(pair =>
                string.IsNullOrEmpty(pair.Value)
                    ? Uri.EscapeDataString(pair.Key)
                    : $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
    }

    private async Task WriteEbsFallbackAsync(
        Domain.Channel channel,
        string ffmpegPath,
        Stream output,
        CancellationToken cancellationToken)
    {
        var plan = _ebs.CreatePlaybackPlan(channel, durationSeconds: 120);
        var args = _ffmpegBuilder.BuildEbsCommand(channel, plan);
        await CliWrap.Cli.Wrap(ffmpegPath)
            .WithArguments(args)
            .WithStandardOutputPipe(CliWrap.PipeTarget.ToStream(output))
            .WithValidation(CliWrap.CommandResultValidation.None)
            .ExecuteAsync(cancellationToken);
    }
}
