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

    public EpgService(FinTvDbContext db, HolidayChannelService holidays)
    {
        _db = db;
        _holidays = holidays;
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

        foreach (var item in items)
        {
            root.Add(new XElement(
                "programme",
                new XAttribute("start", FormatXmlTvDate(item.Start)),
                new XAttribute("stop", FormatXmlTvDate(item.Finish)),
                new XAttribute("channel", item.ChannelId.ToString("N")),
                new XElement("title", item.Title)));
        }

        return new XDocument(new XDeclaration("1.0", "UTF-8", null), root);
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
            return appHost.GetSmartApiUrl(request).TrimEnd('/');
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
