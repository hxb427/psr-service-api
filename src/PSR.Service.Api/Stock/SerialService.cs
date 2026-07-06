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
    /// (in transit) until the technician acknowledges receipt on mobile.</summary>
    public async Task CaptureOnIssueAsync(
        long partId, string? itemName, long technicianId, string technicianName,
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
            AddHistory(serial, old, SerialStatus.Issued, byUser, $"Issued to {technicianName}", now);
    }

    /// <summary>Admin manual status change (mark missing/found/defective/repaired…). Status + audit only;
    /// ownership is left untouched (matches legacy behaviour). Returns null if the serial is missing.</summary>
    public async Task<ComponentSerial?> ChangeStatusAsync(
        long serialId, SerialStatus newStatus, long byUser, string remarks, CancellationToken ct)
    {
        var c = await db.ComponentSerials.FirstOrDefaultAsync(x => x.Id == serialId, ct);
        if (c is null) return null;
        var old = c.Status.ToString();
        var now = DateTime.UtcNow;
        c.Status = newStatus;
        c.LastUpdatedAt = now;
        AddHistory(c, old, newStatus, byUser, remarks, now);
        return c;
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

    public Task<ComponentSerial?> FindAsync(long partId, string serialNumber, CancellationToken ct)
        => db.ComponentSerials.FirstOrDefaultAsync(
            c => c.PartId == partId && c.SerialNumber == serialNumber.Trim(), ct);

    private void AddHistory(ComponentSerial c, string? oldStatus, SerialStatus newStatus,
        long byUser, string remarks, DateTime at) =>
        db.SerialStatusHistory.Add(new SerialStatusHistory
        {
            ComponentSerialId = c.Id, PartId = c.PartId, SerialNumber = c.SerialNumber,
            OldStatus = oldStatus, NewStatus = newStatus.ToString(),
            ChangedByUserId = byUser, Remarks = remarks, ChangedAt = at,
        });
}
