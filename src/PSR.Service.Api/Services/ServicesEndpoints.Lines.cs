using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PSR.Service.Api.Audit;
using PSR.Service.Api.Auth;
using PSR.Service.Api.Data;
using PSR.Service.Api.Data.Entities;

namespace PSR.Service.Api.Services;

public static partial class ServicesEndpoints
{
    // ---------------------------------------------------------------- lines

    private static async Task<Results<Ok<ServiceLineDto>, NotFound, BadRequest<string>, ForbidHttpResult>> AddLineAsync(
        long id, [FromBody] AddLineRequest req, ClaimsPrincipal user, AppDbContext db,
        Stock.SerialService serial, IAuditService audit, HttpContext http, CancellationToken ct)
    {
        var job = await db.Services.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (job is null) return TypedResults.NotFound();

        var (forbidden, guardError) = LineGuard(user, job);
        if (forbidden) return TypedResults.Forbid();
        if (guardError is not null) return TypedResults.BadRequest(guardError);

        var asked = new Dictionary<long, int>();
        var (line, error) = await BuildLineAsync(db, serial, job, req, asked, ct);
        if (error is not null) return TypedResults.BadRequest(error);

        user.TryGetUserId(out var uid);
        db.ServiceLines.Add(line!);
        job.RowVersion++;
        audit.Log(uid, "service.line.add", "service", job.Id, details: line!.LineType.ToString(), ip: http.GetIp());
        await db.SaveChangesAsync(ct);

        return TypedResults.Ok(await LineToDtoAsync(db, line.Id, ServiceRoles.CanSeePricing(user), ct));
    }

    /// <summary>Add a whole selection of lines at once — the technician sets quantities against their
    /// holding and the charges list and saves once, instead of reopening a dialog per line.
    ///
    /// One transaction, all or nothing. Sending them one request at a time meant a batch that ran out of
    /// stock on its fourth part left the first three on the job, and the technician had to work out
    /// which of the lines now on screen they had actually meant to keep. The running <c>asked</c> tally
    /// is what makes the coverage check correct across the batch: two lines for the same part each pass
    /// on their own while together exceeding what is held.
    ///
    /// Returns the whole job rather than the new lines, because the caller redraws the detail pane from
    /// it either way.</summary>
    private static async Task<Results<Ok<ServiceDetailDto>, NotFound, BadRequest<string>, ForbidHttpResult>> AddLinesAsync(
        long id, [FromBody] AddLinesRequest req, ClaimsPrincipal user, AppDbContext db,
        Stock.SerialService serial, IAuditService audit, HttpContext http, CancellationToken ct)
    {
        var job = await db.Services.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (job is null) return TypedResults.NotFound();

        var (forbidden, guardError) = LineGuard(user, job);
        if (forbidden) return TypedResults.Forbid();
        if (guardError is not null) return TypedResults.BadRequest(guardError);
        if (req.Lines is not { Count: > 0 }) return TypedResults.BadRequest("Nothing was selected to add.");

        var asked = new Dictionary<long, int>();
        var lines = new List<ServiceLine>();
        foreach (var lineReq in req.Lines)
        {
            var (line, error) = await BuildLineAsync(db, serial, job, lineReq, asked, ct);
            if (error is not null) return TypedResults.BadRequest(error);
            lines.Add(line!);
        }

        user.TryGetUserId(out var uid);
        db.ServiceLines.AddRange(lines);
        job.RowVersion++;
        var components = lines.Count(l => l.LineType != ServiceLineType.ServiceCharge);
        audit.Log(uid, "service.line.add-batch", "service", job.Id,
            details: $"{lines.Count} line(s): {components} component/replacement, {lines.Count - components} charge",
            ip: http.GetIp());
        await db.SaveChangesAsync(ct);

        return TypedResults.Ok(await BuildDetailAsync(db, job, ServiceRoles.CanSeePricing(user), ct));
    }

    /// <summary>The checks both line routes share: who may write lines on this job, and whether it is in
    /// a state that accepts them. Returned as a pair rather than a Results&lt;&gt; because the two routes
    /// succeed with different types and a shared Results&lt;&gt; would not convert between them.</summary>
    private static (bool Forbidden, string? Error) LineGuard(ClaimsPrincipal user, ServiceJob job)
    {
        if (!ServiceRoles.IsAssignedTechnician(user, job)) return (true, null);
        if (job.ServiceStatus is not ServiceStatus.InService)
            return (false, $"Lines can only be added while the job is in service (currently {job.ServiceStatus}).");
        if (job.IsTotalLoss)
            return (false, "This job is marked total loss — components/charges cannot be added.");
        return (false, null);
    }

