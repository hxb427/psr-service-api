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
///
/// This is the point stock becomes theirs. A courier issue debits the warehouse when it is dispatched
/// but credits nobody until it arrives, so acknowledging is what puts the quantity on the
/// technician's balance and flips each serial into their custody - one event, both ledgers, so they
/// cannot disagree. Only what arrived and is usable counts: received minus defective, exactly as the
/// legacy app derived it.
///
/// Quantities used to be purely declarative here, recorded and then ignored, while the balance had
/// already been credited in full at dispatch. A shipment that arrived short was written down as short
/// and still counted as complete.</summary>
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

    /// <summary>Check the declared quantities against the per-unit verdicts. Returns the message to
    /// hand back, or null when the two agree. Extracted so the rule can be exercised on its own -
    /// it is the join between the quantity ledger and the serial ledger, and a mistake in it lets
    /// the two drift apart silently, which is the whole failure this guard exists to prevent.</summary>
    internal static string? SerialQuantityMismatch(
        IEnumerable<string> serialVerdicts, int qtyReceived, int qtyDefective, int qtyMissing)
    {
        var counted = new Dictionary<SerialAckStatus, int>();
        foreach (var raw in serialVerdicts)
        {
            if (!Enum.TryParse<SerialAckStatus>(raw, true, out var parsed))
                return $"Unknown serial ack status '{raw}'.";
            counted[parsed] = counted.GetValueOrDefault(parsed) + 1;
        }

        var received = counted.GetValueOrDefault(SerialAckStatus.Received);
        var defective = counted.GetValueOrDefault(SerialAckStatus.Defective);
        var missing = counted.GetValueOrDefault(SerialAckStatus.Missing);
        if (received == qtyReceived && defective == qtyDefective && missing == qtyMissing) return null;

        return "The quantities do not match the serials: "
             + $"{received} received, {defective} defective, {missing} missing were marked on the units.";
    }

    private static async Task<Results<Ok, NotFound, BadRequest<string>>> AckAsync(
        long movementId, [FromBody] AckIssueRequest req, ClaimsPrincipal user, AppDbContext db,
        SerialService serial, StockLedgerService ledger, IAuditService audit, HttpContext http, CancellationToken ct)
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

        // On a serial-tracked issue the units ARE the quantity, so the two accounts of what arrived
        // have to be the same account. Without this a shipment could be booked as three received
        // while all three serials were marked missing: the balance would gain stock that the serial
        // ledger says never turned up, and nothing downstream could tell which one was lying.
        if (serialLines.Count > 0
            && SerialQuantityMismatch(serialLines.Select(l => acks[l.Id]),
                   req.QtyReceived, req.QtyDefective, req.QtyMissing) is { } mismatch)
            return TypedResults.BadRequest(mismatch);

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

            // Rows written before receipts were split out were credited at dispatch, so crediting
            // them again here would double the technician's holding.
            if (!movement.CreditedOnIssue)
                await ledger.AcknowledgeReceiptAsync(movement.PartId, uid,
                    req.QtyReceived, req.QtyDefective, req.QtyMissing, uid,
                    movement.ReferenceType ?? "STOCK_REQUEST", movement.ReferenceId ?? movementId, ct);

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
