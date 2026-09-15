using PSR.Service.Api.Common;
using Microsoft.EntityFrameworkCore;
using PSR.Service.Api.Data;

namespace PSR.Service.Api.Stock;

/// <summary>
/// Atomic document numbers. Must be called inside a transaction — it locks the sequence row
/// (SELECT ... FOR UPDATE) so concurrent callers serialize and never collide.
/// </summary>
public class NumberSequenceService(AppDbContext db)
{
    public async Task<string> NextAsync(string key, CancellationToken ct)
    {
        // The FOR UPDATE below is the entire concurrency guarantee, and it only holds for as long as
        // the enclosing transaction does. Called outside one, MySQL autocommits the SELECT, the lock is
        // released before the row is written back, and two people saving at the same moment are handed
        // the same number — which then fails on the unique index, or worse, does not.
        //
        // Nothing in the type system says "call me in a transaction", so it is asserted here. Every
        // current caller already opens one; this is what stops the next one from quietly not.
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException(
                $"NextAsync('{key}') was called outside a transaction. The sequence row lock only holds "
                + "inside one, so the number it returns would not be unique under concurrent use. "
                + "Wrap the call in db.Database.BeginTransactionAsync().");

        var row = await db.NumberSequences
            .FromSqlInterpolated($"SELECT * FROM `number_sequences` WHERE `key` = {key} FOR UPDATE")
            .FirstOrDefaultAsync(ct)
            ?? throw new StockException($"Number sequence '{key}' is not configured.");

        // Year-scoped sequences (PI / Invoice / DC) format as PREFIX-YYYY-NNNN and reset every January.
        if (row.Year is not null)
        {
            // The shop's year. On UtcNow it stays last year until 05:30 IST on 1 January, so a
            // document raised that morning got last year's number and then reset the counter to 1
            // when the year finally rolled — two documents, same number, five hours apart.
            var year = ShopClock.Now.Year;
            if (row.Year != year) { row.Year = year; row.NextValue = 1; }
            var v = row.NextValue;
            row.NextValue = v + 1;
            await db.SaveChangesAsync(ct);
            return $"{row.Prefix}-{row.Year}-{v:D4}";
        }

        var value = row.NextValue;
        row.NextValue = value + 1;
        await db.SaveChangesAsync(ct);

        return $"{row.Prefix}{value:D5}";
    }
}
