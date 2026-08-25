using FinTv.Configuration;
using FinTv.Data;
using FinTv.Domain;
using Microsoft.EntityFrameworkCore;

namespace FinTv;

public sealed class FinTvRuntime
{
    public static FinTvRuntime Current { get; set; } = null!;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly object _configGate = new();
    private PluginConfiguration _configuration = new();

    public FinTvRuntime(IServiceScopeFactory scopeFactory, IWebHostEnvironment env, IConfiguration config)
    {
        _scopeFactory = scopeFactory;
        var configDir = AppEnvironment.FromConfiguration(config, "CONFIG")
            ?? Path.Combine(env.ContentRootPath, "config");
        DataFolder = configDir;
        LogosFolder = Path.Combine(configDir, "logos");
        EbsFolder = Path.Combine(configDir, "ebs");
        EbsCustomSlatesFolder = Path.Combine(EbsFolder, "custom");
        WeatherStarFolder = Path.Combine(configDir, "weatherstar");
        NewsFolder = Path.Combine(configDir, "news");
        LogsFolder = FileLogging.ResolveDirectory(env.ContentRootPath);
        BundledLogosFolder = Path.Combine(env.ContentRootPath, "wwwroot", "logos", "binarygeek119");
        Directory.CreateDirectory(DataFolder);
        Directory.CreateDirectory(LogosFolder);
        Directory.CreateDirectory(EbsCustomSlatesFolder);
        Directory.CreateDirectory(WeatherStarFolder);
        Directory.CreateDirectory(NewsFolder);
        Directory.CreateDirectory(LogsFolder);
        MusicFolder = Path.Combine(configDir, "music");
        Directory.CreateDirectory(MusicFolder);
        Current = this;
    }

    public string DataFolder { get; }

    public string LogosFolder { get; }

    public string EbsFolder { get; }

    public string EbsCustomSlatesFolder { get; }

    public string WeatherStarFolder { get; }

    public string NewsFolder { get; }

    public string MusicFolder { get; }

    public string LogsFolder { get; }

    public string BundledLogosFolder { get; }

    public PluginConfiguration Configuration => _configuration;

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FinTvDbContext>();
        var row = await db.AppSettings.AsNoTracking().FirstOrDefaultAsync(cancellationToken);
        if (row is null || string.IsNullOrWhiteSpace(row.Json))
        {
            _configuration = new PluginConfiguration();
        }
        else
        {
            _configuration = FinTvJson.Deserialize<PluginConfiguration>(row.Json) ?? new PluginConfiguration();
        }

        EnsureApiKey();
        _configuration.Transcode ??= new TranscodeSettings();
        _configuration.YouTube ??= new YouTubeSettings();
    }

    private void EnsureApiKey()
    {
        if (!string.IsNullOrWhiteSpace(_configuration.ApiKey))
        {
            return;
        }

        _configuration.ApiKey = Auth.PluginApiKey.Generate();
        SaveConfiguration();
    }

    public void SaveConfiguration()
    {
        lock (_configGate)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FinTvDbContext>();
            var row = db.AppSettings.FirstOrDefault(r => r.Id == 1);
            if (row is null)
            {
                row = new AppSettingsRow { Id = 1 };
                db.AppSettings.Add(row);
            }

            row.Json = FinTvJson.Serialize(_configuration);
            db.Entry(row).Property(e => e.Json).IsModified = true;
            db.SaveChanges();
        }
    }
}
