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

        var q = from c in db.ComponentSerials.AsNoTracking()
                join p in db.Parts on c.PartId equals p.Id
                join u in db.Users on c.TechnicianId equals (long?)u.Id into ug
                from u in ug.DefaultIfEmpty()
                select new { c, p.ItemCode, p.Name, TechName = u != null ? (u.FullName ?? u.Username) : null };

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
        var items = rows.Select(x => ToDto(x.c, x.ItemCode, x.Name, x.TechName)).ToList();
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
            ToDto(row.c, row.ItemCode, row.Name, row.TechName), audit));
    }

    private static async Task<Results<Ok<ComponentSerialDto>, NotFound, BadRequest<string>>> ChangeStatusAsync(
        long id, [FromBody] ChangeSerialStatusRequest req, ClaimsPrincipal user, AppDbContext db,
        SerialService serial, IAuditService audit, HttpContext http, CancellationToken ct)
    {
        if (!Enum.TryParse<SerialStatus>(req.Status, true, out var newStatus))
            return TypedResults.BadRequest($"Unknown serial status '{req.Status}'.");
        if (string.IsNullOrWhiteSpace(req.Remarks))
            return TypedResults.BadRequest("Remarks are required for a manual status change.");

        user.TryGetUserId(out var uid);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var updated = await serial.ChangeStatusAsync(id, newStatus, uid, req.Remarks.Trim(), ct);
        if (updated is null) { await tx.RollbackAsync(ct); return TypedResults.NotFound(); }
        audit.Log(uid, "serial.status", "component_serial", id, details: newStatus.ToString(), ip: http.GetIp());
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        var row = await SingleRowAsync(db, id, ct);
        return TypedResults.Ok(ToDto(row!.c, row.ItemCode, row.Name, row.TechName));
    }

    private static async Task<Results<Ok<ComponentSerialDto>, NotFound, BadRequest<string>>> ReceiveAsync(
        long id, [FromBody] ReceiveSerialReturnRequest req, ClaimsPrincipal user, AppDbContext db,
        SerialService serial, IAuditService audit, HttpContext http, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Remarks))
            return TypedResults.BadRequest("Remarks are required to receive a return.");

        user.TryGetUserId(out var uid);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var updated = await serial.ReceiveReturnAsync(id, req.Defective, uid, req.Remarks.Trim(), ct);
        if (updated is null) { await tx.RollbackAsync(ct); return TypedResults.NotFound(); }
        audit.Log(uid, "serial.receive", "component_serial", id,
            details: req.Defective ? "DEFECTIVE" : "RETURNED_TO_SC", ip: http.GetIp());
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        var row = await SingleRowAsync(db, id, ct);
        return TypedResults.Ok(ToDto(row!.c, row.ItemCode, row.Name, row.TechName));
    }

    // ---- helpers ----

    private record SerialRow(ComponentSerial c, string ItemCode, string Name, string? TechName);

    private static async Task<SerialRow?> SingleRowAsync(AppDbContext db, long id, CancellationToken ct)
    {
        var row = await (from c in db.ComponentSerials.AsNoTracking()
                         join p in db.Parts on c.PartId equals p.Id
                         join u in db.Users on c.TechnicianId equals (long?)u.Id into ug
                         from u in ug.DefaultIfEmpty()
                         where c.Id == id
                         select new { c, p.ItemCode, p.Name, TechName = u != null ? (u.FullName ?? u.Username) : null })
            .FirstOrDefaultAsync(ct);
        return row is null ? null : new SerialRow(row.c, row.ItemCode, row.Name, row.TechName);
    }

    private static ComponentSerialDto ToDto(ComponentSerial c, string itemCode, string partName, string? techName) =>
        new(c.Id, c.PartId, itemCode, partName, c.SerialNumber, c.Status.ToString(), c.OwnerType.ToString(),
            c.OwnerRef, c.TechnicianId, techName, c.LastUpdatedAt, c.CreatedAt);
}
