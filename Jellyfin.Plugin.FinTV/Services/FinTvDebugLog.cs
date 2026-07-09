using System.Globalization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.FinTV.Services;

/// <summary>
/// Gates verbose developer logging behind the FinTV debug setting.
/// </summary>
public static class FinTvDebugLog
{
    private const string AiLogTemplate = "[FinTV AI] {Detail}";

    /// <summary>
    /// Gets whether verbose FinTV debug logging is enabled.
    /// </summary>
    public static bool IsEnabled => Plugin.Instance?.Configuration.DebugLogging == true;

    /// <summary>
    /// Writes an AI pipeline log line when debug logging is enabled.
    /// </summary>
    public static void Ai(ILogger logger, string message, params object[] args)
    {
        if (!IsEnabled)
        {
            return;
        }

        logger.LogInformation(AiLogTemplate, FormatDetail(message, args));
    }

    /// <summary>
    /// Writes an AI pipeline log line with exception details when debug logging is enabled.
    /// </summary>
    public static void Ai(ILogger logger, Exception ex, string message, params object[] args)
    {
        if (!IsEnabled)
        {
            return;
        }

        logger.LogInformation(ex, AiLogTemplate, FormatDetail(message, args));
    }

    private static string FormatDetail(string message, object[] args)
    {
        if (args.Length == 0)
        {
            return message;
        }

        return string.Format(CultureInfo.InvariantCulture, message, args);
    }
}
