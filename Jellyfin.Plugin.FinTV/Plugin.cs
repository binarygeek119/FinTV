using Jellyfin.Plugin.FinTV.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.FinTV;

/// <summary>
/// FinTV Jellyfin plugin entry point.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Application paths.</param>
    /// <param name="xmlSerializer">XML serializer.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <summary>
    /// Gets the active plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => "FinTV";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("f4e8a2b1-3c5d-4e6f-9a8b-7c6d5e4f3a2b");

    /// <summary>
    /// Gets the plugin data folder path.
    /// </summary>
    public string DataFolder => Path.Combine(ApplicationPaths.PluginConfigurationsPath, "FinTV");

    /// <summary>
    /// Gets the SQLite database file path.
    /// </summary>
    public string DatabasePath => Path.Combine(DataFolder, "fintv.db");

    /// <summary>
    /// Gets the cached logo storage folder.
    /// </summary>
    public string LogosFolder => Path.Combine(DataFolder, "logos");

    /// <summary>
    /// Gets the WeatherStar asset folder.
    /// </summary>
    public string WeatherStarFolder => Path.Combine(DataFolder, "weatherstar");
}
