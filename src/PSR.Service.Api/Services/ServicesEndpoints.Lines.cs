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
        if (!ServiceRoles.IsAssignedTechnician(user, job)) return TypedResults.Forbid();
        if (job.ServiceStatus is not ServiceStatus.InService)
            return TypedResults.BadRequest($"Lines can only be added while the job is in service (currently {job.ServiceStatus}).");
        if (job.IsTotalLoss)
            return TypedResults.BadRequest("This job is marked total loss — components/charges cannot be added.");
        if (!Enum.TryParse<ServiceLineType>(req.LineType, true, out var lineType))
            return TypedResults.BadRequest($"Unknown line type '{req.LineType}'.");
        var qty = req.Qty < 1 ? 1 : req.Qty;

        var line = new ServiceLine { ServiceId = job.Id, LineType = lineType, Qty = qty, Description = req.Description?.Trim() };

        if (lineType is ServiceLineType.Component or ServiceLineType.Replacement)
        {
            if (req.PartId is not { } pid) return TypedResults.BadRequest("A part is required for a component/replacement line.");
            var part = await db.Parts.FirstOrDefaultAsync(p => p.Id == pid, ct);
            if (part is null) return TypedResults.BadRequest("Part not found.");
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
                if (err is not null) return TypedResults.BadRequest(err);
            }
        }
        else // ServiceCharge
        {
            if (req.ServiceChargeId is not { } scid) return TypedResults.BadRequest("A service charge is required for a service-charge line.");
            var sc = await db.ServiceCharges.FirstOrDefaultAsync(s => s.Id == scid, ct);
            if (sc is null) return TypedResults.BadRequest("Service charge not found.");
            line.ServiceChargeId = sc.Id;
            line.UnitPrice = sc.Charge;
            line.Description ??= sc.Name;
        }
        line.Amount = line.UnitPrice * qty;

        user.TryGetUserId(out var uid);
        db.ServiceLines.Add(line);
        job.RowVersion++;
        audit.Log(uid, "service.line.add", "service", job.Id, details: lineType.ToString(), ip: http.GetIp());
        await db.SaveChangesAsync(ct);

        return TypedResults.Ok(await LineToDtoAsync(db, line.Id, ServiceRoles.CanSeePricing(user), ct));
    }

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
