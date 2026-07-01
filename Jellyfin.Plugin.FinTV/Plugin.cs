using Jellyfin.Plugin.FinTV.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.FinTV;

public class Plugin : BasePlugin<PluginConfiguration>
{
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public static Plugin? Instance { get; private set; }

    public override string Name => "FinTV";

    public override Guid Id => Guid.Parse("f4e8a2b1-3c5d-4e6f-9a8b-7c6d5e4f3a2b");

    public string DataFolder => Path.Combine(ApplicationPaths.PluginConfigurationsPath, "FinTV");

    public string DatabasePath => Path.Combine(DataFolder, "fintv.db");

    public string LogosFolder => Path.Combine(DataFolder, "logos");

    public string WeatherStarFolder => Path.Combine(DataFolder, "weatherstar");
}
