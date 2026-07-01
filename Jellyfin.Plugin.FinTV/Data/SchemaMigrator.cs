using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.FinTV.Data;

internal static class SchemaMigrator
{
    private const string MigrationKey = "channel-number-decimal";

    public static async Task MigrateAsync(FinTvDbContext db, ILogger logger, CancellationToken cancellationToken)
    {
        await db.Database.EnsureCreatedAsync(cancellationToken);

        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS "__FinTvSchema" (
                "Key" TEXT NOT NULL PRIMARY KEY,
                "AppliedAt" TEXT NOT NULL
            );
            """,
            cancellationToken);

        var applied = await db.Database
            .SqlQueryRaw<long>("SELECT COUNT(*) AS \"Value\" FROM \"__FinTvSchema\" WHERE \"Key\" = {0}", MigrationKey)
            .FirstAsync(cancellationToken);

        if (applied > 0)
        {
            return;
        }

        var tableExists = await db.Database
            .SqlQueryRaw<long>("SELECT COUNT(*) AS \"Value\" FROM sqlite_master WHERE type = 'table' AND name = 'Channels'")
            .FirstAsync(cancellationToken);

        if (tableExists == 0)
        {
            await MarkMigrationAppliedAsync(db, cancellationToken);
            return;
        }

        var numberType = await db.Database
            .SqlQueryRaw<string>("SELECT type AS \"Value\" FROM pragma_table_info('Channels') WHERE name = 'Number'")
            .FirstOrDefaultAsync(cancellationToken);

        if (IsDecimalColumnType(numberType))
        {
            await MarkMigrationAppliedAsync(db, cancellationToken);
            return;
        }

        logger.LogInformation("Migrating Channels.Number to REAL for sub-channel numbers");

        await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"Channels\" ADD COLUMN \"NumberReal\" REAL NOT NULL DEFAULT 1", cancellationToken);
        await db.Database.ExecuteSqlRawAsync("UPDATE \"Channels\" SET \"NumberReal\" = \"Number\"", cancellationToken);
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"Channels\" DROP COLUMN \"Number\"", cancellationToken);
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"Channels\" RENAME COLUMN \"NumberReal\" TO \"Number\"", cancellationToken);

        await MarkMigrationAppliedAsync(db, cancellationToken);
    }

    private static bool IsDecimalColumnType(string? columnType)
    {
        return columnType is not null
            && (columnType.Contains("REAL", StringComparison.OrdinalIgnoreCase)
                || columnType.Contains("NUMERIC", StringComparison.OrdinalIgnoreCase)
                || columnType.Contains("DECIMAL", StringComparison.OrdinalIgnoreCase));
    }

    private static Task MarkMigrationAppliedAsync(FinTvDbContext db, CancellationToken cancellationToken)
    {
        return db.Database.ExecuteSqlRawAsync(
            """
            INSERT OR IGNORE INTO "__FinTvSchema" ("Key", "AppliedAt")
            VALUES ({0}, {1});
            """,
            MigrationKey,
            DateTime.UtcNow.ToString("O"));
    }
}
