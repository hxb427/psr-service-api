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
        NumberSequenceService seq, SerialService serial, CancellationToken ct)
    {
        var part = await db.Parts.FirstOrDefaultAsync(p => p.Id == req.PartId, ct);
        if (part is null) return TypedResults.NotFound();
        user.TryGetUserId(out var uid);

        var serialIds = (req.SerialIds ?? []).Distinct().ToList();
        var requester = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == uid, ct);
        // Field technicians must enumerate serials when returning a serial-tracked part.
        if (part.IsSerialTracked && requester is { IsFieldTechnician: true } && serialIds.Count != req.Qty)
            return TypedResults.BadRequest($"Select exactly {req.Qty} serial(s) for this serial-tracked part.");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        StockReturn ret;
        try
        {
            var no = await seq.NextAsync(SequenceKeys.StockReturn, ct);
            ret = new StockReturn
            {
                ReturnNo = no, TechnicianId = uid, PartId = req.PartId, Qty = req.Qty,
                Remarks = req.Remarks, Courier = req.Courier?.Trim(), TrackingNo = req.TrackingNo?.Trim(),
            };
            db.StockReturns.Add(ret);
            await db.SaveChangesAsync(ct);

            foreach (var sid in serialIds)
            {
                var cs = await db.ComponentSerials.AsNoTracking().FirstOrDefaultAsync(c => c.Id == sid, ct);
                if (cs is null || cs.PartId != req.PartId)
                { await tx.RollbackAsync(ct); return TypedResults.BadRequest($"Serial id {sid} is not a unit of this part."); }
                var defective = cs.Status is SerialStatus.Defective or SerialStatus.Collected;

                var err = await serial.ShipReturnSerialAsync(sid, uid, uid, ct);
                if (err is not null) { await tx.RollbackAsync(ct); return TypedResults.BadRequest(err); }

                db.StockReturnSerials.Add(new StockReturnSerial
                { StockReturnId = ret.Id, ComponentSerialId = sid, Defective = defective });
            }
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (StockException ex) { await tx.RollbackAsync(ct); return TypedResults.BadRequest(ex.Message); }

        return TypedResults.Created($"/stock-returns/{ret.Id}", await ToDtoAsync(db, ret.Id, ct));
    }

    private static async Task<Results<Ok<StockReturnDto>, NotFound, BadRequest<string>>> AcknowledgeAsync(
        long id, ClaimsPrincipal user, AppDbContext db, StockLedgerService ledger,
        SerialService serial, IAuditService audit, HttpContext http, CancellationToken ct)
    {
        var r = await db.StockReturns.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (r is null) return TypedResults.NotFound();
        if (r.Status != StockReturnStatus.Pending) return TypedResults.BadRequest($"Return is already {r.Status}.");

        user.TryGetUserId(out var uid);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            await ledger.ReturnToStockAsync(r.PartId, r.TechnicianId, r.Qty, uid, "STOCK_RETURN", r.Id, ct);

            // Serial-tracked units on the shipment arrive back at the service center.
            var serialLines = await db.StockReturnSerials.AsNoTracking()
                .Where(s => s.StockReturnId == r.Id).ToListAsync(ct);
            foreach (var line in serialLines)
                await serial.ReceiveReturnAsync(line.ComponentSerialId, line.Defective, uid,
                    $"Return {r.ReturnNo} received at service center", ct);

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
        long id, ClaimsPrincipal user, AppDbContext db, SerialService serial,
        IAuditService audit, HttpContext http, CancellationToken ct)
    {
        var r = await db.StockReturns.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (r is null) return TypedResults.NotFound();
        if (r.Status != StockReturnStatus.Pending) return TypedResults.BadRequest($"Return is already {r.Status}.");

        user.TryGetUserId(out var uid);
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // Shipment never arrived — its serial-tracked units are lost in transit.
        var serialLines = await db.StockReturnSerials.AsNoTracking()
            .Where(s => s.StockReturnId == r.Id).ToListAsync(ct);
        foreach (var line in serialLines)
            await serial.ChangeStatusAsync(line.ComponentSerialId, SerialStatus.Missing, uid,
                $"Return {r.ReturnNo} reported missing in transit", ct);

        r.Status = StockReturnStatus.Missing;
        r.AcknowledgedByUserId = uid;
        r.AcknowledgedDate = DateTime.UtcNow;
        audit.Log(uid, "stock-return.missing", "stock_return", r.Id, ip: http.GetIp());
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
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
        var serials = await (from s in db.StockReturnSerials.AsNoTracking()
                             where s.StockReturnId == id
                             join c in db.ComponentSerials on s.ComponentSerialId equals c.Id
                             select new StockReturnSerialDto(c.Id, c.SerialNumber, s.Defective, c.Status.ToString()))
            .ToListAsync(ct);
        return new StockReturnDto(x.r.Id, x.r.ReturnNo, x.r.TechnicianId, x.Username, x.r.PartId, x.ItemCode, x.Name,
            x.r.Qty, x.r.Status.ToString(), x.r.AcknowledgedDate, x.r.Remarks, x.r.CreatedAt,
            x.r.Courier, x.r.TrackingNo, serials);
    }
}
