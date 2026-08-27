using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging;

namespace FinTv.Data;

public static class DbContextSaveExtensions
{
    /// <summary>
    /// Saves changes, treating already-deleted rows as success instead of throwing
    /// <see cref="DbUpdateConcurrencyException"/>. Missing updates are re-inserted.
    /// </summary>
    public static async Task SaveChangesIgnoringGoneRowsAsync(
        this DbContext db,
        CancellationToken cancellationToken)
    {
        await RebaseGoneRowsAsync(db, cancellationToken).ConfigureAwait(false);

        for (var attempt = 0; attempt < 8; attempt++)
        {
            try
            {
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (DbUpdateConcurrencyException ex) when (attempt < 7)
            {
                db.GetService<ILoggerFactory>()
                    ?.CreateLogger("FinTv.Data.DbContextSaveExtensions")
                    .LogWarning(
                        "Catalog save hit a gone row (attempt {Attempt}, reported {Reported}). Re-inserting instead of UPDATE/DELETE.",
                        attempt + 1,
                        ex.Entries.Count);

                var recovered = RecoverFailedUpdatesAsInserts(db, ex);
                if (recovered == 0)
                {
                    throw;
                }
            }
        }
    }

    private static async Task RebaseGoneRowsAsync(DbContext db, CancellationToken cancellationToken)
    {
        foreach (var entry in db.ChangeTracker.Entries()
            .Where(candidate => candidate.State == EntityState.Deleted)
            .ToList())
        {
            entry.State = EntityState.Detached;
        }

        foreach (var entry in db.ChangeTracker.Entries()
            .Where(candidate => candidate.State == EntityState.Modified)
            .ToList())
        {
            var databaseValues = await entry.GetDatabaseValuesAsync(cancellationToken).ConfigureAwait(false);
            if (databaseValues is null)
            {
                entry.State = EntityState.Added;
            }
            else
            {
                entry.OriginalValues.SetValues(databaseValues);
            }
        }
    }

    private static int RecoverFailedUpdatesAsInserts(DbContext db, DbUpdateConcurrencyException ex)
    {
        var recovered = 0;
        foreach (var entry in db.ChangeTracker.Entries()
            .Where(candidate => candidate.State == EntityState.Deleted)
            .ToList())
        {
            entry.State = EntityState.Detached;
            recovered++;
        }

        var modified = ex.Entries
            .Where(entry => entry.State == EntityState.Modified)
            .ToList();
        if (modified.Count == 0)
        {
            modified = db.ChangeTracker.Entries()
                .Where(candidate => candidate.State == EntityState.Modified)
                .ToList();
        }

        foreach (var entry in modified)
        {
            if (entry.State != EntityState.Modified)
            {
                continue;
            }

            entry.State = EntityState.Added;
            recovered++;
        }

        return recovered;
    }
}