    /// <summary>Build one line and price it, or return the message explaining why it cannot be added.
    /// Nothing is attached to the context here — the caller decides when to add, so a batch can reject
    /// its last line without the earlier ones already being tracked.
    ///
    /// <paramref name="asked"/> carries how much of each part this same call has already claimed, and is
    /// counted on top of what the technician has booked elsewhere.</summary>
    private static async Task<(ServiceLine? Line, string? Error)> BuildLineAsync(
        AppDbContext db, Stock.SerialService serial, ServiceJob job, AddLineRequest req,
        Dictionary<long, int> asked, CancellationToken ct)
    {
        if (!Enum.TryParse<ServiceLineType>(req.LineType, true, out var lineType))
            return (null, $"Unknown line type '{req.LineType}'.");
        var qty = req.Qty < 1 ? 1 : req.Qty;

        var line = new ServiceLine { ServiceId = job.Id, LineType = lineType, Qty = qty, Description = req.Description?.Trim() };

        if (lineType is ServiceLineType.Component or ServiceLineType.Replacement)
        {
            if (req.PartId is not { } pid) return (null, "A part is required for a component/replacement line.");
            var part = await db.Parts.FirstOrDefaultAsync(p => p.Id == pid, ct);
            if (part is null) return (null, "Part not found.");
            line.PartId = part.Id;
            line.UnitPrice = part.CustomerRate;                 // server-set; technicians never send price
            // Replacement lines carry the new unit's serial; serial-tracked component lines carry the fitted serial.
            if (lineType is ServiceLineType.Replacement || part.IsSerialTracked)
                line.ReplacementSerialNo = req.ReplacementSerialNo?.Trim();

            // A fitted serial on a serial-tracked component must be a unit the technician actually
            // holds (RECEIVED, their custody) — legacy pick-list rule enforced server-side.
            if (lineType is ServiceLineType.Component && part.IsSerialTracked
                && !string.IsNullOrWhiteSpace(line.ReplacementSerialNo) && job.TechnicianId is { } techId)
            {
                var err = await serial.ValidateFittedSerialAsync(part.Id, line.ReplacementSerialNo!, techId, ct);
                if (err is not null) return (null, err);
            }

            // The line has to be one the technician can actually cover, because completing the job is
            // what consumes it — and that consume is guarded. Until this check existed nothing verified
            // it at all: the desk app only ever offered the technician's own holdings, so a quantity
            // above what they held (or any part at all, from anything but the desk app) was accepted,
            // and the job then refused to complete until someone worked out which line to delete.
            if (job.TechnicianId is { } holder)
            {
                var onHand = await db.StockBalances.AsNoTracking()
                    .Where(b => b.PartId == part.Id && b.TechnicianId == holder)
                    .Select(b => b.OnHand).FirstOrDefaultAsync(ct);
                var booked = await BookedQtyQuery(db, part.Id, holder).SumAsync(ct);
                var pending = asked.GetValueOrDefault(part.Id);
                var free = onHand - booked - pending;
                if (free < qty)
                    return (null, booked + pending > 0
                        ? $"You hold {onHand} × {part.ItemCode}, and {booked + pending} of those are already spoken for. "
                          + $"{Math.Max(free, 0)} left to book, not {qty}."
                        : $"You hold {onHand} × {part.ItemCode} — not enough for {qty}.");
                asked[part.Id] = pending + qty;
            }
        }
        else // ServiceCharge
        {
            if (req.ServiceChargeId is not { } scid) return (null, "A service charge is required for a service-charge line.");
            var sc = await db.ServiceCharges.FirstOrDefaultAsync(s => s.Id == scid, ct);
            if (sc is null) return (null, "Service charge not found.");
            line.ServiceChargeId = sc.Id;
            line.UnitPrice = sc.Charge;
            line.Description ??= sc.Name;
        }

        line.Amount = line.UnitPrice * qty;
        return (line, null);
    }

    /// <summary>How much of one part a technician already has committed but not yet consumed.
    ///
    /// Consumption happens at completion, never at add-line, so anything still uncommitted sits on a
    /// job that is IN SERVICE: earlier states cannot carry lines (they can only be added in service),
    /// and later ones have already had their stock taken. A revert returns the stock and puts the job
    /// back in service, so it lands back in this sum.
    ///
    /// Exposed rather than inlined so the translation test can call ToQueryString on it — a join and a
    /// SUM the provider failed to translate would otherwise surface only against the real database.</summary>
    public static IQueryable<int> BookedQtyQuery(AppDbContext db, long partId, long technicianId) =>
        from l in db.ServiceLines.AsNoTracking()
        join s in db.Services on l.ServiceId equals s.Id
        where l.PartId == partId && s.TechnicianId == technicianId && !s.IsDeleted
              && s.ServiceStatus == ServiceStatus.InService
              && (l.LineType == ServiceLineType.Component || l.LineType == ServiceLineType.Replacement)
        select l.Qty;

    private static async Task<Results<NoContent, NotFound, BadRequest<string>, ForbidHttpResult>> DeleteLineAsync(
        long id, long lineId, ClaimsPrincipal user, AppDbContext db, IAuditService audit, HttpContext http, CancellationToken ct)
    {
        var job = await db.Services.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (job is null) return TypedResults.NotFound();
        if (!ServiceRoles.IsAssignedTechnician(user, job)) return TypedResults.Forbid();
        if (job.ServiceStatus is not ServiceStatus.InService)
            return TypedResults.BadRequest($"Lines can only be removed while the job is in service (currently {job.ServiceStatus}).");

        var line = await db.ServiceLines.FirstOrDefaultAsync(l => l.Id == lineId && l.ServiceId == id, ct);
        if (line is null) return TypedResults.NotFound();

        user.TryGetUserId(out var uid);
        db.ServiceLines.Remove(line);
        job.RowVersion++;
        audit.Log(uid, "service.line.delete", "service", job.Id, details: $"line {lineId}", ip: http.GetIp());
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }
}
