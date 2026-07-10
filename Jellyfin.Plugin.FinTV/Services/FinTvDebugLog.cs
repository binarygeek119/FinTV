using System.Globalization;
using System.Text;
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

        return string.Format(CultureInfo.InvariantCulture, NormalizeNamedPlaceholders(message), args);
    }

    private static string NormalizeNamedPlaceholders(string message)
    {
        var result = new StringBuilder(message.Length);
        var argIndex = 0;

        for (var i = 0; i < message.Length; i++)
        {
            if (message[i] != '{')
            {
                result.Append(message[i]);
                continue;
            }

            var close = message.IndexOf('}', i + 1);
            if (close < 0)
            {
                result.Append(message[i]);
                continue;
            }

            var token = message.Substring(i + 1, close - i - 1);
            if (token.Length > 0 && (char.IsDigit(token[0]) || token[0] == ','))
            {
                result.Append('{').Append(token).Append('}');
            }
            else
            {
                result.Append('{').Append(argIndex++).Append('}');
            }

            i = close;
        }

        return result.ToString();
    }
}
