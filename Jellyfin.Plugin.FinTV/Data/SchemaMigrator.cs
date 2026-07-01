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

        await MigrateChannelNumberColumnAsync(db, logger, cancellationToken);
        await MarkMigrationAppliedAsync(db, cancellationToken);
    }

    private static async Task MigrateChannelNumberColumnAsync(
        FinTvDbContext db,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var numberType = await GetColumnTypeAsync(db, "Channels", "Number", cancellationToken);
        var hasNumber = await ColumnExistsAsync(db, "Channels", "Number", cancellationToken);
        var hasNumberReal = await ColumnExistsAsync(db, "Channels", "NumberReal", cancellationToken);

        if (hasNumber && IsDecimalColumnType(numberType))
        {
            await EnsureChannelNumberIndexAsync(db, cancellationToken);
            return;
        }

        if (hasNumberReal)
        {
            logger.LogInformation("Resuming Channels.Number decimal migration");
            await DropChannelNumberIndexAsync(db, cancellationToken);

            if (hasNumber)
            {
                await db.Database.ExecuteSqlRawAsync(
                    "UPDATE \"Channels\" SET \"NumberReal\" = CAST(\"Number\" AS REAL)",
                    cancellationToken);
                await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"Channels\" DROP COLUMN \"Number\"", cancellationToken);
            }

            if (await ColumnExistsAsync(db, "Channels", "NumberReal", cancellationToken))
            {
                await db.Database.ExecuteSqlRawAsync(
                    "ALTER TABLE \"Channels\" RENAME COLUMN \"NumberReal\" TO \"Number\"",
                    cancellationToken);
            }

            await EnsureChannelNumberIndexAsync(db, cancellationToken);
            return;
        }

        if (!hasNumber)
        {
            return;
        }

        logger.LogInformation("Migrating Channels.Number to REAL for sub-channel numbers");

        await DropChannelNumberIndexAsync(db, cancellationToken);
        await db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE \"Channels\" ADD COLUMN \"NumberReal\" REAL NOT NULL DEFAULT 1",
            cancellationToken);
        await db.Database.ExecuteSqlRawAsync(
            "UPDATE \"Channels\" SET \"NumberReal\" = CAST(\"Number\" AS REAL)",
            cancellationToken);
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"Channels\" DROP COLUMN \"Number\"", cancellationToken);
        await db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE \"Channels\" RENAME COLUMN \"NumberReal\" TO \"Number\"",
            cancellationToken);
        await EnsureChannelNumberIndexAsync(db, cancellationToken);
    }

    private static Task DropChannelNumberIndexAsync(FinTvDbContext db, CancellationToken cancellationToken)
    {
        return db.Database.ExecuteSqlRawAsync(
            """
            DROP INDEX IF EXISTS "IX_Channels_Number";
            """,
            cancellationToken);
    }

    private static Task EnsureChannelNumberIndexAsync(FinTvDbContext db, CancellationToken cancellationToken)
    {
        return db.Database.ExecuteSqlRawAsync(
            """
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_Channels_Number" ON "Channels" ("Number");
            """,
            cancellationToken);
    }

    private static async Task<bool> ColumnExistsAsync(
        FinTvDbContext db,
        string table,
        string column,
        CancellationToken cancellationToken)
    {
        var count = await db.Database
            .SqlQueryRaw<long>(
                "SELECT COUNT(*) AS \"Value\" FROM pragma_table_info({0}) WHERE name = {1}",
                table,
                column)
            .FirstAsync(cancellationToken);

        return count > 0;
    }

    private static async Task<string?> GetColumnTypeAsync(
        FinTvDbContext db,
        string table,
        string column,
        CancellationToken cancellationToken)
    {
        if (!await ColumnExistsAsync(db, table, column, cancellationToken))
        {
            return null;
        }

        return await db.Database
            .SqlQueryRaw<string>(
                "SELECT type AS \"Value\" FROM pragma_table_info({0}) WHERE name = {1} LIMIT 1",
                table,
                column)
            .FirstAsync(cancellationToken);
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
            new object[] { MigrationKey, DateTime.UtcNow.ToString("O") },
            cancellationToken);
    }
}
