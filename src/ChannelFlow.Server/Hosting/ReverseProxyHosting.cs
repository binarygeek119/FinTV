using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;

namespace FinTv;

internal static class ReverseProxyHosting
{
    public static void AddReverseProxySupport(this WebApplicationBuilder builder)
    {
        builder.Services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor
                | ForwardedHeaders.XForwardedProto
                | ForwardedHeaders.XForwardedHost
                | ForwardedHeaders.XForwardedPrefix;
            options.RequireHeaderSymmetry = false;
            options.ForwardLimit = 4;
            options.KnownIPNetworks.Clear();
            options.KnownProxies.Clear();
        });

        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            kestrel.Limits.MinResponseDataRate = null;
            kestrel.Limits.MinRequestBodyDataRate = null;
            kestrel.Limits.KeepAliveTimeout = TimeSpan.FromHours(8);
        });

        var configDir = AppEnvironment.FromConfiguration(builder.Configuration, "CONFIG")
            ?? Path.Combine(builder.Environment.ContentRootPath, "config");
        var keyDir = Path.Combine(configDir, "dataprotection");
        Directory.CreateDirectory(keyDir);
        builder.Services.AddDataProtection()
            .SetApplicationName("ChannelFlow")
            .PersistKeysToFileSystem(new DirectoryInfo(keyDir));
    }

    public static void UseReverseProxy(this WebApplication app)
    {
        app.UseForwardedHeaders();

        var pathBase = AppEnvironment.FromConfiguration(app.Configuration, "PATH_BASE")
            ?? Environment.GetEnvironmentVariable("ASPNETCORE_PATHBASE");
        if (!string.IsNullOrWhiteSpace(pathBase))
        {
            var trimmed = pathBase.Trim();
            if (!trimmed.StartsWith('/'))
            {
                trimmed = "/" + trimmed;
            }

            app.UsePathBase(trimmed.TrimEnd('/'));
        }
    }

    public static void MapSpaFallback(this WebApplication app)
    {
        async Task WriteIndex(HttpContext context)
        {
            var file = Path.Combine(app.Environment.WebRootPath, "index.html");
            var html = await File.ReadAllTextAsync(file);
            var prefix = context.Request.PathBase.HasValue
                ? context.Request.PathBase.Value!.TrimEnd('/')
                : "";
            var href = string.IsNullOrEmpty(prefix) ? "/" : prefix + "/";
            html = html.Replace("<base href=\"/\">", "<base href=\"" + href + "\">");
            html = html.Replace("window.__CF_BASE__=\"\"", "window.__CF_BASE__=\"" + prefix + "\"");
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.Headers.CacheControl = "no-store";
            await context.Response.WriteAsync(html);
        }

        app.MapGet("/", WriteIndex);
        app.MapGet("/index.html", WriteIndex);
        app.MapFallback(WriteIndex);
    }

    public static string PublicOrigin(HttpRequest request)
    {
        var prefix = request.PathBase.HasValue ? request.PathBase.Value!.TrimEnd('/') : "";
        return $"{request.Scheme}://{request.Host}{prefix}";
    }

    public static string? NormalizePublicBaseUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var url = value.Trim().TrimEnd('/');
        if (!url.Contains("://", StringComparison.Ordinal))
        {
            url = "https://" + url;
        }

        return url;
    }
}
