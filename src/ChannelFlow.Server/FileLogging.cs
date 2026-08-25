using Serilog;
using Serilog.Events;

namespace FinTv;

internal static class FileLogging
{
    private const string OutputTemplate =
        "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}";

    public static string ResolveDirectory(string contentRoot)
    {
        var fromEnv = AppEnvironment.Get("LOG_DIR");
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            return Path.GetFullPath(fromEnv);
        }

        var configDir = AppEnvironment.Get("CONFIG")
            ?? Path.Combine(contentRoot, "config");
        return Path.GetFullPath(Path.Combine(configDir, "logs"));
    }

    public static void Configure(WebApplicationBuilder builder)
    {
        var logsDir = ResolveDirectory(builder.Environment.ContentRootPath);
        Directory.CreateDirectory(logsDir);
        var logFile = Path.Combine(logsDir, "channelflow-.log");

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
            .MinimumLevel.Override("System.Net.Http.HttpClient", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .WriteTo.Console(outputTemplate: OutputTemplate)
            .WriteTo.File(
                logFile,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                fileSizeLimitBytes: 50 * 1024 * 1024,
                rollOnFileSizeLimit: true,
                shared: true,
                flushToDiskInterval: TimeSpan.FromSeconds(1),
                outputTemplate: OutputTemplate)
            .CreateLogger();

        builder.Host.UseSerilog();
        Log.Information("Writing logs to {LogDirectory}", logsDir);
    }
}
