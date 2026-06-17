using Microsoft.EntityFrameworkCore;
using PSR.Service.Api.Data;
using PSR.Service.Api.Data.Entities;

namespace PSR.Service.Api.Stock;

/// <summary>
/// Writes the append-only ledger and updates the balance cache atomically. Balance changes use
/// raw guarded SQL (INSERT ... ON DUPLICATE KEY UPDATE for increments; a conditional UPDATE for
/// decrements) so there are no read-modify-write races and stock can never go negative.
/// Call these inside a transaction; the movement row is added to the context and saved by the caller.
/// </summary>
public class StockLedgerService(AppDbContext db)
{
    public async Task ReceiptAsync(long partId, int qty, long byUser, string? remarks, CancellationToken ct)
    {
        await IncrementAsync(partId, StockBalance.Warehouse, qty, ct);
        db.StockMovements.Add(new StockMovement
        {
            PartId = partId, MovementType = MovementType.Receipt, Quantity = qty,
            PerformedByUserId = byUser, ReferenceType = "MANUAL", Remarks = remarks,
        });
    }

    public async Task AdjustAsync(long partId, int delta, long byUser, string? remarks, CancellationToken ct)
    {
        if (delta == 0) throw new StockException("Adjustment delta cannot be zero.");
        if (delta > 0)
            await IncrementAsync(partId, StockBalance.Warehouse, delta, ct);
        else if (!await GuardedDecrementAsync(partId, StockBalance.Warehouse, -delta, ct))
            throw new StockException("Adjustment would make warehouse stock negative.");

        db.StockMovements.Add(new StockMovement
        {
            PartId = partId, MovementType = MovementType.Adjustment, Quantity = delta,
            PerformedByUserId = byUser, ReferenceType = "MANUAL", Remarks = remarks,
        });
    }

    public async Task IssueAsync(long partId, long technicianId, int qty, long byUser, string referenceType, long referenceId, CancellationToken ct)
    {
        if (!await GuardedDecrementAsync(partId, StockBalance.Warehouse, qty, ct))
            throw new StockException("Insufficient warehouse stock to issue.");
        await IncrementAsync(partId, technicianId, qty, ct);
        db.StockMovements.Add(new StockMovement
        {
            PartId = partId, MovementType = MovementType.Issue, Quantity = qty, TechnicianId = technicianId,
            PerformedByUserId = byUser, ReferenceType = referenceType, ReferenceId = referenceId,
        });
    }

    public async Task ReturnToStockAsync(long partId, long technicianId, int qty, long byUser, string referenceType, long referenceId, CancellationToken ct)
    {
        if (!await GuardedDecrementAsync(partId, technicianId, qty, ct))
            throw new StockException("Technician does not hold enough of this part to return.");
        await IncrementAsync(partId, StockBalance.Warehouse, qty, ct);
        db.StockMovements.Add(new StockMovement
        {
            PartId = partId, MovementType = MovementType.Return, Quantity = qty, TechnicianId = technicianId,
            PerformedByUserId = byUser, ReferenceType = referenceType, ReferenceId = referenceId,
        });
    }

    private Task IncrementAsync(long partId, long technicianId, int delta, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($@"
INSERT INTO `stock_balances` (`part_id`, `technician_id`, `on_hand`, `created_at`, `updated_at`)
VALUES ({partId}, {technicianId}, {delta}, UTC_TIMESTAMP(6), UTC_TIMESTAMP(6))
ON DUPLICATE KEY UPDATE `on_hand` = `on_hand` + {delta}, `updated_at` = UTC_TIMESTAMP(6)", ct);

    private async Task<bool> GuardedDecrementAsync(long partId, long technicianId, int qty, CancellationToken ct)
    {
        var affected = await db.Database.ExecuteSqlInterpolatedAsync($@"
UPDATE `stock_balances` SET `on_hand` = `on_hand` - {qty}, `updated_at` = UTC_TIMESTAMP(6)
WHERE `part_id` = {partId} AND `technician_id` = {technicianId} AND `on_hand` >= {qty}", ct);
        return affected == 1;
    }
}
