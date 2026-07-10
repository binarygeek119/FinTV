using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Jellyfin.Plugin.FinTV.Data;
using Jellyfin.Plugin.FinTV.Domain;
using MediaBrowser.Controller;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Jellyfin.Plugin.FinTV.Services;

public class EpgService
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly FinTvDbContext _db;
    private readonly HolidayChannelService _holidays;
    private readonly GuideMetadataService _guideMetadata;
    private readonly WeatherGuideMetadataService _weatherGuideMetadata;

    public EpgService(
        FinTvDbContext db,
        HolidayChannelService holidays,
        GuideMetadataService guideMetadata,
        WeatherGuideMetadataService weatherGuideMetadata)
    {
        _db = db;
        _holidays = holidays;
        _guideMetadata = guideMetadata;
        _weatherGuideMetadata = weatherGuideMetadata;
    }

    public async Task<byte[]> GenerateXmlTvBytesAsync(string baseUrl, CancellationToken cancellationToken = default)
    {
        var doc = await BuildXmlTvDocumentAsync(baseUrl, cancellationToken);
        return SerializeUtf8(doc);
    }

    public async Task<string> GenerateXmlTvAsync(string baseUrl, CancellationToken cancellationToken = default)
    {
        return Utf8NoBom.GetString(await GenerateXmlTvBytesAsync(baseUrl, cancellationToken));
    }

    private async Task<XDocument> BuildXmlTvDocumentAsync(string baseUrl, CancellationToken cancellationToken)
    {
        var channels = await _db.Channels.Where(c => c.Enabled).OrderBy(c => c.Number).AsNoTracking().ToListAsync(cancellationToken);
        var start = DateTime.UtcNow.AddHours(-3);
        var end = DateTime.UtcNow.AddDays(PlayoutScheduleHelper.GetPlayoutDaysToBuild());

        var root = new XElement(
            "tv",
            new XAttribute("generator-info-name", "FinTV"),
            new XAttribute("generator-info-url", "https://github.com/binarygeek119/FinTV"));

        foreach (var channel in channels)
        {
            var channelElement = new XElement(
                "channel",
                new XAttribute("id", channel.Id.ToString("N")),
                new XElement("display-name", channel.Name),
                new XElement("icon", new XAttribute("src", GetLogoUrl(channel, baseUrl))));
            channelElement.Add(new XElement("lcn", ChannelNumbers.Format(channel.Number)));
            root.Add(channelElement);
        }

        var items = await _db.PlayoutItems
            .Where(p =>
                p.Finish > start
                && p.Start < end
                && (p.GuideGroup == null || p.GuideGroup != "commercial"))
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var metadataByItemId = _guideMetadata.ResolveBatch(items.Select(i => i.JellyfinItemId));
        var channelsById = channels.ToDictionary(c => c.Id);
        var weatherItems = items
            .Where(i => i.IsVirtual && i.VirtualSource == VirtualContentSource.WeatherStar)
            .ToList();
        var weatherMetadataByPlayoutId = await _weatherGuideMetadata.ResolveAsync(
            weatherItems,
            channelsById,
            channel => GetLogoUrl(channel, baseUrl),
            cancellationToken);

        foreach (var item in items)
        {
            GuideProgramMetadata? metadata = null;
            if (weatherMetadataByPlayoutId.TryGetValue(item.Id, out var weatherMetadata))
            {
                metadata = weatherMetadata;
            }
            else if (item.JellyfinItemId.HasValue)
            {
                metadataByItemId.TryGetValue(item.JellyfinItemId.Value, out metadata);
            }

            root.Add(BuildProgrammeElement(item, metadata, baseUrl));
        }

        return new XDocument(new XDeclaration("1.0", "UTF-8", null), root);
    }

    private static XElement BuildProgrammeElement(PlayoutItem item, GuideProgramMetadata? metadata, string baseUrl)
    {
        var programme = new XElement(
            "programme",
            new XAttribute("start", FormatXmlTvDate(item.Start)),
            new XAttribute("stop", FormatXmlTvDate(item.Finish)),
            new XAttribute("channel", item.ChannelId.ToString("N")));

        var title = metadata?.Title ?? item.Title;
        if (!string.IsNullOrWhiteSpace(title))
        {
            programme.Add(CreateLangElement("title", title, metadata?.Language));
        }

        if (!string.IsNullOrWhiteSpace(metadata?.SubTitle))
        {
            programme.Add(CreateLangElement("sub-title", metadata.SubTitle, metadata.Language));
        }

        if (!string.IsNullOrWhiteSpace(metadata?.Description))
        {
            programme.Add(CreateLangElement("desc", metadata.Description, metadata.Language));
        }

        if (metadata?.Categories is not null)
        {
            foreach (var category in metadata.Categories.Where(c => !string.IsNullOrWhiteSpace(c)))
            {
                programme.Add(CreateLangElement("category", category, metadata.Language));
            }
        }

        if (!string.IsNullOrWhiteSpace(metadata?.EpisodeXmlTvNs))
        {
            programme.Add(new XElement(
                "episode-num",
                new XAttribute("system", "xmltv_ns"),
                metadata.EpisodeXmlTvNs));
        }

        if (!string.IsNullOrWhiteSpace(metadata?.EpisodeOnScreen))
        {
            programme.Add(new XElement(
                "episode-num",
                new XAttribute("system", "onscreen"),
                metadata.EpisodeOnScreen));
        }

        if (metadata?.ProductionYear is int year && year > 0)
        {
            programme.Add(new XElement("date", year.ToString(CultureInfo.InvariantCulture)));
        }

        if (!string.IsNullOrWhiteSpace(metadata?.OfficialRating))
        {
            programme.Add(new XElement(
                "rating",
                new XAttribute("system", "MPAA"),
                metadata.OfficialRating));
        }

        var posterUrl = !string.IsNullOrWhiteSpace(metadata?.IconUrl)
            ? metadata.IconUrl
            : GuideMetadataService.GetPosterUrl(baseUrl, metadata?.PosterItemId);
        if (!string.IsNullOrWhiteSpace(posterUrl))
        {
            programme.Add(new XElement("icon", new XAttribute("src", posterUrl)));
        }

        return programme;
    }

    private static XElement CreateLangElement(string name, string value, string? language)
    {
        return string.IsNullOrWhiteSpace(language)
            ? new XElement(name, value)
            : new XElement(name, new XAttribute("lang", language), value);
    }

    private static byte[] SerializeUtf8(XDocument doc)
    {
        var settings = new XmlWriterSettings
        {
            Encoding = Utf8NoBom,
            Indent = true,
            OmitXmlDeclaration = false
        };

        using var ms = new MemoryStream();
        using (var writer = XmlWriter.Create(ms, settings))
        {
            doc.Save(writer);
        }

        return ms.ToArray();
    }

    public async Task<string> GenerateM3uAsync(string baseUrl, CancellationToken cancellationToken = default)
    {
        var channels = await _db.Channels.Where(c => c.Enabled).OrderBy(c => c.Number).AsNoTracking().ToListAsync(cancellationToken);
        var sb = new StringBuilder();
        sb.AppendLine("#EXTM3U");

        foreach (var channel in channels)
        {
            sb.Append("#EXTINF:-1 tvg-id=\"")
                .Append(channel.Id.ToString("N"))
                .Append("\" tvg-chno=\"")
                .Append(ChannelNumbers.Format(channel.Number))
                .Append("\" tvg-name=\"")
                .Append(EscapeM3u(channel.Name))
                .Append("\" tvg-logo=\"")
                .Append(GetLogoUrl(channel, baseUrl))
                .Append("\",")
                .AppendLine(channel.Name);

            sb.AppendLine(CultureInfo.InvariantCulture, $"{baseUrl.TrimEnd('/')}/FinTV/iptv/stream/{channel.Id:N}");
        }

        return sb.ToString();
    }

    public static string GetPublicBaseUrl(HttpRequest? request, IServerApplicationHost? appHost = null)
    {
        var configured = Plugin.Instance?.Configuration.PublicBaseUrl;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured.TrimEnd('/');
        }

        if (request is not null && appHost is not null)
        {
            try
            {
                return appHost.GetSmartApiUrl(request).TrimEnd('/');
            }
            catch
            {
                // Fall back to forwarded headers or request host when Jellyfin cannot resolve a smart API URL.
            }
        }

        if (request is not null)
        {
            var forwardedProto = request.Headers["X-Forwarded-Proto"].FirstOrDefault();
            var forwardedHost = request.Headers["X-Forwarded-Host"].FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(forwardedHost))
            {
                var scheme = !string.IsNullOrWhiteSpace(forwardedProto) ? forwardedProto : request.Scheme;
                return $"{scheme}://{forwardedHost}".TrimEnd('/');
            }

            return $"{request.Scheme}://{request.Host}".TrimEnd('/');
        }

        return "http://localhost:8096";
    }

    private string GetLogoUrl(Channel channel, string? baseUrl = null)
    {
        baseUrl ??= Plugin.Instance?.Configuration.PublicBaseUrl ?? "http://localhost:8096";
        var fileName = channel.LogoFileName;
        if (_holidays.IsHolidayChannel(channel))
        {
            var scheduleDate = _holidays.GetScheduleDateUtc(DateTime.UtcNow);
            fileName = _holidays.ResolveEffectiveLogoFileName(channel, scheduleDate) ?? fileName;
        }

        if (!string.IsNullOrWhiteSpace(fileName))
        {
            return $"{baseUrl.TrimEnd('/')}/FinTV/api/logos/{channel.Id:N}/{Uri.EscapeDataString(fileName)}";
        }

        return string.Empty;
    }

    private static string FormatXmlTvDate(DateTime utc)
    {
        return utc.ToUniversalTime().ToString("yyyyMMddHHmmss zzz", CultureInfo.InvariantCulture)
            .Replace(":", string.Empty);
    }

    private static string EscapeM3u(string value) => value.Replace(',', ' ');
}
