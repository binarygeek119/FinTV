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
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (DbUpdateConcurrencyException ex) when (attempt < 4)
            {
                foreach (var entry in ex.Entries)
                {
                    if (entry.State == EntityState.Deleted)
                    {
                        entry.State = EntityState.Detached;
                        continue;
                    }

                    if (entry.State == EntityState.Modified)
                    {
                        var databaseValues = await entry.GetDatabaseValuesAsync(cancellationToken)
                            .ConfigureAwait(false);
                        if (databaseValues is null)
                        {
                            entry.State = EntityState.Added;
                        }
                        else
                        {
                            entry.OriginalValues.SetValues(databaseValues);
                        }

                        continue;
                    }

                    try
                    {
                        await entry.ReloadAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                        entry.State = EntityState.Detached;
                    }
                }
            }
        }
    }
}
