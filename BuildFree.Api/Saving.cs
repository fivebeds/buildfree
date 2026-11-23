using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BuildFree.Api;

public static class Saving
{
    // Estimates, change orders and invoices are numbered per company: the next
    // number is the highest one so far plus one. If two people create one at
    // the same moment, both pick the same number and the database's unique
    // index refuses the second save. This picks the next number again and
    // retries, so neither person sees an error.
    public static async Task SaveNumbered(AppDbContext db, Func<Task<int>> nextNumber, Action<int> setNumber)
    {
        for (var attempt = 1; ; attempt++)
        {
            setNumber(await nextNumber());
            try
            {
                await db.SaveChangesAsync();
                return;
            }
            catch (DbUpdateException ex) when (attempt < 10 &&
                ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                await Task.Delay(Random.Shared.Next(5, 40));
            }
        }
    }
}
