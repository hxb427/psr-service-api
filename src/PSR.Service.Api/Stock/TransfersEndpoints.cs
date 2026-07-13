using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PSR.Service.Api.Audit;
using PSR.Service.Api.Auth;
using PSR.Service.Api.Data;
using PSR.Service.Api.Data.Entities;

namespace PSR.Service.Api.Stock;

/// <summary>Peer-to-peer technician stock transfer (legacy technician_transfers). Balances move
/// only at acknowledgement; serials sit IN_TRANSIT_TECH while pending (sender keeps ownership so
/// cancel rolls back cleanly). MISSING outcomes stay with the sender.</summary>
public static class TransfersEndpoints
{
    public static IEndpointRouteBuilder MapTransferEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/transfers").WithTags("transfers").RequireAuthorization();

        group.MapGet("/", ListAsync);
        group.MapGet("/technicians", TechniciansAsync);
        group.MapPost("/", CreateAsync);
        group.MapPost("/{id:long}/acknowledge", AcknowledgeAsync);
        group.MapPost("/{id:long}/cancel", CancelAsync);

        return app;
    }

    /// <summary>direction=in → addressed to me; direction=out → sent by me; managers see all
    /// when no direction is given (audit view).</summary>
    private static async Task<Ok<List<TransferDto>>> ListAsync(
        AppDbContext db, ClaimsPrincipal user, string? direction, string? status, CancellationToken ct)
    {
        user.TryGetUserId(out var uid);
        var manage = StockRoles.CanManage(user);

        var q = db.TechnicianTransfers.AsNoTracking()
            .Include(t => t.Lines).ThenInclude(l => l.Serials)
            .AsQueryable();

        if (string.Equals(direction, "in", StringComparison.OrdinalIgnoreCase))
            q = q.Where(t => t.ToTechnicianId == uid);
        else if (string.Equals(direction, "out", StringComparison.OrdinalIgnoreCase))
            q = q.Where(t => t.FromTechnicianId == uid);
        else if (!manage)
            q = q.Where(t => t.FromTechnicianId == uid || t.ToTechnicianId == uid);

        if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<TransferStatus>(status, true, out var st))
            q = q.Where(t => t.Status == st);

        var rows = await q.OrderByDescending(t => t.Id).Take(200).ToListAsync(ct);
        var dtos = new List<TransferDto>();
        foreach (var t in rows) dtos.Add(await ToDtoAsync(db, t, ct));
        return TypedResults.Ok(dtos);
    }

    private static async Task<Results<Created<TransferDto>, BadRequest<string>>> CreateAsync(
        [FromBody] CreateTransferRequest req, ClaimsPrincipal user, AppDbContext db,
        NumberSequenceService seq, SerialService serial, IAuditService audit, HttpContext http, CancellationToken ct)
    {
        user.TryGetUserId(out var uid);
        if (req.ToTechnicianId == uid) return TypedResults.BadRequest("Cannot transfer to yourself.");
        var receiver = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == req.ToTechnicianId, ct);
        if (receiver is null || !receiver.IsActive) return TypedResults.BadRequest("Receiving technician not found or inactive.");
        var receiverName = receiver.FullName ?? receiver.Username;

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        TechnicianTransfer transfer;
        try
        {
            var no = await seq.NextAsync(SequenceKeys.Transfer, ct);
            transfer = new TechnicianTransfer
            {
                TransferNo = no, FromTechnicianId = uid, ToTechnicianId = req.ToTechnicianId,
                Remarks = req.Remarks?.Trim(),
            };
            db.TechnicianTransfers.Add(transfer);

            foreach (var lineReq in req.Lines)
            {
                var part = await db.Parts.AsNoTracking().FirstOrDefaultAsync(p => p.Id == lineReq.PartId, ct);
                if (part is null) { await tx.RollbackAsync(ct); return TypedResults.BadRequest($"Part {lineReq.PartId} not found."); }

                // Sender must actually hold the quantity (balances move at ack, so only validate here).
                var onHand = await db.StockBalances.AsNoTracking()
                    .Where(b => b.PartId == lineReq.PartId && b.TechnicianId == uid)
                    .Select(b => (int?)b.OnHand).FirstOrDefaultAsync(ct) ?? 0;
                if (onHand < lineReq.Qty)
                { await tx.RollbackAsync(ct); return TypedResults.BadRequest($"You hold only {onHand} of {part.ItemCode}."); }

                var serialIds = (lineReq.SerialIds ?? []).Distinct().ToList();
                if (part.IsSerialTracked && serialIds.Count != lineReq.Qty)
                { await tx.RollbackAsync(ct); return TypedResults.BadRequest($"Select exactly {lineReq.Qty} serial(s) for {part.ItemCode}."); }

                var line = new TechnicianTransferLine { Transfer = transfer, PartId = lineReq.PartId, Qty = lineReq.Qty };
                db.TechnicianTransferLines.Add(line);

                foreach (var sid in serialIds)
                {
                    var err = await serial.MarkInTransitTechAsync(sid, uid, receiverName, uid, ct);
                    if (err is not null) { await tx.RollbackAsync(ct); return TypedResults.BadRequest(err); }
                    db.TechnicianTransferSerials.Add(new TechnicianTransferSerial { Line = line, ComponentSerialId = sid });
                }
            }

            audit.Log(uid, "transfer.create", "technician_transfer", null, details: transfer.TransferNo, ip: http.GetIp());
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (StockException ex) { await tx.RollbackAsync(ct); return TypedResults.BadRequest(ex.Message); }

        var created = await LoadAsync(db, transfer.Id, ct);
        return TypedResults.Created($"/transfers/{transfer.Id}", await ToDtoAsync(db, created!, ct));
    }

    private static async Task<Results<Ok<TransferDto>, NotFound, BadRequest<string>, ForbidHttpResult>> AcknowledgeAsync(
        long id, [FromBody] AckTransferRequest req, ClaimsPrincipal user, AppDbContext db,
        StockLedgerService ledger, SerialService serial, IAuditService audit, HttpContext http, CancellationToken ct)
    {
        var t = await LoadAsync(db, id, ct);
        if (t is null) return TypedResults.NotFound();
        user.TryGetUserId(out var uid);
        if (t.ToTechnicianId != uid) return TypedResults.Forbid();
        if (t.Status != TransferStatus.Pending) return TypedResults.BadRequest($"Transfer is already {t.Status}.");

        var sender = await db.Users.AsNoTracking().FirstAsync(u => u.Id == t.FromTechnicianId, ct);
        var receiver = await db.Users.AsNoTracking().FirstAsync(u => u.Id == t.ToTechnicianId, ct);
        var senderName = sender.FullName ?? sender.Username;
        var receiverName = receiver.FullName ?? receiver.Username;

        var ackByLine = req.Lines.ToDictionary(l => l.LineId);
        if (t.Lines.Any(l => !ackByLine.ContainsKey(l.Id)))
            return TypedResults.BadRequest("Acknowledge every line on this transfer.");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            foreach (var line in t.Lines)
            {
                var ack = ackByLine[line.Id];
                if (ack.QtyReceived + ack.QtyDefective + ack.QtyMissing != line.Qty)
                    return TypedResults.BadRequest($"Line {line.Id}: quantities must add up to {line.Qty}.");

                // Received + defective units physically moved: sender → receiver balance.
                var moved = ack.QtyReceived + ack.QtyDefective;
                if (moved > 0)
                    await ledger.TransferAsync(line.PartId, t.FromTechnicianId, t.ToTechnicianId, moved, uid,
                        "TRANSFER", t.Id, ct);

                line.QtyReceived = ack.QtyReceived;
                line.QtyDefective = ack.QtyDefective;
                line.QtyMissing = ack.QtyMissing;

                var serialAcks = (ack.Serials ?? []).ToDictionary(s => s.IssueSerialId, s => s.Status);
                if (line.Serials.Count > 0 && line.Serials.Any(s => !serialAcks.ContainsKey(s.Id)))
                { await tx.RollbackAsync(ct); return TypedResults.BadRequest($"Line {line.Id}: acknowledge every serial."); }

                foreach (var ts in line.Serials)
                {
                    if (!Enum.TryParse<SerialAckStatus>(serialAcks[ts.Id], true, out var st))
                    { await tx.RollbackAsync(ct); return TypedResults.BadRequest($"Unknown serial ack status '{serialAcks[ts.Id]}'."); }
                    var err = await serial.AckTransferSerialAsync(ts.ComponentSerialId, st,
                        t.FromTechnicianId, senderName, t.ToTechnicianId, receiverName, ct);
                    if (err is not null) { await tx.RollbackAsync(ct); return TypedResults.BadRequest(err); }
                    ts.AckStatus = st;
                }
            }

            t.Status = TransferStatus.Acknowledged;
            t.AcknowledgedAt = DateTime.UtcNow;
            audit.Log(uid, "transfer.acknowledge", "technician_transfer", t.Id, details: t.TransferNo, ip: http.GetIp());
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (StockException ex) { await tx.RollbackAsync(ct); return TypedResults.BadRequest(ex.Message); }

        return TypedResults.Ok(await ToDtoAsync(db, t, ct));
    }

    private static async Task<Results<Ok<TransferDto>, NotFound, BadRequest<string>, ForbidHttpResult>> CancelAsync(
        long id, ClaimsPrincipal user, AppDbContext db, SerialService serial,
        IAuditService audit, HttpContext http, CancellationToken ct)
    {
        var t = await LoadAsync(db, id, ct);
        if (t is null) return TypedResults.NotFound();
        user.TryGetUserId(out var uid);
        if (t.FromTechnicianId != uid && !StockRoles.CanManage(user)) return TypedResults.Forbid();
        if (t.Status != TransferStatus.Pending) return TypedResults.BadRequest($"Transfer is already {t.Status}.");

        var sender = await db.Users.AsNoTracking().FirstAsync(u => u.Id == t.FromTechnicianId, ct);
        var senderName = sender.FullName ?? sender.Username;

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        foreach (var ts in t.Lines.SelectMany(l => l.Serials))
            await serial.RollbackTransferSerialAsync(ts.ComponentSerialId, t.FromTechnicianId, senderName, uid, ct);

        t.Status = TransferStatus.Cancelled;
        audit.Log(uid, "transfer.cancel", "technician_transfer", t.Id, details: t.TransferNo, ip: http.GetIp());
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return TypedResults.Ok(await ToDtoAsync(db, t, ct));
    }

    /// <summary>Active technicians a transfer can target (excludes the caller). Open to any
    /// authenticated user — names only, no sensitive fields.</summary>
    private static async Task<Ok<List<TransferTechnicianDto>>> TechniciansAsync(
        AppDbContext db, ClaimsPrincipal user, CancellationToken ct)
    {
        user.TryGetUserId(out var uid);
        var rows = await (from u in db.Users.AsNoTracking()
                          join ur in db.UserRoles on u.Id equals ur.UserId
                          join r in db.Roles on ur.RoleId equals r.Id
                          where u.IsActive && u.Id != uid && r.Name == RoleNames.Technician
                          orderby u.Username
                          select new TransferTechnicianDto(u.Id, u.Username, u.FullName, u.IsFieldTechnician))
            .ToListAsync(ct);
        return TypedResults.Ok(rows);
    }

    // ---- helpers ----

    private static Task<TechnicianTransfer?> LoadAsync(AppDbContext db, long id, CancellationToken ct)
        => db.TechnicianTransfers.Include(t => t.Lines).ThenInclude(l => l.Serials)
            .FirstOrDefaultAsync(t => t.Id == id, ct);

    private static async Task<TransferDto> ToDtoAsync(AppDbContext db, TechnicianTransfer t, CancellationToken ct)
    {
        var userNames = await db.Users.AsNoTracking()
            .Where(u => u.Id == t.FromTechnicianId || u.Id == t.ToTechnicianId)
            .ToDictionaryAsync(u => u.Id, u => u.FullName ?? u.Username, ct);

        var partIds = t.Lines.Select(l => l.PartId).Distinct().ToList();
        var parts = await db.Parts.AsNoTracking().Where(p => partIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, ct);

        var serialIds = t.Lines.SelectMany(l => l.Serials).Select(s => s.ComponentSerialId).Distinct().ToList();
        var serialNos = serialIds.Count == 0
            ? new Dictionary<long, string>()
            : await db.ComponentSerials.AsNoTracking().Where(c => serialIds.Contains(c.Id))
                .ToDictionaryAsync(c => c.Id, c => c.SerialNumber, ct);

        var lines = t.Lines.Select(l => new TransferLineDto(
            l.Id, l.PartId,
            parts.TryGetValue(l.PartId, out var p) ? p.ItemCode : $"#{l.PartId}",
            p?.Name ?? string.Empty,
            l.Qty, l.QtyReceived, l.QtyDefective, l.QtyMissing,
            l.Serials.Select(s => new TransferSerialDto(
                s.Id, s.ComponentSerialId,
                serialNos.GetValueOrDefault(s.ComponentSerialId, $"#{s.ComponentSerialId}"),
                s.AckStatus?.ToString())).ToList())).ToList();

        return new TransferDto(
            t.Id, t.TransferNo,
            t.FromTechnicianId, userNames.GetValueOrDefault(t.FromTechnicianId),
            t.ToTechnicianId, userNames.GetValueOrDefault(t.ToTechnicianId),
            t.Status.ToString(), t.Remarks, t.CreatedAt, t.AcknowledgedAt, lines);
    }
}
