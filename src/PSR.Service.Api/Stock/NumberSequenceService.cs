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
        var row = await db.NumberSequences
            .FromSqlInterpolated($"SELECT * FROM `number_sequences` WHERE `key` = {key} FOR UPDATE")
            .FirstOrDefaultAsync(ct)
            ?? throw new StockException($"Number sequence '{key}' is not configured.");

        // Year-scoped sequences (PI / Invoice / DC) format as PREFIX-YYYY-NNNN and reset every January.
        if (row.Year is not null)
        {
            var year = DateTime.UtcNow.Year;
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
