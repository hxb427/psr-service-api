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

        var value = row.NextValue;
        row.NextValue = value + 1;
        await db.SaveChangesAsync(ct);

        return $"{row.Prefix}{value:D5}";
    }
}
