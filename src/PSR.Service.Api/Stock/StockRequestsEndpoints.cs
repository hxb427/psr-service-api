using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PSR.Service.Api.Audit;
using PSR.Service.Api.Auth;
using PSR.Service.Api.Data;
using PSR.Service.Api.Data.Entities;

namespace PSR.Service.Api.Stock;

public static class StockRequestsEndpoints
{
    public static IEndpointRouteBuilder MapStockRequestEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/stock-requests").WithTags("stock-requests").RequireAuthorization();

        group.MapGet("/", ListAsync);
        group.MapPost("/", CreateAsync);
        group.MapPost("/{id:long}/issue", IssueAsync).RequireAuthorization("StockManage");
        // Hand stock over without a request having been raised first. Writes the request itself, so the
        // register and every report over it keep working off one shape.
        group.MapPost("/direct-issue", DirectIssueAsync).RequireAuthorization("StockManage");
        group.MapGet("/technicians", TechniciansAsync).RequireAuthorization("StockManage");
        group.MapPost("/{id:long}/cancel", CancelAsync);
        group.MapDelete("/{id:long}", DeleteAsync).RequireAuthorization("StockManage");
        group.MapGet("/inventory/me", MyInventoryAsync);
        group.MapGet("/inventory/{technicianId:long}", TechInventoryAsync).RequireAuthorization("StockManage");
        // Everyone's holdings in one call. The per-technician route above answers a single holder;
        // asking it once per technician to build a team view would be a request per head.
        group.MapGet("/inventory", AllInventoryAsync).RequireAuthorization("StockManage");

