using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.FinTV.Services;

/// <summary>
/// Ensures the plugin manifest uses the embedded plugin image instead of a disk logo.png file.
/// Jellyfin re-downloads manifest imageUrl into logo.png when browsing the plugin catalog, which
/// locks the file during updates on Windows and Docker bind mounts.
/// </summary>
public sealed class PluginManifestRepairHostedService : IHostedService
{
    private const string MetaFileName = "meta.json";
    private const string DiskLogoFileName = "logo.png";

    private readonly ILogger<PluginManifestRepairHostedService> _logger;

    public PluginManifestRepairHostedService(ILogger<PluginManifestRepairHostedService> logger)
    {
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            RepairManifest();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FinTV could not repair plugin manifest image settings.");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private void RepairManifest()
    {
        var pluginDir = Path.GetDirectoryName(typeof(Plugin).Assembly.Location);
        if (string.IsNullOrEmpty(pluginDir))
        {
            return;
        }

        var metaPath = Path.Combine(pluginDir, MetaFileName);
        if (!File.Exists(metaPath))
        {
            return;
        }

        var node = JsonNode.Parse(File.ReadAllText(metaPath)) as JsonObject;
        if (node is null)
        {
            return;
        }

        var changed = false;
        if (!string.Equals(node["imageResourceName"]?.GetValue<string>(), Plugin.PluginImageResourceName, StringComparison.Ordinal))
        {
            node["imageResourceName"] = Plugin.PluginImageResourceName;
            changed = true;
        }

        if (!string.IsNullOrEmpty(node["imagePath"]?.GetValue<string>()))
        {
            node["imagePath"] = string.Empty;
            changed = true;
        }

        if (changed)
        {
            File.WriteAllText(metaPath, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            _logger.LogInformation("FinTV plugin manifest now uses embedded plugin image resource.");
        }

        var diskLogo = Path.Combine(pluginDir, DiskLogoFileName);
        if (!File.Exists(diskLogo))
        {
            return;
        }

        try
        {
            File.Delete(diskLogo);
            _logger.LogInformation("Removed disk plugin logo {LogoPath} so updates do not file-lock.", diskLogo);
        }
        catch (IOException ex)
        {
            _logger.LogDebug(ex, "FinTV could not delete disk plugin logo {LogoPath}; it may be in use.", diskLogo);
        }
    }
}
