using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PSR.Service.Api.Audit;
using PSR.Service.Api.Auth;
using PSR.Service.Api.Common;
using PSR.Service.Api.Data;
using PSR.Service.Api.Data.Entities;

namespace PSR.Service.Api.Stock;

/// <summary>Admin/service-center control surface for the serial ledger: browse deployed serials,
/// inspect a unit's full audit trail, and apply a manual status change (mark missing/found/…).</summary>
public static class SerialsEndpoints
{
    private const int MaxPageSize = 200;

    public static IEndpointRouteBuilder MapSerialEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/serials").WithTags("serials").RequireAuthorization("StockManage");

        group.MapGet("/", ListAsync);
        group.MapGet("/{id:long}", GetAsync);
        group.MapPost("/{id:long}/status", ChangeStatusAsync);
        group.MapPost("/{id:long}/receive", ReceiveAsync);

        return app;
    }

    private static async Task<Ok<PagedResult<ComponentSerialDto>>> ListAsync(
        AppDbContext db, long? partId, string? status, string? ownerType, long? technicianId,
        string? search, int? page, int? pageSize, CancellationToken ct)
    {
        var pageNum = page is null or < 1 ? 1 : page.Value;
        var size = pageSize is null or < 1 or > MaxPageSize ? 50 : pageSize.Value;

        // Deleted jobs are excluded from the job join, not just left to show. Every other list filters
        // IsDeleted out, so printing a withdrawn job's number here named a job the desk could not open
        // and global search could never return — the SN screen's status looked explained when it was
        // not. A unit whose job has gone shows no number, which is the truth about it.
        var q = from c in db.ComponentSerials.AsNoTracking()
                join p in db.Parts on c.PartId equals p.Id
                join u in db.Users on c.TechnicianId equals (long?)u.Id into ug
                from u in ug.DefaultIfEmpty()
                join j in db.Services.Where(s => !s.IsDeleted) on c.CurrentServiceJobId equals (long?)j.Id into jg
                from j in jg.DefaultIfEmpty()
                select new { c, p.ItemCode, p.Name, TechName = u != null ? (u.FullName ?? u.Username) : null,
                             ServiceNo = j != null ? j.ServiceNo : null };

        if (partId is { } pid) q = q.Where(x => x.c.PartId == pid);
        if (technicianId is { } tid) q = q.Where(x => x.c.TechnicianId == tid);
        if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<SerialStatus>(status, true, out var st))
            q = q.Where(x => x.c.Status == st);
        if (!string.IsNullOrWhiteSpace(ownerType) && Enum.TryParse<SerialOwnerType>(ownerType, true, out var ot))
            q = q.Where(x => x.c.OwnerType == ot);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim();
            q = q.Where(x => x.c.SerialNumber.Contains(s) || x.ItemCode.Contains(s) || x.Name.Contains(s));
        }

        q = q.OrderByDescending(x => x.c.LastUpdatedAt ?? x.c.CreatedAt);
        var total = await q.CountAsync(ct);
        var rows = await q.Skip((pageNum - 1) * size).Take(size).ToListAsync(ct);
        var items = rows.Select(x => ToDto(x.c, x.ItemCode, x.Name, x.TechName, x.ServiceNo)).ToList();
        return TypedResults.Ok(new PagedResult<ComponentSerialDto>(items, pageNum, size, total));
    }

    private static async Task<Results<Ok<ComponentSerialDetailDto>, NotFound>> GetAsync(
        long id, AppDbContext db, CancellationToken ct)
    {
        var row = await SingleRowAsync(db, id, ct);
        if (row is null) return TypedResults.NotFound();

        var audit = await (from h in db.SerialStatusHistory.AsNoTracking()
                           join u in db.Users on h.ChangedByUserId equals u.Id into ug
                           from u in ug.DefaultIfEmpty()
                           where h.ComponentSerialId == id
                           orderby h.ChangedAt descending, h.Id descending
                           select new SerialAuditDto(h.Id, h.OldStatus, h.NewStatus, h.ChangedByUserId,
                               u != null ? u.Username : null, h.Remarks, h.ChangedAt)).ToListAsync(ct);

        return TypedResults.Ok(new ComponentSerialDetailDto(
            ToDto(row.c, row.ItemCode, row.Name, row.TechName, row.ServiceNo), audit));
    }

    /// <summary>Apply a manual status change, moving whatever balance the change implies.
    ///
    /// THE SHELF needs no quantity. A unit put on it, or taken off it, moves the shelf - and the
    /// shelf is counted by unit, as whichever rows are SHELFED in the service centre's custody, so
    /// the status change IS the count change. (This used to apply a +1/-1 to the WAREHOUSE, because
    /// the shelf was then part of that count and the two could disagree: a unit written off as
    /// missing and later found went re-issuable while the count stayed one short for ever.
    /// Separating the two pools closed that by construction.)
    ///
    /// A TECHNICIAN does, and that is what this adds. Their holding is a quantity in
    /// <c>stock_balances</c>, so a unit moving into or out of their hands has to move it - otherwise
    /// a unit marked RECEIVED sits with somebody who cannot fit, transfer or return it, and a unit
    /// marked off them leaves a quantity behind that the serial ledger says is elsewhere. Only
    /// RECEIVED counts (<see cref="SerialService.IsTechnicianStock"/>), and a change between two
    /// technicians moves both sides.
    ///
    /// Handing a unit over is still better done as an issue - that is what produces a challan and an
    /// acknowledgement. This is the correction for when it was not.</summary>
    private static async Task<Results<Ok<ComponentSerialDto>, NotFound, BadRequest<string>>> ChangeStatusAsync(
        long id, [FromBody] ChangeSerialStatusRequest req, ClaimsPrincipal user, AppDbContext db,
        SerialService serial, StockLedgerService ledger, IAuditService audit, HttpContext http,
        CancellationToken ct)
    {
        if (!Enum.TryParse<SerialStatus>(req.Status, true, out var newStatus))
            return TypedResults.BadRequest($"Unknown serial status '{req.Status}'.");
        if (string.IsNullOrWhiteSpace(req.Remarks))
            return TypedResults.BadRequest("Remarks are required for a manual status change.");

        var before = await db.ComponentSerials.AsNoTracking()
            .Where(c => c.Id == id)
            .Select(c => new { c.Status, c.PartId, c.SerialNumber, c.TechnicianId })
            .FirstOrDefaultAsync(ct);
        if (before is null) return TypedResults.NotFound();
        if (SerialService.ManualChangeBlocked(newStatus, req.TechnicianId, before.SerialNumber) is { } blocked)
            return TypedResults.BadRequest(blocked);

        // Checked by name before anything is written. Balances are keyed by user id, so crediting a
        // non-technician would create a holding on a page that only lists technicians - stock that
        // exists and that nobody can see or return, which is the same trap the direct issue guards.
        if (req.TechnicianId is { } wanted)
        {
            var holder = await db.Users.AsNoTracking()
                .Where(u => u.Id == wanted)
                .Select(u => new { u.Id, u.Username, u.IsActive })
                .FirstOrDefaultAsync(ct);
            if (holder is null) return TypedResults.BadRequest("That technician does not exist.");
            if (!holder.IsActive) return TypedResults.BadRequest($"{holder.Username} is deactivated.");
            if (!await IsTechnicianAsync(db, wanted, ct))
                return TypedResults.BadRequest(
                    $"{holder.Username} is not a technician and cannot hold stock.");
        }

        user.TryGetUserId(out var uid);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            var updated = await serial.ChangeStatusAsync(id, newStatus, uid, req.Remarks.Trim(), ct,
                req.TechnicianId);
            if (updated is null) { await tx.RollbackAsync(ct); return TypedResults.NotFound(); }

            // A unit put ON the shelf by hand gets its shelf row, so the shelf's history reads the
            // same whether a unit arrived off a job or was corrected into place. Coming OFF needs no
            // row: the movement types that take a unit off the shelf all write their own, and a
            // correction that simply ends the unit (scrapped, missing) has nowhere to send it.
            if (!SerialService.IsOnShelf(before.Status) && SerialService.IsOnShelf(newStatus))
                ledger.ShelveUnit(before.PartId, uid, "SERIAL_STATUS", id, before.SerialNumber,
                    $"{before.SerialNumber}: {before.Status} -> {newStatus} (manual)");

            // The technician side. Written as two independent halves rather than one net move,
            // because the holders can differ: correcting a unit from one technician to another takes
            // it off the first and puts it on the second, and that is two balances and two rows. When
            // the holder is the same person on both sides the two cancel and neither is written.
            var heldBefore = SerialService.IsTechnicianStock(before.Status) ? before.TechnicianId : null;
            var heldAfter = SerialService.IsTechnicianStock(newStatus) ? updated.TechnicianId : null;

            if (heldBefore != heldAfter)
            {
                var note = $"{before.SerialNumber}: {before.Status} -> {newStatus} (manual)";
                if (heldBefore is { } lost)
                    await ledger.CustodyCorrectionAsync(before.PartId, lost, -1, uid, id, note, ct);
                if (heldAfter is { } gained)
                    await ledger.CustodyCorrectionAsync(before.PartId, gained, +1, uid, id, note, ct);
            }

            audit.Log(uid, "serial.status", "component_serial", id,
                details: heldAfter is { } t ? $"{newStatus} (technician #{t})" : newStatus.ToString(),
                ip: http.GetIp());
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (StockException ex) { await tx.RollbackAsync(ct); return TypedResults.BadRequest(ex.Message); }

        var row = await SingleRowAsync(db, id, ct);
        return TypedResults.Ok(ToDto(row!.c, row.ItemCode, row.Name, row.TechName, row.ServiceNo));
    }

    /// <summary>Receive a unit back at the counter by hand. Unused stock goes onto the shelf, which
    /// the status alone says; received as defective it is at the service centre but is not stock
    /// anyone can issue, so it goes no further than DEFECTIVE and lands on no shelf.
    ///
    /// No quantity moves either way, for the same reason a manual status change moves none: the shelf
    /// is counted by unit.</summary>
    private static async Task<Results<Ok<ComponentSerialDto>, NotFound, BadRequest<string>>> ReceiveAsync(
        long id, [FromBody] ReceiveSerialReturnRequest req, ClaimsPrincipal user, AppDbContext db,
        SerialService serial, StockLedgerService ledger, IAuditService audit, HttpContext http,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Remarks))
            return TypedResults.BadRequest("Remarks are required to receive a return.");

        var before = await db.ComponentSerials.AsNoTracking()
            .Where(c => c.Id == id)
            .Select(c => new { c.Status, c.PartId, c.SerialNumber })
            .FirstOrDefaultAsync(ct);
        if (before is null) return TypedResults.NotFound();

        user.TryGetUserId(out var uid);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            var updated = await serial.ReceiveReturnAsync(id, req.Defective, uid, req.Remarks.Trim(), ct);
            if (updated is null) { await tx.RollbackAsync(ct); return TypedResults.NotFound(); }

            if (!SerialService.IsOnShelf(before.Status) && SerialService.IsOnShelf(updated.Status))
                ledger.ShelveUnit(before.PartId, uid, "SERIAL_STATUS", id, before.SerialNumber,
                    $"{before.SerialNumber}: {before.Status} -> {updated.Status} (received at counter)");

            audit.Log(uid, "serial.receive", "component_serial", id,
                details: updated.Status.ToString().ToUpperInvariant(), ip: http.GetIp());
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (StockException ex) { await tx.RollbackAsync(ct); return TypedResults.BadRequest(ex.Message); }

        var row = await SingleRowAsync(db, id, ct);
        return TypedResults.Ok(ToDto(row!.c, row.ItemCode, row.Name, row.TechName, row.ServiceNo));
    }

    // ---- helpers ----

    /// <summary>Who may hold stock. Role-scoped the same way the issue paths are, and for the same
    /// reason: a balance keyed to somebody who is not a technician is stock on a page nobody looks
    /// at.</summary>
    private static Task<bool> IsTechnicianAsync(AppDbContext db, long userId, CancellationToken ct) =>
        (from ur in db.UserRoles
         join r in db.Roles on ur.RoleId equals r.Id
         where ur.UserId == userId && r.Name == RoleNames.Technician
         select ur.UserId).AnyAsync(ct);

    private record SerialRow(ComponentSerial c, string ItemCode, string Name, string? TechName, string? ServiceNo);

    private static async Task<SerialRow?> SingleRowAsync(AppDbContext db, long id, CancellationToken ct)
    {
        var row = await (from c in db.ComponentSerials.AsNoTracking()
                         join p in db.Parts on c.PartId equals p.Id
                         join u in db.Users on c.TechnicianId equals (long?)u.Id into ug
                         from u in ug.DefaultIfEmpty()
                         join j in db.Services.Where(s => !s.IsDeleted) on c.CurrentServiceJobId equals (long?)j.Id into jg
                         from j in jg.DefaultIfEmpty()
                         where c.Id == id
                         select new { c, p.ItemCode, p.Name, TechName = u != null ? (u.FullName ?? u.Username) : null,
                                      ServiceNo = j != null ? j.ServiceNo : null })
            .FirstOrDefaultAsync(ct);
        return row is null ? null : new SerialRow(row.c, row.ItemCode, row.Name, row.TechName, row.ServiceNo);
    }

    private static ComponentSerialDto ToDto(ComponentSerial c, string itemCode, string partName, string? techName,
        string? serviceNo = null) =>
        new(c.Id, c.PartId, itemCode, partName, c.SerialNumber, c.Status.ToString(), c.OwnerType.ToString(),
            c.OwnerRef, c.TechnicianId, techName, c.LastUpdatedAt, c.CreatedAt,
            c.CustomerId, c.CurrentServiceJobId, serviceNo);
}
