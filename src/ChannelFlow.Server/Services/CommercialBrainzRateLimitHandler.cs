using System.Net;
using Microsoft.Extensions.Logging;

namespace FinTv.Services;

/// <summary>
/// Serializes CommercialBrainz API calls and backs off on HTTP 429 so playlist pulls
/// and playout builds cannot stampede the public API.
/// </summary>
public sealed class CommercialBrainzRateLimitHandler : DelegatingHandler
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly TimeSpan BaselineInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxInterval = TimeSpan.FromSeconds(10);
    private static TimeSpan _minInterval = BaselineInterval;
    private static DateTimeOffset _nextAllowedUtc = DateTimeOffset.MinValue;

    private readonly ILogger<CommercialBrainzRateLimitHandler> _logger;

    public CommercialBrainzRateLimitHandler(ILogger<CommercialBrainzRateLimitHandler> logger)
    {
        _logger = logger;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (!IsCommercialBrainzApi(request.RequestUri))
        {
            return await base.SendAsync(request, cancellationToken);
        }

        await Gate.WaitAsync(cancellationToken);
        try
        {
            HttpResponseMessage? last = null;
            for (var attempt = 0; attempt < 8; attempt++)
            {
                var pause = _nextAllowedUtc - DateTimeOffset.UtcNow;
                if (pause > TimeSpan.Zero)
                {
                    await Task.Delay(pause, cancellationToken);
                }

                last?.Dispose();
                last = await base.SendAsync(Clone(request), cancellationToken);
                if (last.StatusCode != HttpStatusCode.TooManyRequests)
                {
                    if (_minInterval > BaselineInterval)
                    {
                        _minInterval = TimeSpan.FromMilliseconds(
                            Math.Max(BaselineInterval.TotalMilliseconds, _minInterval.TotalMilliseconds * 0.75));
                    }

                    _nextAllowedUtc = DateTimeOffset.UtcNow + _minInterval;
                    return last;
                }

                var wait = ParseRetryAfter(last) ?? TimeSpan.FromSeconds(Math.Min(45, 5 * Math.Pow(2, attempt)));
                if (wait < TimeSpan.FromSeconds(5))
                {
                    wait = TimeSpan.FromSeconds(5);
                }

                _minInterval = TimeSpan.FromSeconds(Math.Min(MaxInterval.TotalSeconds, Math.Max(_minInterval.TotalSeconds, 3)));
                _nextAllowedUtc = DateTimeOffset.UtcNow + wait;
                _logger.LogWarning(
                    "CommercialBrainz rate-limited {Url}; waiting {Delay}s",
                    request.RequestUri,
                    wait.TotalSeconds);
                await Task.Delay(wait, cancellationToken);
            }

            return last ?? new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        }
        finally
        {
            Gate.Release();
        }
    }

    private static bool IsCommercialBrainzApi(Uri? uri)
        => uri is not null
           && uri.Host.Contains("commercialbrainz", StringComparison.OrdinalIgnoreCase);

    private static TimeSpan? ParseRetryAfter(HttpResponseMessage response)
    {
        var retry = response.Headers.RetryAfter;
        if (retry is null)
        {
            return null;
        }

        if (retry.Delta is TimeSpan delta)
        {
            return delta < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : delta;
        }

        if (retry.Date is DateTimeOffset when)
        {
            var wait = when - DateTimeOffset.UtcNow;
            return wait > TimeSpan.Zero ? wait : TimeSpan.FromSeconds(1);
        }

        return null;
    }

    private static HttpRequestMessage Clone(HttpRequestMessage request)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri);
        foreach (var header in request.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        return clone;
    }
}
