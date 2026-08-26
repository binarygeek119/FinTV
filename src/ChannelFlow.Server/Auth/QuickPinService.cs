using System.Security.Cryptography;
using System.Text;

namespace FinTv.Auth;

/// <summary>
/// Short-lived PIN so another app can fetch M3U/XMLTV links without copying the API key.
/// </summary>
public sealed class QuickPinService
{
    public const int LifetimeSeconds = 300;
    public const int MaxFailedAttempts = 8;
    public const int PinLength = 6;

    private readonly object _gate = new();
    private string? _pin;
    private DateTimeOffset _expiresAt;
    private int _failures;

    public QuickPinSnapshot Create()
    {
        lock (_gate)
        {
            _pin = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
            _expiresAt = DateTimeOffset.UtcNow.AddSeconds(LifetimeSeconds);
            _failures = 0;
            return SnapshotUnlocked(includePin: true);
        }
    }

    public QuickPinSnapshot Snapshot(bool includePin)
    {
        lock (_gate)
        {
            return SnapshotUnlocked(includePin);
        }
    }

    public bool TryAccept(string? pin, out string error)
    {
        var normalized = Normalize(pin);
        lock (_gate)
        {
            if (!IsActiveUnlocked())
            {
                ClearUnlocked();
                error = "expired";
                return false;
            }

            if (normalized.Length != PinLength
                || !CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(normalized),
                    Encoding.ASCII.GetBytes(_pin!)))
            {
                _failures++;
                if (_failures >= MaxFailedAttempts)
                {
                    ClearUnlocked();
                    error = "expired";
                    return false;
                }

                error = "invalid";
                return false;
            }

            error = string.Empty;
            return true;
        }
    }

    private QuickPinSnapshot SnapshotUnlocked(bool includePin)
    {
        if (!IsActiveUnlocked())
        {
            ClearUnlocked();
            return QuickPinSnapshot.Inactive();
        }

        var remaining = (int)Math.Ceiling((_expiresAt - DateTimeOffset.UtcNow).TotalSeconds);
        return new QuickPinSnapshot(
            Active: true,
            Pin: includePin ? _pin : null,
            ExpiresAt: _expiresAt,
            ExpiresInSeconds: Math.Max(0, remaining));
    }

    private bool IsActiveUnlocked()
        => !string.IsNullOrEmpty(_pin) && DateTimeOffset.UtcNow < _expiresAt;

    private void ClearUnlocked()
    {
        _pin = null;
        _expiresAt = default;
        _failures = 0;
    }

    internal static string Normalize(string? pin)
    {
        if (string.IsNullOrWhiteSpace(pin))
        {
            return string.Empty;
        }

        var chars = pin.Where(char.IsDigit).ToArray();
        return new string(chars);
    }
}

public readonly record struct QuickPinSnapshot(
    bool Active,
    string? Pin,
    DateTimeOffset? ExpiresAt,
    int ExpiresInSeconds)
{
    public static QuickPinSnapshot Inactive()
        => new(false, null, null, 0);
}
