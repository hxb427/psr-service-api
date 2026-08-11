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
                select new { r, p.ItemCode, p.Name, p.IsSerialTracked,
                    Username = u != null ? u.Username : null,
                    RequesterIsField = u != null && u.IsFieldTechnician };

        if (!manage) q = q.Where(x => x.r.RequestedByUserId == uid);
        if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<StockRequestStatus>(status, true, out var st))
            q = q.Where(x => x.r.Status == st);

        var rows = await q.OrderByDescending(x => x.r.Id).ToListAsync(ct);
        var items = rows.Select(x => new StockRequestDto(
            x.r.Id, x.r.RequestNo, x.r.RequestedByUserId, x.Username, x.r.RequestDate,
            x.r.PartId, x.ItemCode, x.Name, x.r.QtyRequested, x.r.QtyIssued,
            x.r.Status.ToString(), x.r.IssuedDate, x.r.Remarks, x.r.Courier, x.r.TrackingNo,
            x.IsSerialTracked, x.RequesterIsField)).ToList();
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

        // Serial capture applies only when a serial-tracked part goes to a field technician.
        var needSerials = part.IsSerialTracked && requester is { IsFieldTechnician: true };
        var serials = (req.Serials ?? [])
            .Select(s => s?.Trim() ?? string.Empty).Where(s => s.Length > 0).ToList();
        if (needSerials)
        {
            if (serials.Count != issueQty)
                return TypedResults.BadRequest($"Enter exactly {issueQty} serial number(s) for this serial-tracked part.");
            var conflicts = await serial.FindIssueConflictsAsync(r.PartId, serials, ct);
            if (conflicts.Count > 0)
                return TypedResults.BadRequest("Cannot issue: " +
                    string.Join("; ", conflicts.Select(kv => $"{kv.Key} — {kv.Value}")));
        }

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
                          select new TechInventoryRowDto(p.Id, p.ItemCode, p.Name, p.Unit, b.OnHand))
            .ToListAsync(ct);
        return TypedResults.Ok(rows);
    }

    private static async Task<StockRequestDto> ToDtoAsync(AppDbContext db, long id, CancellationToken ct)
    {
        var x = await (from r in db.StockRequests.AsNoTracking()
                       join p in db.Parts on r.PartId equals p.Id
                       join u in db.Users on r.RequestedByUserId equals u.Id into ug
                       from u in ug.DefaultIfEmpty()
                       where r.Id == id
                       select new { r, p.ItemCode, p.Name, p.IsSerialTracked,
                           Username = u != null ? u.Username : null,
                           RequesterIsField = u != null && u.IsFieldTechnician })
            .FirstAsync(ct);
        return new StockRequestDto(x.r.Id, x.r.RequestNo, x.r.RequestedByUserId, x.Username, x.r.RequestDate,
            x.r.PartId, x.ItemCode, x.Name, x.r.QtyRequested, x.r.QtyIssued, x.r.Status.ToString(), x.r.IssuedDate, x.r.Remarks,
            x.r.Courier, x.r.TrackingNo, x.IsSerialTracked, x.RequesterIsField);
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
