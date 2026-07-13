using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PSR.Service.Api.Audit;
using PSR.Service.Api.Auth;
using PSR.Service.Api.Data;
using PSR.Service.Api.Data.Entities;

namespace PSR.Service.Api.Stock;

/// <summary>Technician acknowledgement of issued stock (legacy "pending receipts").
/// Quantities are declarative (no balance mutation — discrepancies are resolved by admin
/// adjustment/returns); serial-tracked units are acknowledged per-serial and flip custody.</summary>
public static class StockAcksEndpoints
{
    public static IEndpointRouteBuilder MapStockAckEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/stock-acks").WithTags("stock-acks").RequireAuthorization();

        group.MapGet("/pending", PendingAsync);
        group.MapPost("/{movementId:long}", AckAsync);

        return app;
    }

    /// <summary>Issue movements addressed to the caller that have no acknowledgement yet,
    /// with any serial lines awaiting per-serial ack.</summary>
    private static async Task<Ok<List<PendingIssueDto>>> PendingAsync(
        AppDbContext db, ClaimsPrincipal user, CancellationToken ct)
    {
        user.TryGetUserId(out var uid);

        var rows = await (from m in db.StockMovements.AsNoTracking()
                          where m.TechnicianId == uid && m.MovementType == MovementType.Issue
                          join a in db.StockIssueAcks on m.Id equals a.StockMovementId into ag
                          from a in ag.DefaultIfEmpty()
                          where a == null
                          join p in db.Parts on m.PartId equals p.Id
                          join r in db.StockRequests on m.ReferenceId equals (long?)r.Id into rg
                          from r in rg.DefaultIfEmpty()
                          orderby m.Id descending
                          select new
                          {
                              m.Id, m.PartId, p.ItemCode, p.Name, m.Quantity, m.CreatedAt,
                              RequestNo = r != null ? r.RequestNo : null,
                              Courier = r != null ? r.Courier : null,
                              TrackingNo = r != null ? r.TrackingNo : null,
                          }).ToListAsync(ct);

        var movementIds = rows.Select(x => x.Id).ToList();
        var serials = movementIds.Count == 0
            ? []
            : await (from s in db.StockIssueSerials.AsNoTracking()
                     where movementIds.Contains(s.StockMovementId) && s.AckStatus == null
                     join c in db.ComponentSerials on s.ComponentSerialId equals c.Id
                     select new { s.StockMovementId, IssueSerialId = s.Id, ComponentSerialId = c.Id, c.SerialNumber })
                .ToListAsync(ct);

        var items = rows.Select(x => new PendingIssueDto(
            x.Id, x.PartId, x.ItemCode, x.Name, x.Quantity, x.RequestNo, x.Courier, x.TrackingNo, x.CreatedAt,
            serials.Where(s => s.StockMovementId == x.Id)
                .Select(s => new PendingIssueSerialDto(s.IssueSerialId, s.ComponentSerialId, s.SerialNumber)).ToList())).ToList();

        return TypedResults.Ok(items);
    }

    private static async Task<Results<Ok, NotFound, BadRequest<string>>> AckAsync(
        long movementId, [FromBody] AckIssueRequest req, ClaimsPrincipal user, AppDbContext db,
        SerialService serial, IAuditService audit, HttpContext http, CancellationToken ct)
    {
        user.TryGetUserId(out var uid);
        var movement = await db.StockMovements.AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == movementId && m.MovementType == MovementType.Issue, ct);
        if (movement is null) return TypedResults.NotFound();
        if (movement.TechnicianId != uid) return TypedResults.BadRequest("This issue is not addressed to you.");
        if (await db.StockIssueAcks.AnyAsync(a => a.StockMovementId == movementId, ct))
            return TypedResults.BadRequest("This issue is already acknowledged.");
        if (req.QtyReceived + req.QtyDefective + req.QtyMissing != movement.Quantity)
            return TypedResults.BadRequest($"Quantities must add up to the issued {movement.Quantity}.");

        var techName = await db.Users.AsNoTracking().Where(u => u.Id == uid)
            .Select(u => u.FullName ?? u.Username).FirstAsync(ct);

        var serialLines = await db.StockIssueSerials
            .Where(s => s.StockMovementId == movementId).ToListAsync(ct);
        var acks = (req.Serials ?? []).ToDictionary(s => s.IssueSerialId, s => s.Status);
        if (serialLines.Count > 0 && serialLines.Any(l => !acks.ContainsKey(l.Id)))
            return TypedResults.BadRequest("Acknowledge every serial on this issue (Received / Defective / Missing).");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            foreach (var line in serialLines)
            {
                if (!Enum.TryParse<SerialAckStatus>(acks[line.Id], true, out var st))
                    return TypedResults.BadRequest($"Unknown serial ack status '{acks[line.Id]}'.");
                var err = await serial.AckIssueSerialAsync(line.ComponentSerialId, st, uid, techName, ct);
                if (err is not null) { await tx.RollbackAsync(ct); return TypedResults.BadRequest(err); }
                line.AckStatus = st;
            }

            db.StockIssueAcks.Add(new StockIssueAck
            {
                StockMovementId = movementId,
                QtyReceived = req.QtyReceived, QtyDefective = req.QtyDefective, QtyMissing = req.QtyMissing,
                Remarks = req.Remarks?.Trim(), AckedByUserId = uid,
            });
            audit.Log(uid, "stock-ack.create", "stock_movement", movementId,
                details: $"r{req.QtyReceived}/d{req.QtyDefective}/m{req.QtyMissing}", ip: http.GetIp());
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (StockException ex) { await tx.RollbackAsync(ct); return TypedResults.BadRequest(ex.Message); }

        return TypedResults.Ok();
    }
}
