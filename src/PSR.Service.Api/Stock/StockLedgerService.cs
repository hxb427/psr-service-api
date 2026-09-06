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
    public async Task ReceiptAsync(long partId, int qty, long byUser, string? remarks, string? invoiceNo, string? source, CancellationToken ct)
    {
        await IncrementAsync(partId, StockBalance.Warehouse, qty, ct);
        db.StockMovements.Add(new StockMovement
        {
            PartId = partId, MovementType = MovementType.Receipt, Quantity = qty,
            PerformedByUserId = byUser, ReferenceType = "MANUAL", Remarks = remarks,
            InvoiceNo = invoiceNo, Source = source,
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

    /// <summary>Returns the movement entity so callers can link serial rows to it (id is assigned
    /// once the caller saves changes).</summary>
    public async Task<StockMovement> IssueAsync(long partId, long technicianId, int qty, long byUser, string referenceType, long referenceId, CancellationToken ct)
    {
        if (!await GuardedDecrementAsync(partId, StockBalance.Warehouse, qty, ct))
            throw new StockException("Insufficient warehouse stock to issue.");
        await IncrementAsync(partId, technicianId, qty, ct);
        var movement = new StockMovement
        {
            PartId = partId, MovementType = MovementType.Issue, Quantity = qty, TechnicianId = technicianId,
            PerformedByUserId = byUser, ReferenceType = referenceType, ReferenceId = referenceId,
        };
        db.StockMovements.Add(movement);
        return movement;
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

    /// <summary>Peer transfer at acknowledgement: sender technician → receiver technician.
    /// TechnicianId on the movement is the SENDER; the receiver is on the transfer row.</summary>
    public async Task TransferAsync(long partId, long fromTechnicianId, long toTechnicianId, int qty,
        long byUser, string referenceType, long referenceId, CancellationToken ct)
    {
        if (!await GuardedDecrementAsync(partId, fromTechnicianId, qty, ct))
            throw new StockException("Sender does not hold enough of this part.");
        await IncrementAsync(partId, toTechnicianId, qty, ct);
        db.StockMovements.Add(new StockMovement
        {
            PartId = partId, MovementType = MovementType.Transfer, Quantity = qty, TechnicianId = fromTechnicianId,
            PerformedByUserId = byUser, ReferenceType = referenceType, ReferenceId = referenceId,
        });
    }

    /// <summary>Consume a part from a technician's on-hand stock (parts fitted while servicing).
    /// Called per part-bearing line when a service is completed.</summary>
    public async Task ConsumeAsync(long partId, long technicianId, int qty, long byUser, string referenceType, long referenceId, CancellationToken ct)
    {
        if (!await GuardedDecrementAsync(partId, technicianId, qty, ct))
            throw new StockException("Technician does not hold enough of this part to consume.");
        db.StockMovements.Add(new StockMovement
        {
            PartId = partId, MovementType = MovementType.Consumption, Quantity = qty, TechnicianId = technicianId,
            PerformedByUserId = byUser, ReferenceType = referenceType, ReferenceId = referenceId,
        });
    }

    /// <summary>Return parts to a technician that were consumed at completion (service reverted).
    /// Mirror of ConsumeAsync — increments the technician balance and logs a reversal movement.</summary>
    public async Task ReverseConsumptionAsync(long partId, long technicianId, int qty, long byUser, string referenceType, long referenceId, CancellationToken ct)
    {
        await IncrementAsync(partId, technicianId, qty, ct);
        db.StockMovements.Add(new StockMovement
        {
            PartId = partId, MovementType = MovementType.ConsumptionReversal, Quantity = qty, TechnicianId = technicianId,
            PerformedByUserId = byUser, ReferenceType = referenceType, ReferenceId = referenceId,
        });
    }

    /// <summary>Ship a whole replacement unit out of the warehouse (service resolved by full replacement).
    /// Records the replacement unit's serial on the movement.</summary>
    public async Task ReplacementOutAsync(long partId, int qty, long byUser, long serviceId, string? serialNo, string? remarks, CancellationToken ct)
    {
        if (!await GuardedDecrementAsync(partId, StockBalance.Warehouse, qty, ct))
            throw new StockException("Insufficient warehouse stock for the replacement unit.");
        db.StockMovements.Add(new StockMovement
        {
            PartId = partId, MovementType = MovementType.Replacement, Quantity = qty,
            PerformedByUserId = byUser, ReferenceType = "SERVICE", ReferenceId = serviceId,
            SerialNo = serialNo, Remarks = remarks,
        });
    }

    /// <summary>Ship a spare out of the warehouse against a direct sale. Called once per sale line when the
    /// sale is marked sold — that one action is the point the goods actually leave, so a sale that has not
    /// been marked reserves nothing and an over-sold item fails here rather than silently going negative.</summary>
    public async Task SaleOutAsync(long partId, string itemCode, int qty, long byUser, long saleId,
        string? remarks, CancellationToken ct)
    {
        if (!await GuardedDecrementAsync(partId, StockBalance.Warehouse, qty, ct))
            throw new StockException(
                $"{itemCode} no longer has {qty} in warehouse stock — the invoice was not raised and nothing was taken out.");
        db.StockMovements.Add(new StockMovement
        {
            PartId = partId, MovementType = MovementType.Sale, Quantity = qty,
            PerformedByUserId = byUser, ReferenceType = "SPARE_SALE", ReferenceId = saleId,
            Remarks = remarks,
        });
    }

    /// <summary>Undo a Mark as sold: the units go back on the shelf. Distinct from a sale return — nothing
    /// ever reached the customer, the sale is simply not sold any more — so it carries its own movement
    /// type and the ledger keeps the two apart. No guard: putting stock back cannot drive a balance
    /// negative.</summary>
    public async Task SaleUnsoldInAsync(long partId, int qty, long byUser, long saleId,
        string? remarks, CancellationToken ct)
    {
        await IncrementAsync(partId, StockBalance.Warehouse, qty, ct);
        db.StockMovements.Add(new StockMovement
        {
            PartId = partId, MovementType = MovementType.SaleUnsold, Quantity = qty,
            PerformedByUserId = byUser, ReferenceType = "SPARE_SALE", ReferenceId = saleId,
            Remarks = remarks,
        });
    }

    /// <summary>Goods coming back from a sold sale go straight to the warehouse. No guard is needed
    /// the way SaleOutAsync needs one — putting stock back cannot drive a balance negative — but it still
    /// goes through the ledger so the movement carries a SALE_RETURN reference and shows up in the audit
    /// trail as a return rather than an unexplained adjustment.</summary>
    public async Task SaleReturnInAsync(long partId, int qty, long byUser, long returnId,
        string? remarks, CancellationToken ct)
    {
        await IncrementAsync(partId, StockBalance.Warehouse, qty, ct);
        db.StockMovements.Add(new StockMovement
        {
            PartId = partId, MovementType = MovementType.SaleReturn, Quantity = qty,
            PerformedByUserId = byUser, ReferenceType = "SPARE_SALE_RETURN", ReferenceId = returnId,
            Remarks = remarks,
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
