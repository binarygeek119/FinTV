namespace Jellyfin.Plugin.FinTV;

/// <summary>
/// Reads CHANNELFLOW_* environment variables, falling back to legacy FINTV_* names.
/// </summary>
internal static class AppEnvironment
{
    public static string? Get(string name)
    {
        foreach (var prefix in new[] { "CHANNELFLOW_", "FINTV_" })
        {
            var value = Environment.GetEnvironmentVariable(prefix + name);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }
}
