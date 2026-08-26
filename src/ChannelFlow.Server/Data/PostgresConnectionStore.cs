using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;

namespace FinTv.Data;

/// <summary>
/// PostgreSQL connection for ChannelFlow. Docker can still set POSTGRES_* env vars;
/// otherwise first-run saves <c>database.json</c> under the config folder from the web UI.
/// </summary>
public sealed class PostgresConnectionStore
{
    public const string FileName = "database.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _filePath;
    private readonly object _gate = new();
    private PostgresSettings? _fileSettings;

    public PostgresConnectionStore(IWebHostEnvironment env, IConfiguration config)
    {
        var configDir = AppEnvironment.FromConfiguration(config, "CONFIG")
            ?? Path.Combine(env.ContentRootPath, "config");
        Directory.CreateDirectory(configDir);
        _filePath = Path.Combine(configDir, FileName);
        _fileSettings = TryReadFile();
    }

    public string FilePath => _filePath;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(GetConnectionString());

    public bool FromEnvironment => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("POSTGRES_HOST"));

    public PostgresSettings? GetPublicSettings()
    {
        if (FromEnvironment)
        {
            return new PostgresSettings
            {
                Host = Environment.GetEnvironmentVariable("POSTGRES_HOST") ?? "127.0.0.1",
                Port = int.TryParse(Environment.GetEnvironmentVariable("POSTGRES_PORT"), out var port) ? port : 5432,
                Database = Environment.GetEnvironmentVariable("POSTGRES_DB") ?? "channelflow",
                Username = Environment.GetEnvironmentVariable("POSTGRES_USER") ?? "channelflow"
            };
        }

        lock (_gate)
        {
            var settings = _fileSettings ?? TryReadFile();
            if (settings is null)
            {
                return null;
            }

            return settings with { Password = null };
        }
    }

    public string? GetConnectionString()
    {
        if (FromEnvironment)
        {
            return BuildConnectionString(FromEnv());
        }

        lock (_gate)
        {
            var settings = _fileSettings ?? TryReadFile();
            return settings is null ? null : BuildConnectionString(settings);
        }
    }

    public async Task SaveAndVerifyAsync(PostgresSettings request, CancellationToken cancellationToken)
    {
        if (FromEnvironment)
        {
            var connectionString = GetConnectionString();
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new InvalidOperationException("POSTGRES_HOST is set but the connection is incomplete.");
            }

            try
            {
                await OpenAsync(connectionString, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new InvalidOperationException(
                    "Could not reach PostgreSQL using POSTGRES_* environment variables. " + ex.Message,
                    ex);
            }

            return;
        }

        if (string.IsNullOrWhiteSpace(request.Password))
        {
            PostgresSettings? existing;
            lock (_gate)
            {
                existing = _fileSettings ?? TryReadFile();
            }

            if (existing is not null && !string.IsNullOrWhiteSpace(existing.Password))
            {
                request = request with { Password = existing.Password };
            }
        }

        var settings = Normalize(request);
        await VerifyOrCreateDatabaseAsync(settings, cancellationToken).ConfigureAwait(false);
        WriteFile(settings);
        lock (_gate)
        {
            _fileSettings = settings;
        }
    }

    private static PostgresSettings FromEnv()
        => new()
        {
            Host = Environment.GetEnvironmentVariable("POSTGRES_HOST") ?? "127.0.0.1",
            Port = int.TryParse(Environment.GetEnvironmentVariable("POSTGRES_PORT"), out var port) ? port : 5432,
            Database = Environment.GetEnvironmentVariable("POSTGRES_DB") ?? "channelflow",
            Username = Environment.GetEnvironmentVariable("POSTGRES_USER") ?? "channelflow",
            Password = Environment.GetEnvironmentVariable("POSTGRES_PASSWORD") ?? ""
        };

    private static PostgresSettings Normalize(PostgresSettings request)
    {
        var host = string.IsNullOrWhiteSpace(request.Host) ? "127.0.0.1" : request.Host.Trim();
        var database = string.IsNullOrWhiteSpace(request.Database) ? "channelflow" : request.Database.Trim();
        var username = string.IsNullOrWhiteSpace(request.Username) ? "channelflow" : request.Username.Trim();
        var port = request.Port is > 0 and < 65536 ? request.Port.Value : 5432;
        if (!IsSafeName(database))
        {
            throw new InvalidOperationException("Database name may only contain letters, numbers, and underscores.");
        }

        if (!IsSafeName(username))
        {
            throw new InvalidOperationException("Username may only contain letters, numbers, and underscores.");
        }

        if (string.IsNullOrWhiteSpace(request.Password))
        {
            throw new InvalidOperationException("Password is required.");
        }

        return new PostgresSettings
        {
            Host = host,
            Port = port,
            Database = database,
            Username = username,
            Password = request.Password
        };
    }

    private static async Task VerifyOrCreateDatabaseAsync(PostgresSettings settings, CancellationToken cancellationToken)
    {
        try
        {
            await OpenAsync(BuildConnectionString(settings), cancellationToken).ConfigureAwait(false);
            return;
        }
        catch (PostgresException ex) when (ex.SqlState == "3D000")
        {
            await CreateDatabaseAsync(settings, cancellationToken).ConfigureAwait(false);
            await OpenAsync(BuildConnectionString(settings), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
        {
            throw new InvalidOperationException(
                "Could not reach PostgreSQL at "
                + settings.Host
                + ":"
                + settings.Port
                + ". Check host, port, username, and password. "
                + ex.Message,
                ex);
        }
    }

    private static async Task CreateDatabaseAsync(PostgresSettings settings, CancellationToken cancellationToken)
    {
        var maintenance = settings with { Database = "postgres" };
        try
        {
            await using var conn = new NpgsqlConnection(BuildConnectionString(maintenance));
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var cmd = new NpgsqlCommand(
                "CREATE DATABASE " + QuoteIdent(settings.Database!),
                conn);
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException ex) when (ex.SqlState == "42P04")
        {
            // already exists
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                "Connected, but database '"
                + settings.Database
                + "' does not exist and could not be created. Create it in PostgreSQL, then try again. "
                + ex.Message,
                ex);
        }
    }

    private static async Task OpenAsync(string connectionString, CancellationToken cancellationToken)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand("SELECT 1", conn);
        await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    private PostgresSettings? TryReadFile()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return null;
            }

            var settings = JsonSerializer.Deserialize<PostgresSettings>(File.ReadAllText(_filePath), JsonOptions);
            if (settings is null
                || string.IsNullOrWhiteSpace(settings.Host)
                || string.IsNullOrWhiteSpace(settings.Database)
                || string.IsNullOrWhiteSpace(settings.Username)
                || string.IsNullOrWhiteSpace(settings.Password))
            {
                return null;
            }

            return settings;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void WriteFile(PostgresSettings settings)
    {
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        var temp = _filePath + ".tmp";
        File.WriteAllText(temp, json);
        File.Move(temp, _filePath, overwrite: true);
        TryRestrict(_filePath);
    }

    private static void TryRestrict(string path)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception)
        {
            // Best-effort.
        }
    }

    public static string BuildConnectionString(PostgresSettings settings)
        => new NpgsqlConnectionStringBuilder
        {
            Host = settings.Host,
            Port = settings.Port ?? 5432,
            Database = settings.Database,
            Username = settings.Username,
            Password = settings.Password ?? "",
            Timeout = 8,
            CommandTimeout = 30
        }.ConnectionString;

    private static bool IsSafeName(string value)
        => value.Length is > 0 and <= 63 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');

    private static string QuoteIdent(string value)
        => "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}

public sealed record PostgresSettings
{
    public string? Host { get; init; }

    public int? Port { get; init; }

    public string? Database { get; init; }

    public string? Username { get; init; }

    public string? Password { get; init; }
}
