using Microsoft.EntityFrameworkCore;
using PSR.Service.Api.Data;
using PSR.Service.Api.Data.Entities;

namespace PSR.Service.Api.Stock;

/// <summary>
/// Owns every mutation of <c>component_serials</c> + <c>serial_status_history</c>. Mirrors
/// <see cref="StockLedgerService"/>: call inside the caller's transaction. Methods that create serials
/// persist them (to obtain ids for the audit FK); the audit rows are added to the context and saved by
/// the caller's final SaveChanges — all within the one ambient transaction.
/// </summary>
public class SerialService(AppDbContext db)
{
    // A serial may be (re)issued only when it is brand-new, or back at the service center ready to redeploy.
    private static readonly SerialStatus[] ReIssuable = { SerialStatus.ReturnedToSc, SerialStatus.Repaired };

    // Physically-with-technician statuses a return shipment may contain.
    private static readonly SerialStatus[] ReturnShippable =
        { SerialStatus.Received, SerialStatus.Collected, SerialStatus.Defective };

    /// <summary>Returns serial → reason for any serial that cannot be issued for this part
    /// (duplicated in the request, or currently deployed elsewhere). Empty ⇒ all issuable.</summary>
    public async Task<Dictionary<string, string>> FindIssueConflictsAsync(
        long partId, IReadOnlyCollection<string> serials, CancellationToken ct)
    {
        var conflicts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var trimmed = serials.Select(s => s.Trim()).Where(s => s.Length > 0).ToList();

        foreach (var g in trimmed.GroupBy(s => s, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            conflicts[g.Key] = "Entered more than once.";

        var bySn = (await db.ComponentSerials.AsNoTracking().Where(c => c.PartId == partId).ToListAsync(ct))
            .GroupBy(c => c.SerialNumber, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var sn in trimmed)
        {
            if (!bySn.TryGetValue(sn, out var c)) continue;                                   // new serial: fine
            if (ReIssuable.Contains(c.Status) && c.OwnerType == SerialOwnerType.ServiceCenter) continue;
            conflicts[sn] = c.OwnerType switch
            {
                SerialOwnerType.Technician => $"Already with a technician (status {c.Status}).",
                SerialOwnerType.Customer => $"Installed at a customer (status {c.Status}).",
                _ => $"Not available for issue (status {c.Status}).",
            };
        }
        return conflicts;
    }

    /// <summary>Bind serials to a technician on issue: upsert each to ISSUED with owner SERVICE_CENTER
    /// (in transit) until the technician acknowledges receipt. Writes <c>stock_issue_serials</c> link
    /// rows against the issue movement so the acknowledgement knows which serials belong to it.</summary>
    public async Task CaptureOnIssueAsync(
        long stockMovementId, long partId, string? itemName, long technicianId, string technicianName,
        IReadOnlyCollection<string> serials, long byUser, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var transitRef = $"In transit to {technicianName}";
        var trimmed = serials.Select(s => s.Trim()).Where(s => s.Length > 0).ToList();

        var bySn = (await db.ComponentSerials.Where(c => c.PartId == partId).ToListAsync(ct))
            .GroupBy(c => c.SerialNumber, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var touched = new List<(ComponentSerial serial, string? old)>();
        foreach (var sn in trimmed)
        {
            if (bySn.TryGetValue(sn, out var c))
            {
                var old = c.Status.ToString();
                c.Status = SerialStatus.Issued;
                c.OwnerType = SerialOwnerType.ServiceCenter;
                c.OwnerRef = transitRef;
                c.TechnicianId = technicianId;
                c.ItemName ??= itemName;
                c.LastUpdatedAt = now;
                touched.Add((c, old));
            }
            else
            {
                var created = new ComponentSerial
                {
                    PartId = partId, SerialNumber = sn, ItemName = itemName,
                    Status = SerialStatus.Issued, OwnerType = SerialOwnerType.ServiceCenter,
                    OwnerRef = transitRef, TechnicianId = technicianId,
                    LastUpdatedAt = now, CreatedAt = now,
                };
                db.ComponentSerials.Add(created);
                touched.Add((created, null));
            }
        }

        await db.SaveChangesAsync(ct);   // assign ids to newly-created serials

        foreach (var (serial, old) in touched)
        {
            db.StockIssueSerials.Add(new StockIssueSerial
            {
                StockMovementId = stockMovementId, ComponentSerialId = serial.Id,
            });
            AddHistory(serial, old, SerialStatus.Issued, byUser, $"Issued to {technicianName}", now);
        }
    }

    /// <summary>Admin manual status change (mark missing/found/defective/repaired…). Ownership is
    /// synced to the new status so status and owner can't diverge: back-at-SC statuses flip to
    /// SERVICE_CENTER, with-technician statuses flip to the serial's technician, installed/used flip
    /// to CUSTOMER, MISSING keeps the current custody. Returns null if the serial is missing.</summary>
    public async Task<ComponentSerial?> ChangeStatusAsync(
        long serialId, SerialStatus newStatus, long byUser, string remarks, CancellationToken ct)
    {
        var c = await db.ComponentSerials.FirstOrDefaultAsync(x => x.Id == serialId, ct);
        if (c is null) return null;
        var old = c.Status.ToString();
        var now = DateTime.UtcNow;
        c.Status = newStatus;
        await SyncOwnerToStatusAsync(c, ct);
        c.LastUpdatedAt = now;
        AddHistory(c, old, newStatus, byUser, remarks, now);
        return c;
    }

    /// <summary>Technician acknowledges one issued serial: RECEIVED / DEFECTIVE flip ownership to the
    /// technician (unit physically arrived); MISSING keeps SERVICE_CENTER custody.</summary>
    public async Task<string?> AckIssueSerialAsync(
        long serialId, SerialAckStatus ack, long technicianId, string technicianName, CancellationToken ct)
    {
        var c = await db.ComponentSerials.FirstOrDefaultAsync(x => x.Id == serialId, ct);
        if (c is null) return $"Serial record {serialId} not found.";
        if (c.TechnicianId != technicianId || c.Status != SerialStatus.Issued)
            return $"{c.SerialNumber} is not awaiting your acknowledgement (status {c.Status}).";

        var old = c.Status.ToString();
        var now = DateTime.UtcNow;
        var newStatus = ack switch
        {
            SerialAckStatus.Received => SerialStatus.Received,
            SerialAckStatus.Defective => SerialStatus.Defective,
            _ => SerialStatus.Missing,
        };
        c.Status = newStatus;
        if (ack is SerialAckStatus.Received or SerialAckStatus.Defective)
        {
            c.OwnerType = SerialOwnerType.Technician;
            c.OwnerRef = technicianName;
        }
        // MISSING: ownership stays SERVICE_CENTER — the unit never reached the technician.
        c.LastUpdatedAt = now;
        AddHistory(c, old, newStatus, technicianId,
            ack == SerialAckStatus.Missing ? "Technician reports unit not received" : "Receipt acknowledged", now);
        return null;
    }

    /// <summary>Service revert: a completed job went back to in-service — the fitted unit returns to
    /// the technician's custody (mirror of <see cref="InstallToCustomerAsync"/>).</summary>
    public async Task UninstallToTechnicianAsync(
        long partId, string serialNumber, long technicianId, string technicianName, long byUser, CancellationToken ct)
    {
        var c = await FindAsync(partId, serialNumber, ct);
        if (c is null) return;
        if (c.Status is not (SerialStatus.Installed or SerialStatus.Used)) return;

        var old = c.Status.ToString();
        var now = DateTime.UtcNow;
        c.Status = SerialStatus.Received;
        c.OwnerType = SerialOwnerType.Technician;
        c.OwnerRef = technicianName;
        c.TechnicianId = technicianId;
        c.LastUpdatedAt = now;
        AddHistory(c, old, SerialStatus.Received, byUser, "Service reverted — unit back with technician", now);
    }

    /// <summary>Service flow: a serial-tracked unit is fitted at / handed to a customer. Owner → CUSTOMER.</summary>
    public async Task InstallToCustomerAsync(
        long partId, string serialNumber, string? itemName, string customerName,
        SerialStatus newStatus, long byUser, CancellationToken ct)
    {
        var sn = serialNumber.Trim();
        if (sn.Length == 0) return;
        var now = DateTime.UtcNow;
        var c = await db.ComponentSerials.FirstOrDefaultAsync(x => x.PartId == partId && x.SerialNumber == sn, ct);
        if (c is null)
        {
            c = new ComponentSerial
            {
                PartId = partId, SerialNumber = sn, ItemName = itemName,
                Status = newStatus, OwnerType = SerialOwnerType.Customer,
                OwnerRef = customerName, LastUpdatedAt = now, CreatedAt = now,
            };
            db.ComponentSerials.Add(c);
            await db.SaveChangesAsync(ct);
            AddHistory(c, null, newStatus, byUser, $"Installed for {customerName}", now);
            return;
        }
        var old = c.Status.ToString();
        c.Status = newStatus;
        c.OwnerType = SerialOwnerType.Customer;
        c.OwnerRef = customerName;
        c.TechnicianId = null;
        c.LastUpdatedAt = now;
        AddHistory(c, old, newStatus, byUser, $"Installed for {customerName}", now);
    }

    /// <summary>Field flow: faulty unit collected from a customer — upserts the serial as COLLECTED
    /// held by the technician (legacy updateOwnerOnCollection).</summary>
    public async Task CollectFromCustomerAsync(
        long partId, string serialNumber, string? itemName, long technicianId, string technicianName,
        bool defective, long byUser, CancellationToken ct)
    {
        var sn = serialNumber.Trim();
        if (sn.Length == 0) return;
        var now = DateTime.UtcNow;
        var newStatus = defective ? SerialStatus.Defective : SerialStatus.Collected;
        var c = await db.ComponentSerials.FirstOrDefaultAsync(x => x.PartId == partId && x.SerialNumber == sn, ct);
        if (c is null)
        {
            c = new ComponentSerial
            {
                PartId = partId, SerialNumber = sn, ItemName = itemName,
                Status = newStatus, OwnerType = SerialOwnerType.Technician,
                OwnerRef = technicianName, TechnicianId = technicianId,
                LastUpdatedAt = now, CreatedAt = now,
            };
            db.ComponentSerials.Add(c);
            await db.SaveChangesAsync(ct);
            AddHistory(c, null, newStatus, byUser, "Collected from customer (new record)", now);
            return;
        }
        var old = c.Status.ToString();
        c.Status = newStatus;
        c.OwnerType = SerialOwnerType.Technician;
        c.OwnerRef = technicianName;
        c.TechnicianId = technicianId;
        c.LastUpdatedAt = now;
        AddHistory(c, old, newStatus, byUser, "Collected from customer", now);
    }

    /// <summary>Ship one serial on a technician return: validates custody, sets IN_TRANSIT_SC.
    /// Returns an error message, or null on success.</summary>
    public async Task<string?> ShipReturnSerialAsync(
        long serialId, long technicianId, long byUser, CancellationToken ct)
    {
        var c = await db.ComponentSerials.FirstOrDefaultAsync(x => x.Id == serialId, ct);
        if (c is null) return $"Serial record {serialId} not found.";
        if (c.OwnerType != SerialOwnerType.Technician || c.TechnicianId != technicianId)
            return $"{c.SerialNumber} is not in your custody.";
        if (!ReturnShippable.Contains(c.Status))
            return $"{c.SerialNumber} cannot be shipped (status {c.Status}).";

        var old = c.Status.ToString();
        var now = DateTime.UtcNow;
        c.Status = SerialStatus.InTransitSc;
        c.LastUpdatedAt = now;
        AddHistory(c, old, SerialStatus.InTransitSc, byUser, "Dispatched to service center", now);
        return null;
    }

    /// <summary>Return flow: receive a unit back at the service center (good → RETURNED_TO_SC, faulty →
    /// DEFECTIVE). Owner → SERVICE_CENTER. Returns the updated serial, or null if not found.</summary>
    public async Task<ComponentSerial?> ReceiveReturnAsync(
        long serialId, bool defective, long byUser, string remarks, CancellationToken ct)
    {
        var c = await db.ComponentSerials.FirstOrDefaultAsync(x => x.Id == serialId, ct);
        if (c is null) return null;
        var old = c.Status.ToString();
        var now = DateTime.UtcNow;
        var newStatus = defective ? SerialStatus.Defective : SerialStatus.ReturnedToSc;
        c.Status = newStatus;
        c.OwnerType = SerialOwnerType.ServiceCenter;
        c.OwnerRef = "Service center";
        c.TechnicianId = null;
        c.LastUpdatedAt = now;
        AddHistory(c, old, newStatus, byUser, remarks, now);
        return c;
    }

    /// <summary>Peer transfer: flip one RECEIVED sender-owned serial to IN_TRANSIT_TECH. Ownership
    /// stays with the sender so cancel rolls back cleanly. Error message or null.</summary>
    public async Task<string?> MarkInTransitTechAsync(
        long serialId, long fromTechnicianId, string toTechnicianName, long byUser, CancellationToken ct)
    {
        var c = await db.ComponentSerials.FirstOrDefaultAsync(x => x.Id == serialId, ct);
        if (c is null) return $"Serial record {serialId} not found.";
        if (c.OwnerType != SerialOwnerType.Technician || c.TechnicianId != fromTechnicianId)
            return $"{c.SerialNumber} is not in your custody.";
        if (c.Status != SerialStatus.Received)
            return $"{c.SerialNumber} cannot be transferred (status {c.Status}).";

        var old = c.Status.ToString();
        var now = DateTime.UtcNow;
        c.Status = SerialStatus.InTransitTech;
        c.LastUpdatedAt = now;
        AddHistory(c, old, SerialStatus.InTransitTech, byUser, $"Transfer initiated to {toTechnicianName}", now);
        return null;
    }

    /// <summary>Receiver acknowledges one transferred serial. RECEIVED / DEFECTIVE flip ownership to
    /// the receiver; MISSING rolls the unit back to the sender.</summary>
    public async Task<string?> AckTransferSerialAsync(
        long serialId, SerialAckStatus ack, long fromTechnicianId, string fromTechnicianName,
        long toTechnicianId, string toTechnicianName, CancellationToken ct)
    {
        var c = await db.ComponentSerials.FirstOrDefaultAsync(x => x.Id == serialId, ct);
        if (c is null) return $"Serial record {serialId} not found.";
        if (c.Status != SerialStatus.InTransitTech)
            return $"{c.SerialNumber} is not in transit (status {c.Status}).";

        var old = c.Status.ToString();
        var now = DateTime.UtcNow;
        if (ack == SerialAckStatus.Missing)
        {
            c.Status = SerialStatus.Received;
            c.OwnerType = SerialOwnerType.Technician;
            c.OwnerRef = fromTechnicianName;
            c.TechnicianId = fromTechnicianId;
            AddHistory(c, old, SerialStatus.Received, toTechnicianId,
                $"Transfer to {toTechnicianName} reported MISSING; custody returned to {fromTechnicianName}", now);
        }
        else
        {
            c.Status = ack == SerialAckStatus.Defective ? SerialStatus.Defective : SerialStatus.Received;
            c.OwnerType = SerialOwnerType.Technician;
            c.OwnerRef = toTechnicianName;
            c.TechnicianId = toTechnicianId;
            AddHistory(c, old, c.Status, toTechnicianId,
                $"Transfer from {fromTechnicianName} acknowledged as {ack}", now);
        }
        c.LastUpdatedAt = now;
        return null;
    }

    /// <summary>Sender cancelled the transfer: roll IN_TRANSIT_TECH serials back to RECEIVED at sender.
    /// Serials that already moved on are skipped silently.</summary>
    public async Task RollbackTransferSerialAsync(
        long serialId, long fromTechnicianId, string fromTechnicianName, long byUser, CancellationToken ct)
    {
        var c = await db.ComponentSerials.FirstOrDefaultAsync(x => x.Id == serialId, ct);
        if (c is null || c.Status != SerialStatus.InTransitTech) return;

        var old = c.Status.ToString();
        var now = DateTime.UtcNow;
        c.Status = SerialStatus.Received;
        c.OwnerType = SerialOwnerType.Technician;
        c.OwnerRef = fromTechnicianName;
        c.TechnicianId = fromTechnicianId;
        c.LastUpdatedAt = now;
        AddHistory(c, old, SerialStatus.Received, byUser, "Transfer cancelled", now);
    }

    /// <summary>Serials this technician holds. Default: RECEIVED only (pick list for usage/transfer).
    /// <paramref name="forReturn"/>: everything physically shippable back (RECEIVED / COLLECTED / DEFECTIVE).</summary>
    public Task<List<ComponentSerial>> AvailableForTechnicianAsync(
        long technicianId, long? partId, bool forReturn, CancellationToken ct)
    {
        var q = db.ComponentSerials.AsNoTracking()
            .Where(c => c.OwnerType == SerialOwnerType.Technician && c.TechnicianId == technicianId);
        q = forReturn
            ? q.Where(c => c.Status == SerialStatus.Received || c.Status == SerialStatus.Collected
                        || c.Status == SerialStatus.Defective)
            : q.Where(c => c.Status == SerialStatus.Received);
        if (partId is { } pid) q = q.Where(c => c.PartId == pid);
        return q.OrderBy(c => c.SerialNumber).ToListAsync(ct);
    }

    /// <summary>Validate a fitted serial for a service/sale line: must exist for the part, be in the
    /// technician's custody, and be RECEIVED. Error message or null.</summary>
    public async Task<string?> ValidateFittedSerialAsync(
        long partId, string serialNumber, long technicianId, CancellationToken ct)
    {
        var c = await FindAsync(partId, serialNumber, ct);
        if (c is null) return $"Serial '{serialNumber.Trim()}' is not in your inventory for this part.";
        if (c.OwnerType != SerialOwnerType.Technician || c.TechnicianId != technicianId)
            return $"Serial '{c.SerialNumber}' is not in your custody ({c.OwnerType}: {c.OwnerRef}).";
        if (c.Status != SerialStatus.Received)
            return $"Serial '{c.SerialNumber}' is not available (status {c.Status}).";
        return null;
    }

    public Task<ComponentSerial?> FindAsync(long partId, string serialNumber, CancellationToken ct)
        => db.ComponentSerials.FirstOrDefaultAsync(
            c => c.PartId == partId && c.SerialNumber == serialNumber.Trim(), ct);

    /// <summary>Keeps owner_type/owner_ref consistent with a manually-set status (gap: status and
    /// custody used to drift apart on admin changes).</summary>
    private async Task SyncOwnerToStatusAsync(ComponentSerial c, CancellationToken ct)
    {
        switch (c.Status)
        {
            case SerialStatus.ReturnedToSc or SerialStatus.Repaired or SerialStatus.InTransitSc:
                c.OwnerType = SerialOwnerType.ServiceCenter;
                c.OwnerRef = "Service center";
                if (c.Status != SerialStatus.InTransitSc) c.TechnicianId = null;
                break;
            case SerialStatus.Received or SerialStatus.Collected or SerialStatus.Defective
                or SerialStatus.InTransitTech when c.TechnicianId is { } tid:
                c.OwnerType = SerialOwnerType.Technician;
                c.OwnerRef = await db.Users.AsNoTracking()
                    .Where(u => u.Id == tid)
                    .Select(u => u.FullName ?? u.Username)
                    .FirstOrDefaultAsync(ct) ?? c.OwnerRef;
                break;
            case SerialStatus.Installed or SerialStatus.Used:
                c.OwnerType = SerialOwnerType.Customer;
                break;
            // Missing / Issued: custody stays where it was.
        }
    }

    private void AddHistory(ComponentSerial c, string? oldStatus, SerialStatus newStatus,
        long byUser, string remarks, DateTime at) =>
        db.SerialStatusHistory.Add(new SerialStatusHistory
        {
            ComponentSerialId = c.Id, PartId = c.PartId, SerialNumber = c.SerialNumber,
            OldStatus = oldStatus, NewStatus = newStatus.ToString(),
            ChangedByUserId = byUser, Remarks = remarks, ChangedAt = at,
        });
}
