using System.Data;
using Microsoft.EntityFrameworkCore;

namespace FinTv.Data;

internal static class CatalogSchema
{
    public static async Task EnsureEpisodesTableAsync(FinTvDbContext db, CancellationToken cancellationToken)
    {
        await ExecuteAsync(
            db,
            """CREATE TABLE IF NOT EXISTS "Episodes" (LIKE "TvShows" INCLUDING DEFAULTS)""",
            cancellationToken);

        var statements = new[]
        {
            """ALTER TABLE "Episodes" ADD COLUMN IF NOT EXISTS "SeriesId" uuid NULL""",
            """ALTER TABLE "Episodes" ADD COLUMN IF NOT EXISTS "SeriesName" text NULL""",
            """ALTER TABLE "Episodes" ADD COLUMN IF NOT EXISTS "SeasonId" uuid NULL""",
            """ALTER TABLE "Episodes" ADD COLUMN IF NOT EXISTS "SeasonName" text NULL""",
            """ALTER TABLE "Episodes" ADD COLUMN IF NOT EXISTS "SeasonNumber" integer NULL""",
            """ALTER TABLE "Episodes" ADD COLUMN IF NOT EXISTS "EpisodeNumber" integer NULL""",
            """CREATE INDEX IF NOT EXISTS "IX_Episodes_Name" ON "Episodes" ("Name")""",
            """CREATE INDEX IF NOT EXISTS "IX_Episodes_LibraryId" ON "Episodes" ("LibraryId")""",
            """CREATE INDEX IF NOT EXISTS "IX_Episodes_JellyfinItemId" ON "Episodes" ("JellyfinItemId")""",
            """CREATE INDEX IF NOT EXISTS "IX_Episodes_SeriesId" ON "Episodes" ("SeriesId")""",
            """ALTER TABLE "Episodes" DROP COLUMN IF EXISTS "IsSeries" """,
            """
            DO $$
            BEGIN
                IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'PK_Episodes') THEN
                    ALTER TABLE "Episodes" ADD CONSTRAINT "PK_Episodes" PRIMARY KEY ("Id");
                END IF;
            END $$;
            """
        };

        foreach (var sql in statements)
        {
            await ExecuteAsync(db, sql, cancellationToken);
        }

        if (!await TableExistsAsync(db, cancellationToken))
        {
            throw new InvalidOperationException("Catalog upgrade failed: relation \"Episodes\" was not created.");
        }
    }

    public static async Task ExecuteAsync(FinTvDbContext db, string sql, CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        var shouldClose = connection.State != ConnectionState.Open;
        if (shouldClose)
        {
            await db.Database.OpenConnectionAsync(cancellationToken);
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            if (shouldClose)
            {
                await db.Database.CloseConnectionAsync();
            }
        }
    }

    public static async Task<bool> TableExistsAsync(FinTvDbContext db, CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        var shouldClose = connection.State != ConnectionState.Open;
        if (shouldClose)
        {
            await db.Database.OpenConnectionAsync(cancellationToken);
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """SELECT to_regclass('"Episodes"') IS NOT NULL""";
            var result = await command.ExecuteScalarAsync(cancellationToken);
            return result is true || result is bool flag && flag;
        }
        finally
        {
            if (shouldClose)
            {
                await db.Database.CloseConnectionAsync();
            }
        }
    }
}