        return app;
    }

    private static async Task<Ok<List<StockRequestDto>>> ListAsync(
        AppDbContext db, ClaimsPrincipal user, string? status, CancellationToken ct)
    {
        user.TryGetUserId(out var uid);
        var manage = StockRoles.CanManage(user);

        var q = from r in db.StockRequests.AsNoTracking()
                join p in db.Parts on r.PartId equals p.Id
                join u in db.Users on r.RequestedByUserId equals u.Id into ug
                from u in ug.DefaultIfEmpty()
                join iu in db.Users on r.IssuedByUserId equals iu.Id into iug
                from iu in iug.DefaultIfEmpty()
                select new { r, p.ItemCode, p.Name, p.IsSerialTracked,
                    Username = u != null ? u.Username : null,
                    RequesterIsField = u != null && u.IsFieldTechnician,
                    IssuedByUsername = iu != null ? iu.Username : null };

        if (!manage) q = q.Where(x => x.r.RequestedByUserId == uid);
        if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<StockRequestStatus>(status, true, out var st))
            q = q.Where(x => x.r.Status == st);

        var rows = await q.OrderByDescending(x => x.r.Id).ToListAsync(ct);
        var items = rows.Select(x => new StockRequestDto(
            x.r.Id, x.r.RequestNo, x.r.RequestedByUserId, x.Username, x.r.RequestDate,
            x.r.PartId, x.ItemCode, x.Name, x.r.QtyRequested, x.r.QtyIssued,
            x.r.Status.ToString(), x.r.IssuedDate, x.r.Remarks, x.r.Courier, x.r.TrackingNo,
            x.IsSerialTracked, x.RequesterIsField, x.r.IssuedByUserId, x.IssuedByUsername)).ToList();
        return TypedResults.Ok(items);
    }

    private static async Task<Results<Created<StockRequestDto>, NotFound, BadRequest<string>>> CreateAsync(
        [FromBody] CreateStockRequestRequest req, ClaimsPrincipal user, AppDbContext db,
        NumberSequenceService seq, IAuditService audit, HttpContext http, CancellationToken ct)
    {
        var part = await db.Parts.FirstOrDefaultAsync(p => p.Id == req.PartId, ct);
        if (part is null) return TypedResults.NotFound();
        if (!part.IsActive) return TypedResults.BadRequest("Part is inactive.");
        user.TryGetUserId(out var uid);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        StockRequest reqEntity;
        try
        {
            var no = await seq.NextAsync(SequenceKeys.StockRequest, ct);
            reqEntity = new StockRequest
            {
                RequestNo = no, RequestedByUserId = uid, PartId = req.PartId,
                QtyRequested = req.Qty, RequestDate = DateTime.UtcNow, Remarks = req.Remarks,
            };
            db.StockRequests.Add(reqEntity);
            await db.SaveChangesAsync(ct);
            audit.Log(uid, "stock-request.create", "stock_request", reqEntity.Id,
                details: $"{no} {part.ItemCode} x{req.Qty}", ip: http.GetIp());
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (StockException ex) { await tx.RollbackAsync(ct); return TypedResults.BadRequest(ex.Message); }

        var requesterIsField = await db.Users.Where(u => u.Id == uid).Select(u => u.IsFieldTechnician).FirstOrDefaultAsync(ct);
        var dto = new StockRequestDto(reqEntity.Id, reqEntity.RequestNo, uid, null, reqEntity.RequestDate,
            part.Id, part.ItemCode, part.Name, reqEntity.QtyRequested, 0, reqEntity.Status.ToString(), null, reqEntity.Remarks, null, null,
            part.IsSerialTracked, requesterIsField);
        return TypedResults.Created($"/stock-requests/{reqEntity.Id}", dto);
    }

    private static async Task<Results<Ok<StockRequestDto>, NotFound, BadRequest<string>>> IssueAsync(
        long id, [FromBody] IssueRequest req, ClaimsPrincipal user, AppDbContext db,
        StockLedgerService ledger, SerialService serial, IAuditService audit, HttpContext http, CancellationToken ct)
    {
        var r = await db.StockRequests.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (r is null) return TypedResults.NotFound();
        if (r.Status is StockRequestStatus.Cancelled or StockRequestStatus.Issued)
            return TypedResults.BadRequest($"Request is {r.Status} and cannot be issued.");

        var remaining = r.QtyRequested - r.QtyIssued;
        var issueQty = Math.Min(req.Qty, remaining);
        if (issueQty <= 0) return TypedResults.BadRequest("Nothing left to issue on this request.");

        var part = await db.Parts.FirstOrDefaultAsync(p => p.Id == r.PartId, ct);
        if (part is null) return TypedResults.BadRequest("Part not found.");
        var requester = await db.Users.FirstOrDefaultAsync(u => u.Id == r.RequestedByUserId, ct);

        var (needSerials, serials, serialError) =
            await CheckSerialsAsync(part, requester, issueQty, req.Serials, serial, ct);
        if (serialError is not null) return TypedResults.BadRequest(serialError);

        user.TryGetUserId(out var uid);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            var movement = await ledger.IssueAsync(r.PartId, r.RequestedByUserId, issueQty, uid, "STOCK_REQUEST", r.Id, ct);
            if (needSerials)
            {
                await db.SaveChangesAsync(ct);   // assign the movement id for serial link rows
                await serial.CaptureOnIssueAsync(movement.Id, r.PartId, part.Name, r.RequestedByUserId,
                    requester!.FullName ?? requester.Username, serials, uid, ct);
            }
            r.QtyIssued += issueQty;
            r.Status = r.QtyIssued >= r.QtyRequested ? StockRequestStatus.Issued : StockRequestStatus.Partial;
            r.IssuedByUserId = uid;
            r.IssuedDate = DateTime.UtcNow;
            if (!string.IsNullOrWhiteSpace(req.Courier)) r.Courier = req.Courier.Trim();
            if (!string.IsNullOrWhiteSpace(req.TrackingNo)) r.TrackingNo = req.TrackingNo.Trim();
            audit.Log(uid, "stock-request.issue", "stock_request", r.Id, details: $"+{issueQty}", ip: http.GetIp());
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (StockException ex) { await tx.RollbackAsync(ct); return TypedResults.BadRequest(ex.Message); }

        return TypedResults.Ok(await ToDtoAsync(db, r.Id, ct));
    }

    private static async Task<Results<Ok<StockRequestDto>, NotFound, BadRequest<string>, ForbidHttpResult>> CancelAsync(
        long id, ClaimsPrincipal user, AppDbContext db, IAuditService audit, HttpContext http, CancellationToken ct)
    {
        var r = await db.StockRequests.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (r is null) return TypedResults.NotFound();

        user.TryGetUserId(out var uid);
        if (!StockRoles.CanManage(user) && r.RequestedByUserId != uid) return TypedResults.Forbid();
        if (r.QtyIssued > 0) return TypedResults.BadRequest("Cannot cancel — part of this request has already been issued.");
        if (r.Status == StockRequestStatus.Cancelled) return TypedResults.BadRequest("Already cancelled.");

        r.Status = StockRequestStatus.Cancelled;
        audit.Log(uid, "stock-request.cancel", "stock_request", r.Id, ip: http.GetIp());
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(await ToDtoAsync(db, r.Id, ct));
    }

    private static Task<Ok<List<TechInventoryRowDto>>> MyInventoryAsync(AppDbContext db, ClaimsPrincipal user, CancellationToken ct)
    {
        user.TryGetUserId(out var uid);
        return InventoryAsync(db, uid, ct);
    }

    private static Task<Ok<List<TechInventoryRowDto>>> TechInventoryAsync(long technicianId, AppDbContext db, CancellationToken ct)
        => InventoryAsync(db, technicianId, ct);

    /// <summary>Every technician's holdings, one row per (technician, part).
    ///
    /// TechnicianId 0 is the warehouse, not a person, so it is excluded — the warehouse has its own
    /// page and would otherwise appear as a nameless holder. Zero and negative balances are dropped
    /// for the same reason the single-technician view drops them: a settled part is not a holding.</summary>
    private static async Task<Ok<List<TechnicianStockRowDto>>> AllInventoryAsync(AppDbContext db, CancellationToken ct)
    {
        var rows = await (from b in db.StockBalances.AsNoTracking()
                          join p in db.Parts on b.PartId equals p.Id
                          join u in db.Users on b.TechnicianId equals u.Id
                          where b.TechnicianId != StockBalance.Warehouse && b.OnHand > 0
                          orderby u.Username, p.ItemCode
                          select new TechnicianStockRowDto(
                              u.Id, u.Username, p.Id, p.ItemCode, p.Name, p.Unit, b.OnHand))
            .ToListAsync(ct);
        return TypedResults.Ok(rows);
    }

    private static async Task<Ok<List<TechInventoryRowDto>>> InventoryAsync(AppDbContext db, long technicianId, CancellationToken ct)
    {
        var rows = await (from b in db.StockBalances.AsNoTracking()
                          join p in db.Parts on b.PartId equals p.Id
                          where b.TechnicianId == technicianId && b.OnHand > 0
                          orderby p.ItemCode
                          select new TechInventoryRowDto(p.Id, p.ItemCode, p.Name, p.Unit, b.OnHand, p.IsSerialTracked))
            .ToListAsync(ct);
        return TypedResults.Ok(rows);
    }

    /// <summary>Serial capture rules, shared by issuing against a request and issuing directly.
    /// Capture applies only when a serial-tracked part goes to a FIELD technician — in-house holdings
    /// are not tracked unit by unit. Returns the message to hand back, or null when the issue may go
    /// ahead.</summary>
    private static async Task<(bool NeedSerials, List<string> Serials, string? Error)> CheckSerialsAsync(
        Part part, User? holder, int qty, IReadOnlyList<string>? supplied,
        SerialService serial, CancellationToken ct)
    {
        var needSerials = part.IsSerialTracked && holder is { IsFieldTechnician: true };
        var serials = (supplied ?? [])
            .Select(s => s?.Trim() ?? string.Empty).Where(s => s.Length > 0).ToList();
        if (!needSerials) return (false, serials, null);

        if (serials.Count != qty)
            return (true, serials, $"Enter exactly {qty} serial number(s) for this serial-tracked part.");
        var conflicts = await serial.FindIssueConflictsAsync(part.Id, serials, ct);
        if (conflicts.Count > 0)
            return (true, serials,
                "Cannot issue: " + string.Join("; ", conflicts.Select(kv => $"{kv.Key} — {kv.Value}")));
        return (true, serials, null);
    }

    /// <summary>Issue stock straight to a technician, no request needed.
    ///
    /// The request row is still written — issued in full, dated now, attributed to the technician as
    /// requester and to the issuer as who handed it over. That keeps the register a single list: the
    /// alternative was a stock movement with nothing in the register explaining it, which is exactly
    /// the gap someone reconciling a technician holding has to close by hand.</summary>
    private static async Task<Results<Created<StockRequestDto>, NotFound, BadRequest<string>>> DirectIssueAsync(
        [FromBody] DirectIssueRequest req, ClaimsPrincipal user, AppDbContext db,
        StockLedgerService ledger, SerialService serial, NumberSequenceService seq,
        IAuditService audit, HttpContext http, CancellationToken ct)
    {
        if (req.Qty < 1) return TypedResults.BadRequest("Quantity must be at least 1.");

        var part = await db.Parts.FirstOrDefaultAsync(p => p.Id == req.PartId, ct);
        if (part is null) return TypedResults.NotFound();
        if (!part.IsActive) return TypedResults.BadRequest("Part is inactive.");

        var technician = await db.Users.FirstOrDefaultAsync(u => u.Id == req.TechnicianId, ct);
        if (technician is null) return TypedResults.NotFound();
        if (!technician.IsActive) return TypedResults.BadRequest("That account is deactivated.");
        // Balances are keyed by user id, so issuing to a non-technician would create a holding on a page
        // that only lists technicians — stock that exists but that nobody can see or return.
        if (!await IsTechnicianAsync(db, technician.Id, ct))
            return TypedResults.BadRequest($"{technician.Username} is not a technician and cannot hold stock.");

        var (needSerials, serials, serialError) =
            await CheckSerialsAsync(part, technician, req.Qty, req.Serials, serial, ct);
        if (serialError is not null) return TypedResults.BadRequest(serialError);

        user.TryGetUserId(out var uid);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        StockRequest entity;
        try
        {
            entity = new StockRequest
            {
                RequestNo = await seq.NextAsync(SequenceKeys.StockRequest, ct),
                RequestedByUserId = technician.Id,
                PartId = part.Id,
                QtyRequested = req.Qty,
                QtyIssued = req.Qty,
                RequestDate = DateTime.UtcNow,
                IssuedDate = DateTime.UtcNow,
                IssuedByUserId = uid,
                Status = StockRequestStatus.Issued,
                Remarks = req.Remarks,
                Courier = string.IsNullOrWhiteSpace(req.Courier) ? null : req.Courier.Trim(),
                TrackingNo = string.IsNullOrWhiteSpace(req.TrackingNo) ? null : req.TrackingNo.Trim(),
            };
            db.StockRequests.Add(entity);
            // Needs the request id before the movement can reference it.
            await db.SaveChangesAsync(ct);

            var movement = await ledger.IssueAsync(part.Id, technician.Id, req.Qty, uid, "STOCK_REQUEST", entity.Id, ct);
            if (needSerials)
            {
                await db.SaveChangesAsync(ct);   // assign the movement id for serial link rows
                await serial.CaptureOnIssueAsync(movement.Id, part.Id, part.Name, technician.Id,
                    technician.FullName ?? technician.Username, serials, uid, ct);
            }

            audit.Log(uid, "stock-request.direct-issue", "stock_request", entity.Id,
                details: $"{entity.RequestNo} {part.ItemCode} x{req.Qty} to {technician.Username} (no request raised)",
                ip: http.GetIp());
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (StockException ex) { await tx.RollbackAsync(ct); return TypedResults.BadRequest(ex.Message); }

        return TypedResults.Created($"/stock-requests/{entity.Id}", await ToDtoAsync(db, entity.Id, ct));
    }

    private static Task<bool> IsTechnicianAsync(AppDbContext db, long userId, CancellationToken ct) =>
        (from ur in db.UserRoles
         join r in db.Roles on ur.RoleId equals r.Id
         where ur.UserId == userId && r.Name == RoleNames.Technician
         select ur.UserId).AnyAsync(ct);

    /// <summary>Who stock can be issued to. Role-scoped rather than the admin-only user list, the same
    /// way the assignment picker is — the store needs the names, not the accounts.</summary>
    private static async Task<Ok<List<StockTechnicianDto>>> TechniciansAsync(AppDbContext db, CancellationToken ct)
    {
        var rows = await (from u in db.Users.AsNoTracking()
                          join ur in db.UserRoles on u.Id equals ur.UserId
                          join r in db.Roles on ur.RoleId equals r.Id
                          where u.IsActive && r.Name == RoleNames.Technician
                          orderby u.Username
                          select new StockTechnicianDto(u.Id, u.Username, u.FullName, u.IsFieldTechnician))
            .Distinct().ToListAsync(ct);
        return TypedResults.Ok(rows);
    }

    private static async Task<StockRequestDto> ToDtoAsync(AppDbContext db, long id, CancellationToken ct)
    {
        var x = await (from r in db.StockRequests.AsNoTracking()
                       join p in db.Parts on r.PartId equals p.Id
                       join u in db.Users on r.RequestedByUserId equals u.Id into ug
                       from u in ug.DefaultIfEmpty()
                       join iu in db.Users on r.IssuedByUserId equals iu.Id into iug
                       from iu in iug.DefaultIfEmpty()
                       where r.Id == id
                       select new { r, p.ItemCode, p.Name, p.IsSerialTracked,
                           Username = u != null ? u.Username : null,
                           RequesterIsField = u != null && u.IsFieldTechnician,
                           IssuedByUsername = iu != null ? iu.Username : null })
            .FirstAsync(ct);
        return new StockRequestDto(x.r.Id, x.r.RequestNo, x.r.RequestedByUserId, x.Username, x.r.RequestDate,
            x.r.PartId, x.ItemCode, x.Name, x.r.QtyRequested, x.r.QtyIssued, x.r.Status.ToString(), x.r.IssuedDate, x.r.Remarks,
            x.r.Courier, x.r.TrackingNo, x.IsSerialTracked, x.RequesterIsField,
            x.r.IssuedByUserId, x.IssuedByUsername);
    }

    private static async Task<Results<NoContent, NotFound, BadRequest<string>, ForbidHttpResult>> DeleteAsync(
        long id, ClaimsPrincipal user, AppDbContext db, IAuditService audit, HttpContext http, CancellationToken ct)
    {
        var r = await db.StockRequests.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (r is null) return TypedResults.NotFound();
        if (r.QtyIssued > 0) return TypedResults.BadRequest("Cannot delete — part of this request was already issued.");

        user.TryGetUserId(out var uid);
        db.StockRequests.Remove(r);
        audit.Log(uid, "stock-request.delete", "stock_request", r.Id, ip: http.GetIp());
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }
}
