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

    /// <summary>Dispatch stock to a technician.
    ///
    /// The warehouse is always debited here - the goods have left the shelf, whatever happens next.
    /// Whether the TECHNICIAN is credited here depends on how the stock travels, and that is the
    /// distinction <paramref name="creditTechnician"/> carries:
    ///
    /// A courier issue is not in anyone's hands while it is in transit. Crediting the technician at
    /// dispatch says they are holding stock they have not seen, and it stays wrong if the shipment
    /// arrives short, damaged or not at all - the acknowledgement recorded the shortfall while the
    /// balance kept counting the full quantity. So it waits for <see cref="AcknowledgeReceiptAsync"/>.
    ///
    /// A counter handover has no transit to survive: it is acknowledged in the same call that issues
    /// it, so it credits immediately and the two are one event.
    ///
    /// Returns the movement entity so callers can link serial rows to it (id is assigned once the
    /// caller saves changes).</summary>
    public async Task<StockMovement> IssueAsync(long partId, long technicianId, int qty, long byUser,
        string referenceType, long referenceId, CancellationToken ct, bool creditTechnician = true)
    {
        if (!await GuardedDecrementAsync(partId, StockBalance.Warehouse, qty, ct))
            throw new StockException("Insufficient warehouse stock to issue.");
        if (creditTechnician) await IncrementAsync(partId, technicianId, qty, ct);
        var movement = new StockMovement
        {
            PartId = partId, MovementType = MovementType.Issue, Quantity = qty, TechnicianId = technicianId,
            PerformedByUserId = byUser, ReferenceType = referenceType, ReferenceId = referenceId,
            CreditedOnIssue = creditTechnician,
        };
        db.StockMovements.Add(movement);
        return movement;
    }

    /// <summary>Apply a technician's acknowledgement of an issue to their balance.
    ///
    /// Only what actually arrived AND is usable is credited: <paramref name="received"/> minus
    /// <paramref name="defective"/>, the same figure the legacy app derived from the movement ledger.
    /// A defective unit is physically with the technician - they can send it in for service - but it
    /// is not stock they can fit, so counting it as on-hand would offer it on the next job.
    ///
    /// The shortfall is not silently dropped. Missing and defective quantities each get their own
    /// movement, so the gap between what left the warehouse and what became usable stock is a row
    /// somebody can look at rather than an unexplained difference between two numbers.</summary>
    public async Task AcknowledgeReceiptAsync(long partId, long technicianId, int received, int defective,
        int missing, long byUser, string referenceType, long referenceId, CancellationToken ct)
    {
        var usable = Math.Max(received - defective, 0);
        if (usable > 0)
        {
            await IncrementAsync(partId, technicianId, usable, ct);
            db.StockMovements.Add(new StockMovement
            {
                PartId = partId, MovementType = MovementType.IssueReceipt, Quantity = usable,
                TechnicianId = technicianId, PerformedByUserId = byUser,
                ReferenceType = referenceType, ReferenceId = referenceId,
            });
        }

        if (defective > 0)
            DefectiveOnArrival(partId, technicianId, defective, byUser, referenceType, referenceId,
                "Arrived faulty - held by the technician, not usable stock");

        if (missing > 0)
            db.StockMovements.Add(new StockMovement
            {
                PartId = partId, MovementType = MovementType.LossInTransit, Quantity = missing,
                TechnicianId = technicianId, PerformedByUserId = byUser,
                ReferenceType = referenceType, ReferenceId = referenceId,
                Remarks = "Dispatched but never arrived",
            });
    }

    /// <summary>The units leave the technician as the shipment leaves them. Mirror of an issue's
    /// warehouse debit: stock stops being yours when it physically goes, so it cannot be fitted on a
    /// job while it is sitting in a courier's van. Booked here rather than at acknowledgement because
    /// a technician who spent it in the meantime used to make the acknowledgement fail outright,
    /// leaving the shipment stuck pending with nothing anyone could do about it from the desk.</summary>
    public async Task ReturnDispatchAsync(long partId, long technicianId, int qty, long byUser,
        string referenceType, long referenceId, CancellationToken ct)
    {
        if (!await GuardedDecrementAsync(partId, technicianId, qty, ct))
            throw new StockException("Technician does not hold enough of this part to return.");
        db.StockMovements.Add(new StockMovement
        {
            PartId = partId, MovementType = MovementType.ReturnDispatched, Quantity = qty,
            TechnicianId = technicianId, PerformedByUserId = byUser,
            ReferenceType = referenceType, ReferenceId = referenceId,
        });
    }

    /// <summary>The shipment arrived and was acknowledged: the units go back on the warehouse shelf.
    /// <paramref name="debitTechnician"/> covers shipments raised before dispatch and receipt were
    /// split out — those never had their quantity taken off the technician, so this still has to.</summary>
    public async Task ReturnToStockAsync(long partId, long technicianId, int qty, long byUser,
        string referenceType, long referenceId, CancellationToken ct, bool debitTechnician = false)
    {
        if (debitTechnician && !await GuardedDecrementAsync(partId, technicianId, qty, ct))
            throw new StockException("Technician does not hold enough of this part to return.");
        await IncrementAsync(partId, StockBalance.Warehouse, qty, ct);
        db.StockMovements.Add(new StockMovement
        {
            PartId = partId, MovementType = MovementType.Return, Quantity = qty, TechnicianId = technicianId,
            PerformedByUserId = byUser, ReferenceType = referenceType, ReferenceId = referenceId,
        });
    }

    /// <summary>Units that arrived faulty. Recorded, never credited: the technician is holding them
    /// and can send them in for service, but they are not stock anyone can fit.</summary>
    public void DefectiveOnArrival(long partId, long technicianId, int qty, long byUser,
        string referenceType, long referenceId, string remarks) =>
        db.StockMovements.Add(new StockMovement
        {
            PartId = partId, MovementType = MovementType.DefectiveOnArrival, Quantity = qty,
            TechnicianId = technicianId, PerformedByUserId = byUser,
            ReferenceType = referenceType, ReferenceId = referenceId, Remarks = remarks,
        });

    /// <summary>A shipment that never arrived. The technician was debited when it left them, so the
    /// quantity is simply gone; this records where it went instead of leaving an unexplained gap
    /// between what was sent and what was shelved.</summary>
    public void LostInTransit(long partId, long technicianId, int qty, long byUser,
        string referenceType, long referenceId, string remarks) =>
        db.StockMovements.Add(new StockMovement
        {
            PartId = partId, MovementType = MovementType.LossInTransit, Quantity = qty,
            TechnicianId = technicianId, PerformedByUserId = byUser,
            ReferenceType = referenceType, ReferenceId = referenceId, Remarks = remarks,
        });

    /// <summary>A peer transfer leaves the sender as it is handed over, for the same reason a return
    /// does: it is out of their hands, so it must not still be fittable on their jobs.</summary>
    public async Task TransferOutAsync(long partId, long fromTechnicianId, int qty,
        long byUser, string referenceType, long referenceId, CancellationToken ct)
    {
        if (!await GuardedDecrementAsync(partId, fromTechnicianId, qty, ct))
            throw new StockException("Sender does not hold enough of this part.");
        db.StockMovements.Add(new StockMovement
        {
            PartId = partId, MovementType = MovementType.TransferOut, Quantity = qty,
            TechnicianId = fromTechnicianId, PerformedByUserId = byUser,
            ReferenceType = referenceType, ReferenceId = referenceId,
        });
    }

    /// <summary>The receiver acknowledged: credit what arrived and is usable. Defective units are
    /// theirs to hold but are not stock they can fit, the same rule an issue receipt follows.</summary>
    public async Task TransferInAsync(long partId, long toTechnicianId, int qty,
        long byUser, string referenceType, long referenceId, CancellationToken ct)
    {
        if (qty <= 0) return;
        await IncrementAsync(partId, toTechnicianId, qty, ct);
        db.StockMovements.Add(new StockMovement
        {
            PartId = partId, MovementType = MovementType.TransferIn, Quantity = qty,
            TechnicianId = toTechnicianId, PerformedByUserId = byUser,
            ReferenceType = referenceType, ReferenceId = referenceId,
        });
    }

    /// <summary>Put a transfer's units back on the sender: the transfer was cancelled, or the receiver
    /// reported them missing and custody rolled back. No guard — returning stock cannot go negative.</summary>
    public async Task TransferReturnToSenderAsync(long partId, long fromTechnicianId, int qty,
        long byUser, string referenceType, long referenceId, string remarks, CancellationToken ct)
    {
        if (qty <= 0) return;
        await IncrementAsync(partId, fromTechnicianId, qty, ct);
        db.StockMovements.Add(new StockMovement
        {
            PartId = partId, MovementType = MovementType.TransferIn, Quantity = qty,
            TechnicianId = fromTechnicianId, PerformedByUserId = byUser,
            ReferenceType = referenceType, ReferenceId = referenceId, Remarks = remarks,
        });
    }

    /// <summary>Legacy one-row transfer: sender → receiver, both sides at acknowledgement. Kept for
    /// transfers raised before send and receipt were split out.</summary>
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

    /// <summary>Put a replacement unit back on the shelf: the swap it was issued for was cancelled
    /// before the job was dispatched, so the unit never left the building. The exact reversal of
    /// <see cref="ReplacementOutAsync"/>, and deliberately not a Receipt — a receipt means goods
    /// arrived from a supplier, and somebody reading the shelf's history a year later should not have
    /// to guess which of those two this row was.</summary>
    public async Task ReplacementReturnAsync(long partId, int qty, long byUser, long serviceId,
        string? serialNo, string? remarks, CancellationToken ct)
    {
        await IncrementAsync(partId, StockBalance.Warehouse, qty, ct);
        db.StockMovements.Add(new StockMovement
        {
            PartId = partId, MovementType = MovementType.ReplacementReturn, Quantity = qty,
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
