namespace FinTv;

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

    public static string? FromConfiguration(IConfiguration configuration, string name)
        => configuration["CHANNELFLOW_" + name] ?? configuration["FINTV_" + name] ?? Get(name);
}
