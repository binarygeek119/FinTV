using Microsoft.EntityFrameworkCore;

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
        for (var attempt = 0; attempt < 8; attempt++)
        {
            try
            {
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (DbUpdateConcurrencyException ex) when (attempt < 7)
            {
                var recovered = 0;
                var entries = ex.Entries.Count > 0
                    ? ex.Entries.ToList()
                    : db.ChangeTracker.Entries()
                        .Where(entry => entry.State is EntityState.Deleted or EntityState.Modified)
                        .ToList();

                foreach (var entry in entries)
                {
                    recovered += await RecoverGoneRowAsync(entry, cancellationToken).ConfigureAwait(false);
                }

                if (recovered == 0)
                {
                    foreach (var entry in db.ChangeTracker.Entries()
                        .Where(candidate => candidate.State == EntityState.Deleted)
                        .ToList())
                    {
                        entry.State = EntityState.Detached;
                        recovered++;
                    }
                }

                if (recovered == 0)
                {
                    throw;
                }
            }
        }
    }

    private static async Task<int> RecoverGoneRowAsync(
        Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry entry,
        CancellationToken cancellationToken)
    {
        if (entry.State == EntityState.Deleted)
        {
            entry.State = EntityState.Detached;
            return 1;
        }

        if (entry.State == EntityState.Modified)
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

            return 1;
        }

        try
        {
            await entry.ReloadAsync(cancellationToken).ConfigureAwait(false);
            return 1;
        }
        catch (Exception)
        {
            entry.State = EntityState.Detached;
            return 1;
        }
    }
}
