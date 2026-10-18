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
        var ids = rows.Select(x => x.r.Id).ToList();

        // Serials and the jobs raised off each shipment used to be omitted here and filled in only
        // on the single-return responses, which left both callers of this list blind: the technician
        // could not see which units they had shipped, and the service centre could not see what it
        // was being asked to book in without acknowledging it first. Loaded in two batched reads
        // rather than per row, so a long list is still two queries.
        var serialsByReturn = await (from s in db.StockReturnSerials.AsNoTracking()
                                     where ids.Contains(s.StockReturnId)
                                     join c in db.ComponentSerials on s.ComponentSerialId equals c.Id
                                     select new
                                     {
                                         s.StockReturnId,
                                         Dto = new StockReturnSerialDto(
                                             c.Id, c.SerialNumber, s.Defective, c.Status.ToString(),
                                             s.AckStatus != null ? s.AckStatus.ToString() : null),
                                     })
            .ToListAsync(ct);
        var serialLookup = serialsByReturn
            .GroupBy(x => x.StockReturnId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Dto).ToList());

        // Keyed off the challan the faulty-return path stamps onto every job it opens.
        var challanNos = rows.Select(x => $"RTN-{x.r.ReturnNo}").ToList();
        var jobs = await db.Services.AsNoTracking()
            .Where(j => j.ChallanNo != null && challanNos.Contains(j.ChallanNo)
                        && j.SourceComponentSerialId != null)
            .OrderBy(j => j.Id)
            .Select(j => new { j.ChallanNo, j.ServiceNo })
            .ToListAsync(ct);
        var jobLookup = jobs
            .GroupBy(j => j.ChallanNo!)
            .ToDictionary(g => g.Key, g => g.Select(j => j.ServiceNo).ToList());

        var items = rows.Select(x => new StockReturnDto(
            x.r.Id, x.r.ReturnNo, x.r.TechnicianId, x.Username, x.r.PartId, x.ItemCode, x.Name,
            x.r.Qty, x.r.Status.ToString(), x.r.AcknowledgedDate, x.r.Remarks, x.r.CreatedAt,
            x.r.Courier, x.r.TrackingNo,
            serialLookup.TryGetValue(x.r.Id, out var ser) ? ser : null,
            x.r.Kind.ToString(),
            jobLookup.TryGetValue($"RTN-{x.r.ReturnNo}", out var nos) ? nos : null,
            x.r.QtyReceived, x.r.QtyMissing)).ToList();
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

    /// <summary>Receive a technician shipment at the service center - all of it, or the part of it
    /// that has actually turned up.
    ///
    /// The two kinds are genuinely different events and the split is the point of this endpoint:
    ///
    /// GOOD STOCK - unused units the technician was issued and still carries. Booking one in moves its
    /// quantity off them and back onto the warehouse shelf, and its serial becomes re-issuable.
    ///
    /// FAULTY - customers' units collected in the field. These were never on the technician's balance,
    /// so there is nothing to decrement and nothing to shelve: putting them on the warehouse count
    /// would be booking broken stock as sellable. Instead custody moves to the service center and each
    /// unit opens its own repair job, which is what puts it in front of a technician. The quantity
    /// comes back only when that job is stocked.
    ///
    /// A shipment is the envelope, not the thing being received. Ten units in one box can arrive as
    /// eight good, one smashed and one absent, and the old all-or-nothing acknowledgement could only
    /// say "all ten arrived perfectly" - so the shelf gained two units nobody had, and the serial
    /// ledger swore two broken or missing units were re-issuable stock. Now each unit carries its own
    /// verdict, the shipment stays Pending until its last unit is resolved, and a quantity-only
    /// shipment takes a received count that may not exceed what was sent.
    ///
    /// An empty body still means what it always meant: everything arrived exactly as sent.</summary>
    private static async Task<Results<Ok<StockReturnDto>, NotFound, BadRequest<string>>> AcknowledgeAsync(
        long id, [FromBody] AckStockReturnRequest? req, ClaimsPrincipal user, AppDbContext db,
        StockLedgerService ledger, SerialService serial, NumberSequenceService seq, IAuditService audit,
        HttpContext http, CancellationToken ct)
    {
        var r = await db.StockReturns.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (r is null) return TypedResults.NotFound();
        if (r.Status != StockReturnStatus.Pending) return TypedResults.BadRequest($"Return is already {r.Status}.");

        user.TryGetUserId(out var uid);
        var serialLines = await db.StockReturnSerials.Where(s => s.StockReturnId == r.Id).ToListAsync(ct);

        return serialLines.Count > 0
            ? await ReceiveUnitsAsync(r, serialLines, req, uid, db, ledger, serial, seq, audit, http, ct)
            : await ReceiveQuantityAsync(r, req, uid, db, ledger, audit, http, ct);
    }

    /// <summary>Book in the serial-tracked units this call names, or every outstanding one when it
    /// names none. Each unit is resolved exactly once: a verdict already recorded is not up for
    /// revision here, because the shelf and the serial ledger have already moved on it.</summary>
    private static async Task<Results<Ok<StockReturnDto>, NotFound, BadRequest<string>>> ReceiveUnitsAsync(
        StockReturn r, List<StockReturnSerial> serialLines, AckStockReturnRequest? req, long uid,
        AppDbContext db, StockLedgerService ledger, SerialService serial, NumberSequenceService seq,
        IAuditService audit, HttpContext http, CancellationToken ct)
    {
        if (req?.QtyReceived is not null)
            return TypedResults.BadRequest(
                "This shipment is serial-tracked: book its units in by serial, not by quantity.");

        var open = serialLines.Where(l => l.AckStatus is null).ToList();
        if (open.Count == 0)
            return TypedResults.BadRequest("Every unit on this shipment has already been booked in.");

        var (verdicts, error) = PlanUnitReceipt([.. open.Select(l => l.ComponentSerialId)], req?.Serials);
        if (error is not null) return TypedResults.BadRequest(error);
        var plan = verdicts
            .Select(v => (Line: open.First(l => l.ComponentSerialId == v.ComponentSerialId), v.Verdict))
            .ToList();

        var now = DateTime.UtcNow;
        var opened = new List<string>();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            foreach (var (line, verdict) in plan)
            {
                if (await ApplyVerdictAsync(db, ledger, serial, seq, r, line, verdict, uid, ct)
                    is { } serviceNo)
                    opened.Add(serviceNo);

                line.AckStatus = verdict;
                line.AckedByUserId = uid;
                line.AckedAt = now;
                if (verdict == SerialAckStatus.Missing) r.QtyMissing++; else r.QtyReceived++;
            }

            CloseIfSettled(r, serialLines, uid, now);
            AppendReceiptNote(r, req?.Remarks);
            audit.Log(uid, "stock-return.acknowledge", "stock_return", r.Id,
                details: string.Join(", ", plan.GroupBy(p => p.Verdict).Select(g => $"{g.Count()} {g.Key}"))
                    + (opened.Count > 0 ? $"; {opened.Count} repair job(s) opened" : "")
                    + (r.Status == StockReturnStatus.Pending
                        ? $"; {serialLines.Count(l => l.AckStatus is null)} unit(s) still outstanding"
                        : ""),
                ip: http.GetIp());
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (StockException ex) { await tx.RollbackAsync(ct); return TypedResults.BadRequest(ex.Message); }

        return TypedResults.Ok(await ToDtoAsync(db, r.Id, ct));
    }

    /// <summary>Work out which units this call resolves and to what, without touching the database.
    ///
    /// Naming no units means the whole outstanding remainder arrived as sent, which is what an empty
    /// body has always meant here. Naming units restricts the call to exactly those - and a name that
    /// is not outstanding is an error rather than a no-op, because the two ways that happens are a
    /// unit someone else already booked in and a serial id from a different shipment, and silently
    /// doing nothing would report both as success.
    ///
    /// Returns the verdicts to apply, or the message to hand back.</summary>
    internal static (List<(long ComponentSerialId, SerialAckStatus Verdict)> Verdicts, string? Error) PlanUnitReceipt(
        IReadOnlyList<long> outstanding, IReadOnlyList<AckStockReturnSerialLine>? asked)
    {
        var plan = new List<(long ComponentSerialId, SerialAckStatus Verdict)>();
        if (asked is null || asked.Count == 0)
        {
            plan.AddRange(outstanding.Select(id => (id, SerialAckStatus.Received)));
            return (plan, null);
        }

        foreach (var ask in asked)
        {
            if (!outstanding.Contains(ask.ComponentSerialId))
                return (plan,
                    $"Serial id {ask.ComponentSerialId} is not a unit of this shipment still awaiting receipt.");
            if (plan.Any(p => p.ComponentSerialId == ask.ComponentSerialId))
                return (plan, $"Serial id {ask.ComponentSerialId} is named twice.");
            if (!Enum.TryParse<SerialAckStatus>(ask.Status, true, out var verdict))
                return (plan, $"Unknown serial ack status '{ask.Status}'.");
            plan.Add((ask.ComponentSerialId, verdict));
        }

        return (plan, null);
    }

    /// <summary>What a receiver may claim arrived. Capped at what was sent, because a receipt is a
    /// statement about one shipment and not a way to conjure stock that was never dispatched.</summary>
    internal static string? QuantityReceiptError(int received, int sent, string returnNo) =>
        received < 0 || received > sent
            ? $"Received quantity must be between 0 and the {sent} sent on {returnNo}."
            : null;

    /// <summary>One unit, booked in as the receiver found it. Returns the repair job opened for it,
    /// or null when none was.
    ///
    /// The envelope a unit travelled in does not decide what it is. A unit the receiver calls broken
    /// is a broken unit now in the service centre's hands whether it rode in on a faulty shipment or
    /// turned up smashed among unused stock, so both get a repair job: it is the only thing that puts
    /// the unit in front of a technician, and UNDER_REPAIR is deliberately not re-issuable, so it
    /// cannot be offered on the next issue while it is in pieces. The quantity follows from that -
    /// the unit comes off the technician now and reaches the warehouse only when its job is stocked.
    ///
    /// Without this, a Defective verdict would leave the unit at the service centre in a status
    /// nothing acts on: not on the shelf, not on anyone's worklist, waiting for somebody to notice it
    /// by hand.</summary>
    private static async Task<string?> ApplyVerdictAsync(
        AppDbContext db, StockLedgerService ledger, SerialService serial, NumberSequenceService seq,
        StockReturn r, StockReturnSerial line, SerialAckStatus verdict, long uid, CancellationToken ct)
    {
        // Shipments raised before dispatch and receipt were split out still carry their quantity on
        // the technician, so whatever became of the unit, this is where it comes off them. A faulty
        // shipment never had a quantity to move in the first place.
        var owes = r.Kind == StockReturnKind.GoodStock && !r.TechnicianDebitedOnShip;

        switch (verdict)
        {
            // Unused stock the receiver says arrived fine. It goes straight onto the shelf: nothing
            // was done to it, nothing needs doing to it, and there is no bench step to wait for.
            //
            // `defective: false` is deliberate, and it is a fix rather than a simplification. This
            // used to pass line.Defective - the SHIPPER's note, written on a phone before the box was
            // sealed - which could send the unit to DEFECTIVE while the line below credited the
            // warehouse for it anyway: a quantity on the shelf with no unit on the shelf to match.
            // The receiver's verdict is the one that counts here, same as everywhere else in this
            // method; a receiver who finds the shipper was right sends Defective, which is the branch
            // below.
            case SerialAckStatus.Received when r.Kind == StockReturnKind.GoodStock:
                await serial.ReceiveReturnAsync(line.ComponentSerialId, defective: false, uid,
                    $"Return {r.ReturnNo}: unused stock booked back onto the shelf", ct);
                // Onto the SHELF, so no warehouse quantity is credited: shelfQty 1 of 1. The unit is
                // SHELFED now, and the shelf is counted by unit - crediting the warehouse as well
                // would count this one machine twice, once as a named unit on the shelf and once as
                // an anonymous quantity in the warehouse, which is the drift the two-ledger split
                // exists to prevent.
                //
                // It does NOT go back to the warehouse it was issued from, and that is a change worth
                // knowing: a unit that has been out to a technician and come back is a named unit in
                // hand from then on, available to issue again or to hand over as a replacement. The
                // warehouse figure means stock bought in and not yet taken out.
                await ledger.ReturnToStockAsync(r.PartId, r.TechnicianId, 1, uid, "STOCK_RETURN", r.Id, ct,
                    debitTechnician: owes, shelfQty: 1);
                return null;

            case SerialAckStatus.Missing:
                await serial.ChangeStatusAsync(line.ComponentSerialId, SerialStatus.Missing, uid,
                    $"Return {r.ReturnNo}: unit never arrived at the service centre", ct);
                await ledger.ReturnLostAsync(r.PartId, r.TechnicianId, r.Kind == StockReturnKind.GoodStock ? 1 : 0,
                    uid, "STOCK_RETURN", r.Id, $"Return {r.ReturnNo}: unit never arrived", ct,
                    debitTechnician: owes);
                return null;

            // Received on a faulty shipment, or Defective on either: a unit for the bench.
            default:
                if (r.Kind == StockReturnKind.GoodStock)
                    await ledger.ReturnDefectiveAsync(r.PartId, r.TechnicianId, 1, uid, "STOCK_RETURN", r.Id,
                        $"Return {r.ReturnNo}: unit arrived faulty, booked in for repair", ct,
                        debitTechnician: owes);
                return await OpenRepairJobAsync(db, serial, seq, r, line, uid, ct);
        }
    }

    /// <summary>Book in a quantity-only shipment. One shot, because there are no units to resolve one
    /// at a time - but the count is the receiver's, capped at what was sent, and the shortfall is
    /// written off rather than quietly shelved.</summary>
    private static async Task<Results<Ok<StockReturnDto>, NotFound, BadRequest<string>>> ReceiveQuantityAsync(
        StockReturn r, AckStockReturnRequest? req, long uid, AppDbContext db, StockLedgerService ledger,
        IAuditService audit, HttpContext http, CancellationToken ct)
    {
        // Only reachable if a shipment's serial rows were deleted underneath it: a faulty return is
        // refused at creation unless the part is serial-tracked, and then carries one unit per qty.
        if (r.Kind == StockReturnKind.Faulty)
            return TypedResults.BadRequest(
                $"Return {r.ReturnNo} is a faulty shipment with no units on it; there is nothing to book in.");

        var received = req?.QtyReceived ?? r.Qty;
        if (QuantityReceiptError(received, r.Qty, r.ReturnNo) is { } bad) return TypedResults.BadRequest(bad);
        var missing = r.Qty - received;

        var now = DateTime.UtcNow;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            var owes = !r.TechnicianDebitedOnShip;
            if (received > 0)
                await ledger.ReturnToStockAsync(r.PartId, r.TechnicianId, received, uid, "STOCK_RETURN", r.Id, ct,
                    debitTechnician: owes);
            await ledger.ReturnLostAsync(r.PartId, r.TechnicianId, missing, uid, "STOCK_RETURN", r.Id,
                $"Return {r.ReturnNo}: {missing} of {r.Qty} never arrived", ct, debitTechnician: owes);

            r.QtyReceived = received;
            r.QtyMissing = missing;
            r.Status = received > 0 ? StockReturnStatus.Stocked : StockReturnStatus.Missing;
            r.AcknowledgedByUserId = uid;
            r.AcknowledgedDate = now;
            AppendReceiptNote(r, req?.Remarks);
            audit.Log(uid, "stock-return.acknowledge", "stock_return", r.Id,
                details: $"{received} of {r.Qty} received"
                    + (missing > 0 ? $", {missing} written off as never arrived" : ""),
                ip: http.GetIp());
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (StockException ex) { await tx.RollbackAsync(ct); return TypedResults.BadRequest(ex.Message); }

        return TypedResults.Ok(await ToDtoAsync(db, r.Id, ct));
    }

    /// <summary>A shipment closes when its last unit has a verdict, and not before - leaving it open is
    /// what lets a straggler be booked in tomorrow against the same shipment. Missing rather than
    /// Stocked when nothing on it ever turned up.</summary>
    private static void CloseIfSettled(StockReturn r, List<StockReturnSerial> lines, long uid, DateTime now)
    {
        if (lines.Any(l => l.AckStatus is null)) return;
        r.Status = r.QtyReceived > 0 ? StockReturnStatus.Stocked : StockReturnStatus.Missing;
        r.AcknowledgedByUserId = uid;
        r.AcknowledgedDate = now;
    }

    /// <summary>Keep the receiver's note on the shipment, where the technician who sent it can read it,
    /// instead of only in an audit log they cannot see.</summary>
    private static void AppendReceiptNote(StockReturn r, string? note)
    {
        note = note?.Trim();
        if (string.IsNullOrEmpty(note)) return;
        var combined = string.IsNullOrWhiteSpace(r.Remarks) ? $"Receipt: {note}" : $"{r.Remarks}\nReceipt: {note}";
        r.Remarks = combined.Length <= 500 ? combined : combined[..500];
    }

    /// <summary>Open the repair job for one unit of a faulty return and move that unit to UNDER_REPAIR
    /// pointing at it. Returns the job number, or null when the unit's record has gone.
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
    private static async Task<string?> OpenRepairJobAsync(
        AppDbContext db, SerialService serial, NumberSequenceService seq, StockReturn r,
        StockReturnSerial line, long uid, CancellationToken ct)
    {
        var unit = await db.ComponentSerials.FirstOrDefaultAsync(c => c.Id == line.ComponentSerialId, ct);
        if (unit is null) return null;

        var part = await db.Parts.AsNoTracking().FirstOrDefaultAsync(p => p.Id == r.PartId, ct);
        var techName = await db.Users.AsNoTracking().Where(u => u.Id == r.TechnicianId)
            .Select(u => u.FullName ?? u.Username).FirstOrDefaultAsync(ct) ?? $"technician #{r.TechnicianId}";

        var priorStatus = unit.Status.ToString();
        var customerId = unit.CustomerId;
        if (customerId is { } cid && !await db.Customers.AnyAsync(c => c.Id == cid, ct)) customerId = null;
        customerId ??= await ReturnsAccountAsync(db, techName, ct);

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
            DateReceived = Common.ShopClock.Today,
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
        return job.ServiceNo;
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

    /// <summary>The shipment never arrived. Only the units still outstanding are written off: one
    /// that was already booked onto the shelf last week is on the shelf, and retrospectively calling
    /// it lost would take a unit out of stock that is sitting in the building.</summary>
    private static async Task<Results<Ok<StockReturnDto>, NotFound, BadRequest<string>>> MissingAsync(
        long id, ClaimsPrincipal user, AppDbContext db, SerialService serial, StockLedgerService ledger,
        IAuditService audit, HttpContext http, CancellationToken ct)
    {
        var r = await db.StockReturns.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (r is null) return TypedResults.NotFound();
        if (r.Status != StockReturnStatus.Pending) return TypedResults.BadRequest($"Return is already {r.Status}.");

        user.TryGetUserId(out var uid);
        var now = DateTime.UtcNow;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            var serialLines = await db.StockReturnSerials
                .Where(s => s.StockReturnId == r.Id).ToListAsync(ct);
            var open = serialLines.Where(l => l.AckStatus is null).ToList();

            foreach (var line in open)
            {
                await serial.ChangeStatusAsync(line.ComponentSerialId, SerialStatus.Missing, uid,
                    $"Return {r.ReturnNo} reported missing in transit", ct);
                line.AckStatus = SerialAckStatus.Missing;
                line.AckedByUserId = uid;
                line.AckedAt = now;
            }

            // Units, when the shipment has them; otherwise whatever quantity is still unaccounted for.
            var lost = serialLines.Count > 0 ? open.Count : r.Qty - r.QtyReceived - r.QtyMissing;

            // A faulty shipment carries customers' units that were never on the technician's balance,
            // so there is no quantity to write off - only the units' own statuses change.
            if (r.Kind == StockReturnKind.GoodStock)
                await ledger.ReturnLostAsync(r.PartId, r.TechnicianId, lost, uid, "STOCK_RETURN", r.Id,
                    $"Return {r.ReturnNo} reported missing in transit", ct,
                    debitTechnician: !r.TechnicianDebitedOnShip);

            r.QtyMissing += lost;
            r.Status = r.QtyReceived > 0 ? StockReturnStatus.Stocked : StockReturnStatus.Missing;
            r.AcknowledgedByUserId = uid;
            r.AcknowledgedDate = now;
            audit.Log(uid, "stock-return.missing", "stock_return", r.Id,
                details: $"{lost} of {r.Qty} never arrived", ip: http.GetIp());
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (StockException ex) { await tx.RollbackAsync(ct); return TypedResults.BadRequest(ex.Message); }

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
                             select new StockReturnSerialDto(c.Id, c.SerialNumber, s.Defective,
                                 c.Status.ToString(), s.AckStatus != null ? s.AckStatus.ToString() : null))
            .ToListAsync(ct);
        // Jobs raised off this shipment, so the acknowledging user sees what was opened without
        // having to go looking for it on the service board.
        var serviceNos = await db.Services.AsNoTracking()
            .Where(j => j.ChallanNo == $"RTN-{x.r.ReturnNo}" && j.SourceComponentSerialId != null)
            .OrderBy(j => j.Id).Select(j => j.ServiceNo).ToListAsync(ct);

        return new StockReturnDto(x.r.Id, x.r.ReturnNo, x.r.TechnicianId, x.Username, x.r.PartId, x.ItemCode, x.Name,
            x.r.Qty, x.r.Status.ToString(), x.r.AcknowledgedDate, x.r.Remarks, x.r.CreatedAt,
            x.r.Courier, x.r.TrackingNo, serials, x.r.Kind.ToString(),
            serviceNos.Count > 0 ? serviceNos : null, x.r.QtyReceived, x.r.QtyMissing);
    }
}
