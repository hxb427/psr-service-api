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
            x.r.Qty, x.r.Status.ToString(), x.r.AcknowledgedDate, x.r.Remarks, x.r.CreatedAt,
            x.r.Courier, x.r.TrackingNo, null, x.r.Kind.ToString())).ToList();
        return TypedResults.Ok(items);
    }

    private static async Task<Results<Created<StockReturnDto>, NotFound, BadRequest<string>>> CreateAsync(
        [FromBody] CreateStockReturnRequest req, ClaimsPrincipal user, AppDbContext db,
        NumberSequenceService seq, SerialService serial, StockLedgerService ledger,
        IAuditService audit, HttpContext http, CancellationToken ct)
    {
        var part = await db.Parts.FirstOrDefaultAsync(p => p.Id == req.PartId, ct);
        if (part is null) return TypedResults.NotFound();
        user.TryGetUserId(out var uid);

        var serialIds = (req.SerialIds ?? []).Distinct().ToList();

        var kind = StockReturnKind.GoodStock;
        if (!string.IsNullOrWhiteSpace(req.Kind) && !Enum.TryParse(req.Kind, true, out kind))
            return TypedResults.BadRequest($"Unknown return kind '{req.Kind}'.");

        // Serial capture on a return is now about the part, not about who is holding it: an in-house
        // holding of a serial-tracked part is tracked unit by unit exactly like a field one.
        if (part.IsSerialTracked && serialIds.Count != req.Qty)
            return TypedResults.BadRequest($"Select exactly {req.Qty} serial(s) for this serial-tracked part.");

        // A faulty return is a shipment of customers' units coming in for service. It moves no
        // quantity, so there is nothing for a non-serial part to carry and nothing to acknowledge -
        // the same scope the legacy technician_return_dispatches flow had.
        if (kind == StockReturnKind.Faulty && !part.IsSerialTracked)
            return TypedResults.BadRequest(
                $"{part.ItemCode} is not serial-tracked, so it cannot be sent in for service as a faulty return. "
                + "Return it as unused stock, or raise it at the service desk.");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        StockReturn ret;
        try
        {
            var no = await seq.NextAsync(SequenceKeys.StockReturn, ct);
            ret = new StockReturn
            {
                ReturnNo = no, TechnicianId = uid, PartId = req.PartId, Qty = req.Qty, Kind = kind,
                // Good stock comes off the technician as it leaves them. A faulty shipment carries
                // customers' units that were never on their balance, so there is nothing to debit.
                TechnicianDebitedOnShip = kind == StockReturnKind.GoodStock,
                Remarks = req.Remarks, Courier = req.Courier?.Trim(), TrackingNo = req.TrackingNo?.Trim(),
            };
            db.StockReturns.Add(ret);
            await db.SaveChangesAsync(ct);

            if (ret.TechnicianDebitedOnShip)
                await ledger.ReturnDispatchAsync(req.PartId, uid, req.Qty, uid, "STOCK_RETURN", ret.Id, ct);

            foreach (var sid in serialIds)
            {
                var cs = await db.ComponentSerials.AsNoTracking().FirstOrDefaultAsync(c => c.Id == sid, ct);
                if (cs is null || cs.PartId != req.PartId)
                { await tx.RollbackAsync(ct); return TypedResults.BadRequest($"Serial id {sid} is not a unit of this part."); }
                var defective = cs.Status is SerialStatus.Defective or SerialStatus.Collected;

                var err = await serial.ShipReturnSerialAsync(sid, uid, uid, kind, ct);
                if (err is not null) { await tx.RollbackAsync(ct); return TypedResults.BadRequest(err); }

                db.StockReturnSerials.Add(new StockReturnSerial
                { StockReturnId = ret.Id, ComponentSerialId = sid, Defective = defective });
            }
            audit.Log(uid, "stock-return.create", "stock_return", ret.Id,
                details: $"{ret.ReturnNo} {part.ItemCode} x{req.Qty}"
                    + (serialIds.Count > 0 ? $" +{serialIds.Count} SN" : ""), ip: http.GetIp());
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (StockException ex) { await tx.RollbackAsync(ct); return TypedResults.BadRequest(ex.Message); }

        return TypedResults.Created($"/stock-returns/{ret.Id}", await ToDtoAsync(db, ret.Id, ct));
    }

    /// <summary>Receive a technician shipment at the service center.
    ///
    /// The two kinds are genuinely different events and the split is the point of this endpoint:
    ///
    /// GOOD STOCK - unused units the technician was issued and still carries. Acknowledging moves the
    /// quantity off them and back onto the warehouse shelf, and their serials become re-issuable.
    /// Unchanged from how this always worked.
    ///
    /// FAULTY - customers' units collected in the field. These were never on the technician's balance,
    /// so there is nothing to decrement and nothing to shelve: putting them on the warehouse count
    /// would be booking broken stock as sellable. Instead custody moves to the service center and each
    /// unit opens its own repair job, which is what puts it in front of a technician. The quantity
    /// comes back only when that job is stocked.</summary>
    private static async Task<Results<Ok<StockReturnDto>, NotFound, BadRequest<string>>> AcknowledgeAsync(
        long id, ClaimsPrincipal user, AppDbContext db, StockLedgerService ledger,
        SerialService serial, NumberSequenceService seq, IAuditService audit, HttpContext http, CancellationToken ct)
    {
        var r = await db.StockReturns.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (r is null) return TypedResults.NotFound();
        if (r.Status != StockReturnStatus.Pending) return TypedResults.BadRequest($"Return is already {r.Status}.");

        user.TryGetUserId(out var uid);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            var serialLines = await db.StockReturnSerials.AsNoTracking()
                .Where(s => s.StockReturnId == r.Id).ToListAsync(ct);

            if (r.Kind == StockReturnKind.Faulty)
            {
                var opened = await OpenRepairJobsAsync(db, serial, seq, r, serialLines, uid, ct);
                audit.Log(uid, "stock-return.acknowledge", "stock_return", r.Id,
                    details: $"faulty, {opened} repair job(s) opened", ip: http.GetIp());
            }
            else
            {
                await ledger.ReturnToStockAsync(r.PartId, r.TechnicianId, r.Qty, uid, "STOCK_RETURN", r.Id, ct,
                    debitTechnician: !r.TechnicianDebitedOnShip);
                foreach (var line in serialLines)
                    await serial.ReceiveReturnAsync(line.ComponentSerialId, line.Defective, uid,
                        $"Return {r.ReturnNo} received at service center", ct);
                audit.Log(uid, "stock-return.acknowledge", "stock_return", r.Id, ip: http.GetIp());
            }

            r.Status = StockReturnStatus.Stocked;
            r.AcknowledgedByUserId = uid;
            r.AcknowledgedDate = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (StockException ex) { await tx.RollbackAsync(ct); return TypedResults.BadRequest(ex.Message); }

        return TypedResults.Ok(await ToDtoAsync(db, r.Id, ct));
    }

    /// <summary>Open one service job per unit on an acknowledged faulty return, and move each unit to
    /// UNDER_REPAIR pointing at its job.
    ///
    /// One job per unit, not one per shipment: a job carries a single serial, a single verdict and a
    /// single set of lines, and the shipment is only how the units travelled. The legacy app made the
    /// same choice, inserting one service_table row per received serial.
    ///
    /// Nothing is keyed in by hand. The unit's own record says which part it is and which customer it
    /// came from, so the job is raised against that customer rather than against a placeholder - which
    /// is the difference between a repair history that reads as one machine's story and a pile of
    /// anonymous return rows. Only when the customer is genuinely unknown (units that entered tracking
    /// before this existed) does it fall back to a per-technician returns account.</summary>
    private static async Task<int> OpenRepairJobsAsync(
        AppDbContext db, SerialService serial, NumberSequenceService seq, StockReturn r,
        List<StockReturnSerial> serialLines, long uid, CancellationToken ct)
    {
        if (serialLines.Count == 0) return 0;

        var part = await db.Parts.AsNoTracking().FirstOrDefaultAsync(p => p.Id == r.PartId, ct);
        var techName = await db.Users.AsNoTracking().Where(u => u.Id == r.TechnicianId)
            .Select(u => u.FullName ?? u.Username).FirstOrDefaultAsync(ct) ?? $"technician #{r.TechnicianId}";
        var today = Common.ShopClock.Today;
        long? fallbackCustomerId = null;
        var opened = 0;

        foreach (var line in serialLines)
        {
            var unit = await db.ComponentSerials.FirstOrDefaultAsync(c => c.Id == line.ComponentSerialId, ct);
            if (unit is null) continue;

            var priorStatus = unit.Status.ToString();
            var customerId = unit.CustomerId;
            if (customerId is { } cid && !await db.Customers.AnyAsync(c => c.Id == cid, ct)) customerId = null;
            if (customerId is null)
                customerId = fallbackCustomerId ??= await ReturnsAccountAsync(db, techName, ct);

            var job = new ServiceJob
            {
                ServiceNo = await seq.NextAsync(SequenceKeys.Service, ct),
                ChallanNo = $"RTN-{r.ReturnNo}",
                CustomerType = "Direct",
                CustomerId = customerId,
                SerialNo = unit.SerialNumber,
                PsCode = part?.ItemCode,
                ModelName = part?.Name,
                Description = unit.ItemName ?? part?.Name,
                ReportedProblem = $"Field return inspection - unit was {priorStatus} with {techName}",
                WarrantyStatus = WarrantyStatus.Unknown,
                InwardDcNo = r.TrackingNo,
                DateReceived = today,
                Priority = Priority.Normal,
                ServiceStatus = ServiceStatus.Inward,
                AckStatus = AckStatus.Pending,
                SourceComponentSerialId = unit.Id,
                CreatedByUserId = uid,
            };
            db.Services.Add(job);
            await db.SaveChangesAsync(ct);   // job id, for the history row and the serial back-reference

            db.ServiceStatusHistory.Add(new ServiceStatusHistory
            {
                ServiceId = job.Id, FromStatus = null, ToStatus = ServiceStatus.Inward.ToString(),
                ChangedByUserId = uid,
                Note = $"Inward created from field return {r.ReturnNo} ({techName})",
            });

            await serial.ReceiveFaultyReturnAsync(unit.Id, uid,
                $"Return {r.ReturnNo} received at service center; job {job.ServiceNo} opened", ct);
            unit.CurrentServiceJobId = job.Id;
            opened++;
        }

        return opened;
    }

    /// <summary>The party a returned unit is booked against when its original customer is unknown.
    /// One account per technician rather than a single shared one, so the service board still says
    /// where the unit came from. Matched on name so repeated returns reuse the same row.</summary>
    private static async Task<long> ReturnsAccountAsync(AppDbContext db, string techName, CancellationToken ct)
    {
        var name = $"Technician Return - {techName}";
        var existing = await db.Customers.FirstOrDefaultAsync(c => c.Name == name, ct);
        if (existing is not null) return existing.Id;

        var created = new Customer { Name = name, OrganizationName = "Service center returns" };
        db.Customers.Add(created);
        await db.SaveChangesAsync(ct);
        return created.Id;
    }

    private static async Task<Results<Ok<StockReturnDto>, NotFound, BadRequest<string>>> MissingAsync(
        long id, ClaimsPrincipal user, AppDbContext db, SerialService serial, StockLedgerService ledger,
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

        if (r.TechnicianDebitedOnShip)
            ledger.LostInTransit(r.PartId, r.TechnicianId, r.Qty, uid, "STOCK_RETURN", r.Id,
                $"Return {r.ReturnNo} reported missing in transit");

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
        // Jobs raised off this shipment, so the acknowledging user sees what was opened without
        // having to go looking for it on the service board.
        var serviceNos = await db.Services.AsNoTracking()
            .Where(j => j.ChallanNo == $"RTN-{x.r.ReturnNo}" && j.SourceComponentSerialId != null)
            .OrderBy(j => j.Id).Select(j => j.ServiceNo).ToListAsync(ct);

        return new StockReturnDto(x.r.Id, x.r.ReturnNo, x.r.TechnicianId, x.Username, x.r.PartId, x.ItemCode, x.Name,
            x.r.Qty, x.r.Status.ToString(), x.r.AcknowledgedDate, x.r.Remarks, x.r.CreatedAt,
            x.r.Courier, x.r.TrackingNo, serials, x.r.Kind.ToString(),
            serviceNos.Count > 0 ? serviceNos : null);
    }
}
