using System.Globalization;
using System.Text;
using System.Xml.Linq;
using Jellyfin.Plugin.FinTV.Data;
using Jellyfin.Plugin.FinTV.Domain;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Jellyfin.Plugin.FinTV.Services;

public class EpgService
{
    private readonly FinTvDbContext _db;

    public EpgService(FinTvDbContext db)
    {
        _db = db;
    }

    public async Task<string> GenerateXmlTvAsync(CancellationToken cancellationToken = default)
    {
        var channels = await _db.Channels.Where(c => c.Enabled).OrderBy(c => c.Number).AsNoTracking().ToListAsync(cancellationToken);
        var start = DateTime.UtcNow.AddHours(-3);
        var end = DateTime.UtcNow.AddDays(Plugin.Instance?.Configuration.PlayoutDaysToBuild ?? 3);

        var root = new XElement(
            "tv",
            new XAttribute("generator-info-name", "FinTV"),
            new XAttribute("generator-info-url", "https://github.com/binarygeek119/FinTV"));

        foreach (var channel in channels)
        {
            root.Add(new XElement(
                "channel",
                new XAttribute("id", channel.Id.ToString("N")),
                new XElement("display-name", channel.Name),
                new XElement("icon", new XAttribute("src", GetLogoUrl(channel)))));
        }

        var items = await _db.PlayoutItems
            .Where(p => p.Start >= start && p.Start < end && p.GuideGroup != "commercial")
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

        var doc = new XDocument(new XDeclaration("1.0", "UTF-8", null), root);
        var sb = new StringBuilder();
        using (var writer = new StringWriter(sb))
        {
            doc.Save(writer);
        }

        return sb.ToString();
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
                .Append(channel.Number)
                .Append("\" tvg-name=\"")
                .Append(EscapeM3u(channel.Name))
                .Append("\" tvg-logo=\"")
                .Append(GetLogoUrl(channel, baseUrl))
                .Append("\",")
                .AppendLine(channel.Name);

            sb.AppendLine($"{baseUrl.TrimEnd('/')}/FinTV/iptv/stream/{channel.Id:N}");
        }

        return sb.ToString();
    }

    public static string GetPublicBaseUrl(HttpRequest? request)
    {
        var configured = Plugin.Instance?.Configuration.PublicBaseUrl;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured.TrimEnd('/');
        }

        if (request is null)
        {
            return "http://localhost:8096";
        }

        return $"{request.Scheme}://{request.Host}";
    }

    private static string GetLogoUrl(Channel channel, string? baseUrl = null)
    {
        baseUrl ??= Plugin.Instance?.Configuration.PublicBaseUrl ?? "http://localhost:8096";
        if (!string.IsNullOrWhiteSpace(channel.LogoFileName))
        {
            return $"{baseUrl.TrimEnd('/')}/FinTV/api/logos/{channel.Id:N}/{Uri.EscapeDataString(channel.LogoFileName)}";
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
