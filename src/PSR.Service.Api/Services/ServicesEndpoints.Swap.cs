using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PSR.Service.Api.Audit;
using PSR.Service.Api.Auth;
using PSR.Service.Api.Common;
using PSR.Service.Api.Data;
using PSR.Service.Api.Data.Entities;
using PSR.Service.Api.Stock;

namespace PSR.Service.Api.Services;

/// <summary>Advance replacement: hand the customer a working unit off the shelf today and keep theirs.
///
/// A swap has two physical outcomes and one job row can only end in one place — the replacement goes
/// OUT and must end Dispatched, the customer's unit STAYS and must end Stocked. So the swap splits the
/// job: the original keeps the customer and joins the pending-dispatch queue, and a retained job is
/// opened carrying the unit that stayed behind.
///
/// Nothing here invents a state. The retained job runs the ordinary Inward → … → Completed → Stocked
/// workflow, and the unit rides the statuses the field-return loop already defined: UNDER_REPAIR while
/// it is being worked on (deliberately not re-issuable), REPAIRED when it reaches the shelf.
///
/// See docs/advance-replacement-and-stocking.md.
/// </summary>
public static partial class ServicesEndpoints
{
    /// <summary>Where a swap may be issued from. Everything up to and including the pending-dispatch
    /// queue: the shop may decide to swap at the counter on day one, or after a fortnight on the
    /// bench, or when a finished job turns out to need a different unit. What it may NOT do is swap a
    /// job that has already gone out, been shelved or been written off — the machine is no longer here
    /// to keep.</summary>
    internal static readonly ServiceStatus[] SwappableStatuses =
    {
        ServiceStatus.Inward, ServiceStatus.Assigned, ServiceStatus.Acknowledged,
        ServiceStatus.InService, ServiceStatus.Completed,
    };

    // ---------------------------------------------------------------- issue

