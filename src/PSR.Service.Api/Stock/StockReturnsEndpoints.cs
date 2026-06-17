using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PSR.Service.Api.Audit;
using PSR.Service.Api.Auth;
using PSR.Service.Api.Data;
using PSR.Service.Api.Data.Entities;

namespace PSR.Service.Api.Stock;

public static class StockReturnsEndpoints
{
    public static IEndpointRouteBuilder MapStockReturnEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/stock-returns").WithTags("stock-returns").RequireAuthorization();

        group.MapGet("/", ListAsync);
        group.MapPost("/", CreateAsync);
        group.MapPost("/{id:long}/acknowledge", AcknowledgeAsync).RequireAuthorization("ReturnAck");
        group.MapPost("/{id:long}/missing", MissingAsync).RequireAuthorization("ReturnAck");

        return app;
    }

    private static async Task<Ok<List<StockReturnDto>>> ListAsync(
        AppDbContext db, ClaimsPrincipal user, string? status, CancellationToken ct)
    {
        user.TryGetUserId(out var uid);
        var manage = StockRoles.CanManage(user);

        var q = from r in db.StockReturns.AsNoTracking()
                join p in db.Parts on r.PartId equals p.Id
                join u in db.Users on r.TechnicianId equals u.Id into ug
                from u in ug.DefaultIfEmpty()
                select new { r, p.ItemCode, p.Name, Username = u != null ? u.Username : null };

        if (!manage) q = q.Where(x => x.r.TechnicianId == uid);
        if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<StockReturnStatus>(status, true, out var st))
            q = q.Where(x => x.r.Status == st);

        var rows = await q.OrderByDescending(x => x.r.Id).ToListAsync(ct);
        var items = rows.Select(x => new StockReturnDto(
            x.r.Id, x.r.ReturnNo, x.r.TechnicianId, x.Username, x.r.PartId, x.ItemCode, x.Name,
            x.r.Qty, x.r.Status.ToString(), x.r.AcknowledgedDate, x.r.Remarks, x.r.CreatedAt)).ToList();
        return TypedResults.Ok(items);
    }

    private static async Task<Results<Created<StockReturnDto>, NotFound, BadRequest<string>>> CreateAsync(
        [FromBody] CreateStockReturnRequest req, ClaimsPrincipal user, AppDbContext db,
        NumberSequenceService seq, CancellationToken ct)
    {
        var part = await db.Parts.FirstOrDefaultAsync(p => p.Id == req.PartId, ct);
        if (part is null) return TypedResults.NotFound();
        user.TryGetUserId(out var uid);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        StockReturn ret;
        try
        {
            var no = await seq.NextAsync(SequenceKeys.StockReturn, ct);
            ret = new StockReturn { ReturnNo = no, TechnicianId = uid, PartId = req.PartId, Qty = req.Qty, Remarks = req.Remarks };
            db.StockReturns.Add(ret);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (StockException ex) { await tx.RollbackAsync(ct); return TypedResults.BadRequest(ex.Message); }

        var dto = new StockReturnDto(ret.Id, ret.ReturnNo, uid, null, part.Id, part.ItemCode, part.Name,
            ret.Qty, ret.Status.ToString(), null, ret.Remarks, ret.CreatedAt);
        return TypedResults.Created($"/stock-returns/{ret.Id}", dto);
    }

    private static async Task<Results<Ok<StockReturnDto>, NotFound, BadRequest<string>>> AcknowledgeAsync(
        long id, ClaimsPrincipal user, AppDbContext db, StockLedgerService ledger,
        IAuditService audit, HttpContext http, CancellationToken ct)
    {
        var r = await db.StockReturns.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (r is null) return TypedResults.NotFound();
        if (r.Status != StockReturnStatus.Pending) return TypedResults.BadRequest($"Return is already {r.Status}.");

        user.TryGetUserId(out var uid);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            await ledger.ReturnToStockAsync(r.PartId, r.TechnicianId, r.Qty, uid, "STOCK_RETURN", r.Id, ct);
            r.Status = StockReturnStatus.Stocked;
            r.AcknowledgedByUserId = uid;
            r.AcknowledgedDate = DateTime.UtcNow;
            audit.Log(uid, "stock-return.acknowledge", "stock_return", r.Id, ip: http.GetIp());
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (StockException ex) { await tx.RollbackAsync(ct); return TypedResults.BadRequest(ex.Message); }

        return TypedResults.Ok(await ToDtoAsync(db, r.Id, ct));
    }

    private static async Task<Results<Ok<StockReturnDto>, NotFound, BadRequest<string>>> MissingAsync(
        long id, ClaimsPrincipal user, AppDbContext db, IAuditService audit, HttpContext http, CancellationToken ct)
    {
        var r = await db.StockReturns.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (r is null) return TypedResults.NotFound();
        if (r.Status != StockReturnStatus.Pending) return TypedResults.BadRequest($"Return is already {r.Status}.");

        user.TryGetUserId(out var uid);
        r.Status = StockReturnStatus.Missing;
        r.AcknowledgedByUserId = uid;
        r.AcknowledgedDate = DateTime.UtcNow;
        audit.Log(uid, "stock-return.missing", "stock_return", r.Id, ip: http.GetIp());
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(await ToDtoAsync(db, r.Id, ct));
    }

    private static async Task<StockReturnDto> ToDtoAsync(AppDbContext db, long id, CancellationToken ct)
    {
        var x = await (from r in db.StockReturns.AsNoTracking()
                       join p in db.Parts on r.PartId equals p.Id
                       join u in db.Users on r.TechnicianId equals u.Id into ug
                       from u in ug.DefaultIfEmpty()
                       where r.Id == id
                       select new { r, p.ItemCode, p.Name, Username = u != null ? u.Username : null })
            .FirstAsync(ct);
        return new StockReturnDto(x.r.Id, x.r.ReturnNo, x.r.TechnicianId, x.Username, x.r.PartId, x.ItemCode, x.Name,
            x.r.Qty, x.r.Status.ToString(), x.r.AcknowledgedDate, x.r.Remarks, x.r.CreatedAt);
    }
}