    private static async Task<Results<Ok<ServiceDetailDto>, NotFound, BadRequest<string>>> SwapAsync(
        long id, [FromBody] SwapRequest req, ClaimsPrincipal user, AppDbContext db,
        StockLedgerService ledger, SerialService serial, NumberSequenceService seq,
        IAuditService audit, HttpContext http, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.ReplacementSerialNo))
            return TypedResults.BadRequest("Enter the serial number of the replacement unit.");
        var outgoingSn = req.ReplacementSerialNo.Trim();

        var job = await db.Services.Include(s => s.Lines).FirstOrDefaultAsync(s => s.Id == id, ct);
        if (job is null) return TypedResults.NotFound();

        if (job.JobKind != JobKind.Customer)
            return TypedResults.BadRequest(
                $"{job.ServiceNo} is not a customer's job — it carries the service centre's own unit, "
                + "so there is nobody to issue a replacement to.");
        if (!SwappableStatuses.Contains(job.ServiceStatus))
            return TypedResults.BadRequest(
                $"A replacement can only be issued while the job is still here (currently {job.ServiceStatus}).");
        if (!string.IsNullOrWhiteSpace(job.ReplacementSerialNo))
            return TypedResults.BadRequest(
                $"{job.ServiceNo} already carries replacement unit {job.ReplacementSerialNo}. Cancel that "
                + "one before issuing another.");

        // The unit that goes OUT. An explicit pick wins — a unit is occasionally replaced with a
        // different model — but the normal case supplies none and it is the same item that came in,
        // which the job carries as its PS code.
        var (outgoing, outgoingError) = await ResolveReplacementPartAsync(db, job, req.ReplacementPartId, ct);
        if (outgoing is null) return TypedResults.BadRequest(outgoingError!);

        // The unit that STAYS. Defaults to the same item, because in the normal case the customer is
        // handed the same thing they brought in — which means nobody has to pick anything for the
        // usual swap to work.
        Part? retained = outgoing;
        if (req.RetainedPartId is { } rpid)
        {
            retained = await db.Parts.FirstOrDefaultAsync(p => p.Id == rpid, ct);
            if (retained is null) return TypedResults.BadRequest("The item the customer's unit is being kept as was not found.");
        }

        // Read before anything is written so an empty shelf reads as an empty shelf, naming the item
        // and what it actually holds. The ledger guards it again inside the transaction, which is what
        // stops two people issuing the last unit at once.
        var onHand = await db.StockBalances.AsNoTracking()
            .Where(b => b.PartId == outgoing.Id && b.TechnicianId == StockBalance.Warehouse)
            .Select(b => (int?)b.OnHand).FirstOrDefaultAsync(ct) ?? 0;
        if (onHand < 1)
            return TypedResults.BadRequest(
                $"{outgoing.ItemCode} — {outgoing.Name} has {onHand} in warehouse stock, so no replacement "
                + "can be issued. Restock it, or pick a different replacement part.");

        // A unit already at a customer or in a technician's bag cannot also be handed over here. The
        // total-loss route never checked this and could hand out a serial that was demonstrably
        // somewhere else.
        if (outgoing.IsSerialTracked)
        {
            var conflicts = await serial.FindIssueConflictsAsync(outgoing.Id, [outgoingSn], ct);
            if (conflicts.TryGetValue(outgoingSn, out var why))
                return TypedResults.BadRequest($"Replacement unit {outgoingSn} cannot be issued: {why}");
        }

        user.TryGetUserId(out var uid);
        var party = await PartyLabelAsync(db, job, ct);
        var statusBefore = job.ServiceStatus;

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            // 1. The replacement leaves the warehouse and becomes the customer's.
            await ledger.ReplacementOutAsync(outgoing.Id, 1, uid, job.Id, outgoingSn,
                $"Advance replacement for service {job.ServiceNo}", ct);
            if (outgoing.IsSerialTracked)
                await serial.InstallToCustomerAsync(outgoing.Id, outgoingSn, outgoing.Name, party,
                    SerialStatus.Used, uid, ct, job.CustomerId);

            job.ReplacementSerialNo = outgoingSn;
            job.ReplacementPartId = outgoing.Id;

            // 2. The retained job, carrying the unit that stayed. It starts at Inward with no
            //    technician: the work on it is the shop's own now, queued and assigned like any other
            //    inward item. DateReceived is the ORIGINAL receipt date, not today — the machine has
            //    been here since the customer brought it, and re-dating it would hide exactly the wait
            //    that made a swap necessary.
            var retainedJob = new ServiceJob
            {
                ServiceNo = await seq.NextAsync(SequenceKeys.Service, ct),
                ChallanNo = $"SWAP-{job.ServiceNo}",
                CustomerType = job.CustomerType,
                CustomerId = job.CustomerId,
                DealerId = job.DealerId,
                SerialNo = job.SerialNo,
                PsCode = retained.ItemCode,
                ModelName = job.ModelName,
                Description = job.Description ?? retained.Name,
                ReportedProblem = job.ReportedProblem,
                // Warranty is a promise to the customer, and the customer has been served. What is
                // left is the shop repairing its own unit, which no warranty covers either way.
                WarrantyStatus = WarrantyStatus.Unknown,
                InwardDcNo = job.InwardDcNo,
                DateReceived = job.DateReceived,
                Priority = job.Priority,
                ServiceStatus = ServiceStatus.Inward,
                AckStatus = AckStatus.Pending,
                JobKind = JobKind.SwapRetained,
                ParentServiceJobId = job.Id,
                CreatedByUserId = uid,
            };
            db.Services.Add(retainedJob);
            await db.SaveChangesAsync(ct);   // id, for the history row and the serial back-reference

            db.ServiceStatusHistory.Add(new ServiceStatusHistory
            {
                ServiceId = retainedJob.Id, FromStatus = null, ToStatus = ServiceStatus.Inward.ToString(),
                ChangedByUserId = uid,
                Note = $"Kept from {job.ServiceNo} ({party}) — replacement {outgoingSn} issued",
            });

            // 3. Ownership of the kept unit moves now; its quantity does not. It is broken, so it is
            //    deliberately not re-issuable and the shelf count only changes when the retained job
            //    is stocked. An untracked item has no unit record — the quantity is the whole of what
            //    there is to move, and it moves at stocking like everything else.
            long? incomingSerialId = null;
            var incomingSerialCreated = false;
            if (retained.IsSerialTracked && !string.IsNullOrWhiteSpace(job.SerialNo))
            {
                var existed = await db.ComponentSerials.AsNoTracking()
                    .AnyAsync(c => c.PartId == retained.Id && c.SerialNumber == job.SerialNo.Trim(), ct);
                var unit = await serial.AcquireToServiceCentreAsync(
                    retained.Id, job.SerialNo, job.Description ?? retained.Name, SerialStatus.UnderRepair,
                    uid, $"Kept from {job.ServiceNo} against replacement {outgoingSn}", ct,
                    serviceJobId: retainedJob.Id);
                incomingSerialId = unit?.Id;
                incomingSerialCreated = unit is not null && !existed;
                retainedJob.SourceComponentSerialId = incomingSerialId;
            }

            // 4. Work already booked describes the unit the customer is NOT getting, so it follows the
            //    unit to the retained job — but only while it is still unconsumed. Once the original
            //    was completed those parts already came off the technician's balance, and moving them
            //    would take them off a second time when the retained job completes.
            if (statusBefore is not ServiceStatus.Completed)
                foreach (var line in job.Lines)
                    line.ServiceId = retainedJob.Id;

            // 5. The bill. An out-of-warranty customer is being handed a unit off the shelf, and until
            //    now nothing charged for it — the total-loss route issues a replacement and adds no
            //    line at all. The line is billing-only and never consumes stock: the unit left through
            //    its own Replacement movement above, and a job carrying a replacement serial can never
            //    be reverted (RevertAsync refuses it), so completion can never run over it twice.
            //    Priced like every other service line, at the customer rate.
            if (job.WarrantyStatus != WarrantyStatus.InWarranty)
                db.ServiceLines.Add(new ServiceLine
                {
                    ServiceId = job.Id,
                    LineType = ServiceLineType.Replacement,
                    PartId = outgoing.Id,
                    Description = $"Replacement unit — {outgoing.Name}",
                    Qty = 1,
                    UnitPrice = outgoing.CustomerRate,
                    Amount = outgoing.CustomerRate,
                    ReplacementSerialNo = outgoingSn,
                });

            // 6. The trail. One row, both ends of the exchange, and what to put back if this is undone.
            db.ServiceReplacements.Add(new ServiceReplacement
            {
                OriginalServiceJobId = job.Id,
                RetainedServiceJobId = retainedJob.Id,
                Kind = ReplacementKind.AdvanceSwap,
                OutgoingPartId = outgoing.Id,
                OutgoingSerialNo = outgoingSn,
                IncomingPartId = retained.Id,
                IncomingSerialNo = string.IsNullOrWhiteSpace(job.SerialNo) ? null : job.SerialNo.Trim(),
                IncomingComponentSerialId = incomingSerialId,
                IncomingSerialCreated = incomingSerialCreated,
                StatusBeforeSwap = statusBefore.ToString(),
                CustomerId = job.CustomerId,
                DealerId = job.DealerId,
                Reason = req.Reason?.Trim(),
                ApprovedByUserId = uid,
            });

            // 7. The original joins the pending-dispatch queue. A job already sitting there does not
            //    "move" anywhere, so it gets an event rather than a transition — a Completed → Completed
            //    row says nothing and would show up in the status metrics as a change that never was.
            var note = req.Note
                ?? $"Advance replacement issued (SN {outgoingSn}) — unit kept on {retainedJob.ServiceNo}";
            if (statusBefore is ServiceStatus.Completed) WriteNote(db, job, "Swap", uid, note);
            else WriteTransition(db, job, ServiceStatus.Completed, uid, note);

            audit.Log(uid, "service.swap", "service", job.Id,
                details: $"out {outgoingSn}, kept on {retainedJob.ServiceNo}", ip: http.GetIp());
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (StockException ex) { await tx.RollbackAsync(ct); return TypedResults.BadRequest(ex.Message); }

        return TypedResults.Ok(await BuildDetailAsync(db, job, ServiceRoles.CanSeePricing(user), ct));
    }

    /// <summary>The item a replacement comes out of: an explicit pick, else the job's own PS code.
    /// Shared with the total-loss route so the two cannot drift — resolving this is what makes the
    /// warehouse move at all, and when it silently failed the shelf count drifted up by one unit
    /// every time somebody forgot to pick a part.</summary>
    private static async Task<(Part? Part, string? Error)> ResolveReplacementPartAsync(
        AppDbContext db, ServiceJob job, long? explicitPartId, CancellationToken ct)
    {
        if (explicitPartId is { } pid)
        {
            var picked = await db.Parts.FirstOrDefaultAsync(p => p.Id == pid, ct);
            return picked is null ? (null, "Replacement part not found.") : (picked, null);
        }
        if (string.IsNullOrWhiteSpace(job.PsCode))
            return (null, "This job carries no PS code, so the replacement cannot be taken out of stock. "
                        + "Pick the replacement part on the form.");

        // No IsActive filter: a retired code with units still on the shelf is exactly the case where
        // the count has to be decremented, and refusing it would leave it wrong.
        var code = job.PsCode.Trim();
        var part = await db.Parts.FirstOrDefaultAsync(p => p.ItemCode == code, ct);
        return part is null
            ? (null, $"No catalogue item matches PS code {code}, so the replacement cannot be taken out "
                   + "of stock. Pick the replacement part on the form.")
            : (part, null);
    }

    // ---------------------------------------------------------------- cancel

    /// <summary>Undo a swap. Allowed until the original job is dispatched, and not afterwards: at that
    /// point the customer has the unit, and getting it back is a stock return rather than an undo.
    ///
    /// Reverses all four effects — the shelf, the two units' custody, the lines that moved, and the
    /// retained job — because a half-undone swap is worse than either state. What it will not touch is
    /// the trail: the replacement row is marked cancelled, never deleted. A unit that went out and came
    /// back is a thing that happened, and the shelf count moved twice because of it.</summary>
    private static async Task<Results<Ok<ServiceDetailDto>, NotFound, BadRequest<string>>> CancelSwapAsync(
        long id, [FromBody] NoteRequest? req, ClaimsPrincipal user, AppDbContext db,
        StockLedgerService ledger, SerialService serial, IAuditService audit, HttpContext http, CancellationToken ct)
    {
        var job = await db.Services.Include(s => s.Lines).FirstOrDefaultAsync(s => s.Id == id, ct);
        if (job is null) return TypedResults.NotFound();

        var swap = await db.ServiceReplacements
            .Where(r => r.OriginalServiceJobId == job.Id && r.Kind == ReplacementKind.AdvanceSwap
                        && r.CancelledAt == null)
            .OrderByDescending(r => r.Id)
            .FirstOrDefaultAsync(ct);
        if (swap is null)
            return TypedResults.BadRequest($"{job.ServiceNo} has no advance replacement to cancel.");

        var retainedJob = swap.RetainedServiceJobId is { } rid
            ? await db.Services.Include(s => s.Lines).FirstOrDefaultAsync(s => s.Id == rid, ct)
            : null;
        if (await SwapCancelBlockedReasonAsync(db, job, retainedJob, ct) is { } blocked)
            return TypedResults.BadRequest(blocked);

        user.TryGetUserId(out var uid);
        var party = await PartyLabelAsync(db, job, ct);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            // 1. The replacement goes back on the shelf. It never left the building — the original was
            //    not dispatched, which is what this route checks before anything else.
            if (swap.OutgoingPartId is { } outPartId)
            {
                await ledger.ReplacementReturnAsync(outPartId, 1, uid, job.Id, swap.OutgoingSerialNo,
                    $"Advance replacement for {job.ServiceNo} cancelled — unit back on the shelf", ct);
                var outPart = await db.Parts.AsNoTracking().FirstOrDefaultAsync(p => p.Id == outPartId, ct);
                if (outPart?.IsSerialTracked == true)
                    await serial.AcquireToServiceCentreAsync(outPartId, swap.OutgoingSerialNo, outPart.Name,
                        SerialStatus.ReturnedToSc, uid,
                        $"Advance replacement for {job.ServiceNo} cancelled before dispatch", ct);
            }

            // 2. The customer's unit is theirs again. A record the swap created is removed outright:
            //    the shop never owned that unit, and a service-centre-owned row left behind would put
            //    a customer's machine on the re-issuable shelf. A record that pre-dated the swap is
            //    handed back to the customer instead, because deleting it would erase history that was
            //    never ours to erase.
            if (swap.IncomingComponentSerialId is { } inSerialId)
            {
                var unit = await db.ComponentSerials.FirstOrDefaultAsync(c => c.Id == inSerialId, ct);
                if (unit is not null)
                {
                    if (swap.IncomingSerialCreated)
                    {
                        var history = await db.SerialStatusHistory
                            .Where(h => h.ComponentSerialId == unit.Id).ToListAsync(ct);
                        db.SerialStatusHistory.RemoveRange(history);
                        db.ComponentSerials.Remove(unit);
                    }
                    else
                    {
                        unit.CurrentServiceJobId = null;
                        await serial.InstallToCustomerAsync(unit.PartId, unit.SerialNumber, unit.ItemName,
                            party, SerialStatus.Installed, uid, ct, job.CustomerId);
                    }
                }
            }

            // 3. The lines come back with the unit. The retained job cannot have grown any of its own:
            //    lines are only writable while a job is in service, and this route refuses a retained
            //    job that has moved past Assigned.
            if (retainedJob is not null)
                foreach (var line in retainedJob.Lines)
                    line.ServiceId = job.Id;

            // 4. The swap's own billing line goes. Matched on the outgoing serial rather than on the
            //    part, so a Replacement line the technician happened to add by hand is left alone.
            var billed = job.Lines.Where(l => l.LineType == ServiceLineType.Replacement
                && l.ReplacementSerialNo == swap.OutgoingSerialNo).ToList();
            db.ServiceLines.RemoveRange(billed);

            job.ReplacementSerialNo = null;
            job.ReplacementPartId = null;

            // 5. The retained job is withdrawn rather than erased, the same way any other job is.
            if (retainedJob is not null)
            {
                retainedJob.IsDeleted = true;
                WriteNote(db, retainedJob, "SwapCancelled", uid,
                    $"Advance replacement on {job.ServiceNo} cancelled — unit returned to the customer");
            }

            swap.CancelledAt = DateTime.UtcNow;
            swap.CancelledByUserId = uid;

            // 6. And the original goes back to where the swap found it.
            var note = req?.Note ?? $"Advance replacement {swap.OutgoingSerialNo} cancelled";
            if (Enum.TryParse<ServiceStatus>(swap.StatusBeforeSwap, out var was) && was != job.ServiceStatus)
                WriteTransition(db, job, was, uid, note);
            else
                WriteNote(db, job, "SwapCancelled", uid, note);

            audit.Log(uid, "service.swap-cancel", "service", job.Id,
                details: swap.OutgoingSerialNo, ip: http.GetIp());
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (StockException ex) { await tx.RollbackAsync(ct); return TypedResults.BadRequest(ex.Message); }

        return TypedResults.Ok(await BuildDetailAsync(db, job, ServiceRoles.CanSeePricing(user), ct));
    }

    /// <summary>Why this swap can no longer be undone, or null when it can. Each rule is a thing that
    /// has been built on top of the swap and cannot be taken back out from the desk.</summary>
    private static async Task<string?> SwapCancelBlockedReasonAsync(
        AppDbContext db, ServiceJob job, ServiceJob? retainedJob, CancellationToken ct)
    {
        if (job.ServiceStatus is not (ServiceStatus.Completed or ServiceStatus.ReplacementApprovalPending))
            return $"{job.ServiceNo} is {job.ServiceStatus} — the replacement has already left the counter, "
                 + "so it comes back through stock rather than by cancelling.";
        if (job.PaymentStatus != PaymentStatus.Pending)
            return "Cannot cancel — a payment has already been recorded against this job.";
        if (!string.IsNullOrWhiteSpace(job.PiNo) || !string.IsNullOrWhiteSpace(job.InvNo)
            || !string.IsNullOrWhiteSpace(job.OutwardDcNo))
            return "Cannot cancel — a PI, invoice or delivery challan has already been raised for this job.";
        if (await db.ServiceDocumentLines.AnyAsync(l => l.ServiceJobId == job.Id, ct))
            return "Cannot cancel — this job is already on a generated document.";

        if (retainedJob is null) return null;
        if (retainedJob.IsDeleted) return "This swap has already been cancelled.";
        if (retainedJob.ServiceStatus is not (ServiceStatus.Inward or ServiceStatus.Assigned))
            return $"Cannot cancel — work has already started on {retainedJob.ServiceNo} "
                 + $"(currently {retainedJob.ServiceStatus}).";
        return null;
    }

    // ---------------------------------------------------------------- lookup

    /// <summary>"This unit has come back — was it one of ours?" Answered by serial, matching either
    /// end of an exchange: a unit that went out as a replacement, or one that was kept in place of
    /// one. Cancelled swaps are included and say so; they are part of what happened to the unit.</summary>
    private static async Task<Ok<List<ServiceReplacementDto>>> ReplacementsAsync(
        AppDbContext db, string? serial, long? serviceId, CancellationToken ct)
    {
        var q = from r in db.ServiceReplacements.AsNoTracking()
                join o in db.Services on r.OriginalServiceJobId equals o.Id into og
                from o in og.DefaultIfEmpty()
                join k in db.Services on r.RetainedServiceJobId equals (long?)k.Id into kg
                from k in kg.DefaultIfEmpty()
                join op in db.Parts on r.OutgoingPartId equals (long?)op.Id into opg
                from op in opg.DefaultIfEmpty()
                join ip in db.Parts on r.IncomingPartId equals (long?)ip.Id into ipg
                from ip in ipg.DefaultIfEmpty()
                join c in db.Customers on r.CustomerId equals (long?)c.Id into cg
                from c in cg.DefaultIfEmpty()
                join d in db.Dealers on r.DealerId equals (long?)d.Id into dg
                from d in dg.DefaultIfEmpty()
                join u in db.Users on r.ApprovedByUserId equals u.Id into ug
                from u in ug.DefaultIfEmpty()
                select new { r, OriginalNo = o != null ? o.ServiceNo : null, RetainedNo = k != null ? k.ServiceNo : null,
                             OutCode = op != null ? op.ItemCode : null, InCode = ip != null ? ip.ItemCode : null,
                             Party = c != null ? c.Name : (d != null ? d.Name : null),
                             ApprovedBy = u != null ? u.Username : null };

        if (!string.IsNullOrWhiteSpace(serial))
        {
            var sn = serial.Trim();
            q = q.Where(x => x.r.OutgoingSerialNo == sn || x.r.IncomingSerialNo == sn);
        }
        if (serviceId is { } sid)
            q = q.Where(x => x.r.OriginalServiceJobId == sid || x.r.RetainedServiceJobId == sid);

        var rows = await q.OrderByDescending(x => x.r.Id).Take(100).ToListAsync(ct);
        return TypedResults.Ok(rows.Select(x => new ServiceReplacementDto(
            x.r.Id, x.r.Kind.ToString(), x.r.OriginalServiceJobId, x.OriginalNo,
            x.r.RetainedServiceJobId, x.RetainedNo,
            x.r.OutgoingSerialNo, x.OutCode, x.r.IncomingSerialNo, x.InCode,
            x.Party, x.r.Reason, x.ApprovedBy, x.r.CreatedAt, x.r.CancelledAt)).ToList());
    }
}
