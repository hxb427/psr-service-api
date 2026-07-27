using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PSR.Service.Api.Audit;
using PSR.Service.Api.Auth;
using PSR.Service.Api.Data;
using PSR.Service.Api.Data.Entities;
using PSR.Service.Api.Stock;

namespace PSR.Service.Api.Services;

public static partial class ServicesEndpoints
{
    // Statuses from "completed" onward — payment / documents only apply here.
    private static readonly ServiceStatus[] CompletedOrLater =
    {
        ServiceStatus.Completed, ServiceStatus.ReplacementApprovalPending,
        ServiceStatus.Dispatched, ServiceStatus.Stocked, ServiceStatus.Replaced, ServiceStatus.TotalLoss,
    };

    // ---------------------------------------------------------------- assignment + acknowledgement

    private static async Task<Results<Ok<ServiceDetailDto>, NotFound, BadRequest<string>>> AssignAsync(
        long id, [FromBody] AssignRequest req, ClaimsPrincipal user, AppDbContext db, IAuditService audit, HttpContext http, CancellationToken ct)
    {
        var job = await db.Services.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (job is null) return TypedResults.NotFound();
        // Assign at inward; re-assign is allowed only while still Assigned (before the technician acknowledges).
        if (job.ServiceStatus is not (ServiceStatus.Inward or ServiceStatus.Assigned))
            return TypedResults.BadRequest($"A technician can only be (re)assigned before acknowledgement (currently {job.ServiceStatus}).");

        var tech = await db.Users.FirstOrDefaultAsync(u => u.Id == req.TechnicianId, ct);
        if (tech is null || !tech.IsActive) return TypedResults.BadRequest("Technician not found or inactive.");
        if (!await UserHasRoleAsync(db, req.TechnicianId, RoleNames.Technician, ct))
            return TypedResults.BadRequest("Selected user is not a technician.");

        user.TryGetUserId(out var uid);
        var reassign = job.ServiceStatus == ServiceStatus.Assigned;
        job.TechnicianId = req.TechnicianId;
        if (!string.IsNullOrWhiteSpace(req.Priority) && Enum.TryParse<Priority>(req.Priority, true, out var pr))
            job.Priority = pr;
        if (req.PromisedDate is { } pd) job.PromisedDate = pd;
        WriteTransition(db, job, ServiceStatus.Assigned, uid, $"{(reassign ? "Re-assigned" : "Assigned")} to {tech.Username}");
        audit.Log(uid, reassign ? "service.reassign" : "service.assign", "service", job.Id, details: tech.Username, ip: http.GetIp());
        await db.SaveChangesAsync(ct);

        return TypedResults.Ok(await BuildDetailAsync(db, job, ServiceRoles.CanSeePricing(user), ct));
    }

    private static async Task<Results<Ok<ServiceDetailDto>, NotFound, BadRequest<string>, ForbidHttpResult>> AcknowledgeAsync(
        long id, [FromBody] NoteRequest? req, ClaimsPrincipal user, AppDbContext db, IAuditService audit, HttpContext http, CancellationToken ct)
    {
        var job = await db.Services.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (job is null) return TypedResults.NotFound();
        if (!ServiceRoles.IsAssignedTechnician(user, job)) return TypedResults.Forbid();   // only the assigned technician
        if (job.ServiceStatus is not ServiceStatus.Assigned)
            return TypedResults.BadRequest($"Only an assigned job can be acknowledged (currently {job.ServiceStatus}).");

        // Acknowledge = the technician confirms receipt; it does NOT start the work (that's /start).
        user.TryGetUserId(out var uid);
        job.AckStatus = AckStatus.Acknowledged;
        WriteTransition(db, job, ServiceStatus.Acknowledged, uid, req?.Note ?? "Received by technician");
        audit.Log(uid, "service.acknowledge", "service", job.Id, ip: http.GetIp());
        await db.SaveChangesAsync(ct);

        return TypedResults.Ok(await BuildDetailAsync(db, job, ServiceRoles.CanSeePricing(user), ct));
    }

    private static async Task<Results<Ok<ServiceDetailDto>, NotFound, BadRequest<string>, ForbidHttpResult>> StartAsync(
        long id, [FromBody] NoteRequest? req, ClaimsPrincipal user, AppDbContext db, IAuditService audit, HttpContext http, CancellationToken ct)
    {
        var job = await db.Services.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (job is null) return TypedResults.NotFound();
        if (!ServiceRoles.IsAssignedTechnician(user, job)) return TypedResults.Forbid();
        if (job.ServiceStatus is not ServiceStatus.Acknowledged)
            return TypedResults.BadRequest($"Acknowledge the job before starting service (currently {job.ServiceStatus}).");

        user.TryGetUserId(out var uid);
        WriteTransition(db, job, ServiceStatus.InService, uid, req?.Note ?? "Service started by technician");
        audit.Log(uid, "service.start", "service", job.Id, ip: http.GetIp());
        await db.SaveChangesAsync(ct);

        return TypedResults.Ok(await BuildDetailAsync(db, job, ServiceRoles.CanSeePricing(user), ct));
    }

    private static async Task<Results<Ok<ServiceDetailDto>, NotFound, BadRequest<string>, ForbidHttpResult>> MarkTotalLossAsync(
        long id, [FromBody] NoteRequest? req, ClaimsPrincipal user, AppDbContext db, IAuditService audit, HttpContext http, CancellationToken ct)
    {
        var job = await db.Services.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (job is null) return TypedResults.NotFound();
        if (!ServiceRoles.IsAssignedTechnician(user, job)) return TypedResults.Forbid();
        if (job.ServiceStatus is not ServiceStatus.InService)
            return TypedResults.BadRequest($"Total loss can only be set while the job is in service (currently {job.ServiceStatus}).");

        user.TryGetUserId(out var uid);
        job.IsTotalLoss = !job.IsTotalLoss;   // toggle
        WriteNote(db, job, "TotalLoss", uid, job.IsTotalLoss ? "Marked total loss" : "Total loss cleared");
        audit.Log(uid, "service.total-loss", "service", job.Id, details: job.IsTotalLoss ? "marked" : "cleared", ip: http.GetIp());
        await db.SaveChangesAsync(ct);

        return TypedResults.Ok(await BuildDetailAsync(db, job, ServiceRoles.CanSeePricing(user), ct));
    }

    private static async Task<Results<Ok<ServiceDetailDto>, NotFound, BadRequest<string>>> RevertAsync(
        long id, [FromBody] NoteRequest? req, ClaimsPrincipal user, AppDbContext db,
        StockLedgerService ledger, SerialService serial, IAuditService audit, HttpContext http, CancellationToken ct)
    {
        var job = await db.Services.Include(s => s.Lines).FirstOrDefaultAsync(s => s.Id == id, ct);
        if (job is null) return TypedResults.NotFound();
        if (job.ServiceStatus is not (ServiceStatus.Completed or ServiceStatus.ReplacementApprovalPending))
            return TypedResults.BadRequest($"Only a completed job can be reverted (currently {job.ServiceStatus}).");
        if (job.PaymentStatus != PaymentStatus.Pending)
            return TypedResults.BadRequest("Cannot revert — a payment has already been recorded.");
        // A generated PI / Invoice / DC freezes the billed figures — reverting would let the lines change underneath it.
        if (await db.ServiceDocumentLines.AnyAsync(l => l.ServiceJobId == id, ct))
            return TypedResults.BadRequest("Cannot revert — a PI, invoice or delivery challan has already been generated for this job.");

        user.TryGetUserId(out var uid);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            // Completion consumed the technician's parts — reverting returns them, so a re-complete
            // consumes once (not twice). Serial-tracked units fitted at completion come back too.
            if (job.TechnicianId is { } techId)
            {
                var techName = await db.Users.AsNoTracking().Where(u => u.Id == techId)
                    .Select(u => u.FullName ?? u.Username).FirstOrDefaultAsync(ct) ?? $"#{techId}";
                foreach (var line in job.Lines.Where(l => l.PartId.HasValue
                    && l.LineType is ServiceLineType.Component or ServiceLineType.Replacement))
                {
                    await ledger.ReverseConsumptionAsync(line.PartId!.Value, techId, line.Qty, uid, "SERVICE", job.Id, ct);
                    if (!string.IsNullOrWhiteSpace(line.ReplacementSerialNo))
                        await serial.UninstallToTechnicianAsync(line.PartId!.Value, line.ReplacementSerialNo!,
                            techId, techName, uid, ct);
                }
            }

            WriteTransition(db, job, ServiceStatus.InService, uid, req?.Note ?? "Service reverted to in-service");
            audit.Log(uid, "service.revert", "service", job.Id, ip: http.GetIp());
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (StockException ex) { await tx.RollbackAsync(ct); return TypedResults.BadRequest(ex.Message); }

        return TypedResults.Ok(await BuildDetailAsync(db, job, ServiceRoles.CanSeePricing(user), ct));
    }

    private static async Task<Results<Ok<ServiceDetailDto>, NotFound, BadRequest<string>>> DispatchAsync(
        long id, [FromBody] DispatchRequest req, ClaimsPrincipal user, AppDbContext db, IAuditService audit, HttpContext http, CancellationToken ct)
    {
        // Reference number is mandatory; the outward DC number is optional.
        if (string.IsNullOrWhiteSpace(req.ReferenceNo)) return TypedResults.BadRequest("Reference number is required.");
        var job = await db.Services.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (job is null) return TypedResults.NotFound();
        if (job.ServiceStatus is not ServiceStatus.Completed)
            return TypedResults.BadRequest($"Only a completed job can be dispatched (currently {job.ServiceStatus}).");

        user.TryGetUserId(out var uid);
        job.OutwardReferenceNo = req.ReferenceNo.Trim();
        job.OutwardDcNo = string.IsNullOrWhiteSpace(req.OutwardDcNo) ? null : req.OutwardDcNo.Trim();
        job.DcDate = req.DcDate ?? DateTime.UtcNow;
        var note = $"Dispatched (ref {job.OutwardReferenceNo}" + (job.OutwardDcNo is null ? ")" : $", DC {job.OutwardDcNo})");
        WriteTransition(db, job, ServiceStatus.Dispatched, uid, note);
        audit.Log(uid, "service.dispatch", "service", job.Id, details: job.OutwardReferenceNo, ip: http.GetIp());
        await db.SaveChangesAsync(ct);

        return TypedResults.Ok(await BuildDetailAsync(db, job, ServiceRoles.CanSeePricing(user), ct));
    }

    private static Task<Results<Ok<ServiceDetailDto>, NotFound, BadRequest<string>>> StockJobAsync(
        long id, [FromBody] NoteRequest? req, ClaimsPrincipal user, AppDbContext db, IAuditService audit, HttpContext http, CancellationToken ct)
        => SimpleTransitionAsync(id, [ServiceStatus.Completed], ServiceStatus.Stocked, "service.stock",
            null, req?.Note, user, db, audit, http, ct);

    // Dispatch role overrides a total-loss call: send the job back to normal Completed (dispatchable).
    private static async Task<Results<Ok<ServiceDetailDto>, NotFound, BadRequest<string>>> RejectReplacementAsync(
        long id, [FromBody] NoteRequest? req, ClaimsPrincipal user, AppDbContext db, IAuditService audit, HttpContext http, CancellationToken ct)
    {
        var job = await db.Services.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (job is null) return TypedResults.NotFound();
        if (job.ServiceStatus is not ServiceStatus.ReplacementApprovalPending)
            return TypedResults.BadRequest($"Only a replacement-pending job can be sent back to dispatch (currently {job.ServiceStatus}).");

        user.TryGetUserId(out var uid);
        job.IsTotalLoss = false;   // overridden — treat as a normal completed job
        WriteTransition(db, job, ServiceStatus.Completed, uid, req?.Note ?? "Replacement rejected — dispatch normally");
        audit.Log(uid, "service.replacement-reject", "service", job.Id, ip: http.GetIp());
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(await BuildDetailAsync(db, job, ServiceRoles.CanSeePricing(user), ct));
    }

    private static Task<Results<Ok<ServiceDetailDto>, NotFound, BadRequest<string>>> LeaveTotalLossAsync(
        long id, [FromBody] NoteRequest? req, ClaimsPrincipal user, AppDbContext db, IAuditService audit, HttpContext http, CancellationToken ct)
        => SimpleTransitionAsync(id, [ServiceStatus.ReplacementApprovalPending], ServiceStatus.TotalLoss, "service.discard",
            null, req?.Note ?? "Discarded — total loss, no replacement", user, db, audit, http, ct);

    private static async Task<Results<Ok<ServiceDetailDto>, NotFound, BadRequest<string>>> PaymentAsync(
        long id, [FromBody] PaymentRequest req, ClaimsPrincipal user, AppDbContext db, IAuditService audit, HttpContext http, CancellationToken ct)
    {
        if (!Enum.TryParse<PaymentStatus>(req.Status, true, out var ps))
            return TypedResults.BadRequest($"Unknown payment status '{req.Status}'.");
        var job = await db.Services.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (job is null) return TypedResults.NotFound();
        if (!CompletedOrLater.Contains(job.ServiceStatus))
            return TypedResults.BadRequest("Payment can only be set once the service is completed.");

        user.TryGetUserId(out var uid);
        var was = job.PaymentStatus;
        job.PaymentStatus = ps;
        WriteNote(db, job, "Payment", uid, $"Payment {was} → {ps}");
        audit.Log(uid, "service.payment", "service", job.Id, details: ps.ToString(), ip: http.GetIp());
        await db.SaveChangesAsync(ct);

        return TypedResults.Ok(await BuildDetailAsync(db, job, ServiceRoles.CanSeePricing(user), ct));
    }

    // ---------------------------------------------------------------- manual document / reference stamps

    // Set the courier / gate-pass reference WITHOUT dispatching. The legacy Pending-Dispatch and
    // Global-Search pages both had this: the reference often arrives after the job has already moved on.
    private static async Task<Results<Ok<ServiceDetailDto>, NotFound, BadRequest<string>>> SetOutwardReferenceAsync(
        long id, [FromBody] OutwardReferenceRequest req, ClaimsPrincipal user, AppDbContext db,
        IAuditService audit, HttpContext http, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.ReferenceNo)) return TypedResults.BadRequest("Reference number is required.");
        var job = await db.Services.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (job is null) return TypedResults.NotFound();

        user.TryGetUserId(out var uid);
        job.OutwardReferenceNo = req.ReferenceNo.Trim();
        var note = $"Outward reference set to {job.OutwardReferenceNo}";
        // Only overwrite the DC number when one was supplied — a blank field must not wipe a generated DC.
        if (!string.IsNullOrWhiteSpace(req.OutwardDcNo))
        {
            job.OutwardDcNo = req.OutwardDcNo.Trim();
            note += $", DC {job.OutwardDcNo}";
        }
        WriteNote(db, job, "OutwardRef", uid, note);
        audit.Log(uid, "service.outward-reference", "service", job.Id, details: job.OutwardReferenceNo, ip: http.GetIp());
        await db.SaveChangesAsync(ct);

        return TypedResults.Ok(await BuildDetailAsync(db, job, ServiceRoles.CanSeePricing(user), ct));
    }

    // Record an invoice raised outside the app (legacy "Set Invoice No"). Generating an invoice here
    // stamps the same field, so refuse to silently overwrite one that a generated document owns.
    private static async Task<Results<Ok<ServiceDetailDto>, NotFound, BadRequest<string>>> SetInvoiceNoAsync(
        long id, [FromBody] InvoiceNoRequest req, ClaimsPrincipal user, AppDbContext db,
        IAuditService audit, HttpContext http, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.InvNo)) return TypedResults.BadRequest("Invoice number is required.");
        var job = await db.Services.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (job is null) return TypedResults.NotFound();
        if (!CompletedOrLater.Contains(job.ServiceStatus))
            return TypedResults.BadRequest($"An invoice number can only be recorded once the service is completed (currently {job.ServiceStatus}).");
        // Explicit join rather than the Document nav — the line's relationship is convention-mapped only.
        var generatedInvoice = await (from l in db.ServiceDocumentLines
                                      join d in db.ServiceDocuments on l.DocumentId equals d.Id
                                      where l.ServiceJobId == id && d.DocType == DocumentType.Invoice
                                      select l.Id).AnyAsync(ct);
        if (generatedInvoice) return TypedResults.BadRequest("This job is already covered by a generated invoice.");

        user.TryGetUserId(out var uid);
        var was = job.InvNo;
        job.InvNo = req.InvNo.Trim();
        job.InvDate = req.InvDate ?? DateTime.UtcNow;
        WriteNote(db, job, "InvoiceNo", uid,
            was is null ? $"Invoice number set to {job.InvNo}" : $"Invoice number {was} → {job.InvNo}");
        audit.Log(uid, "service.invoice-no", "service", job.Id, details: job.InvNo, ip: http.GetIp());
        await db.SaveChangesAsync(ct);

        return TypedResults.Ok(await BuildDetailAsync(db, job, ServiceRoles.CanSeePricing(user), ct));
    }

    private static async Task<Results<NoContent, NotFound, BadRequest<string>>> SoftDeleteAsync(
        long id, ClaimsPrincipal user, AppDbContext db, IAuditService audit, HttpContext http, CancellationToken ct)
    {
        var job = await db.Services.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (job is null) return TypedResults.NotFound();
        // A billed job must not vanish from underneath its paperwork — the document still references it,
        // and every list filters IsDeleted out, so the invoice would lose its line.
        if (await db.ServiceDocumentLines.AnyAsync(l => l.ServiceJobId == id, ct))
            return TypedResults.BadRequest("Cannot delete — a PI, invoice or delivery challan has already been generated for this job.");
        if (!string.IsNullOrWhiteSpace(job.PiNo) || !string.IsNullOrWhiteSpace(job.InvNo)
            || !string.IsNullOrWhiteSpace(job.OutwardDcNo))
            return TypedResults.BadRequest("Cannot delete — this job already carries a PI, invoice or DC number.");

        user.TryGetUserId(out var uid);
        job.IsDeleted = true;
        WriteNote(db, job, "Deleted", uid, "Job deleted (hidden from all lists)");
        audit.Log(uid, "service.delete", "service", job.Id, details: job.ServiceNo, ip: http.GetIp());
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    // ---------------------------------------------------------------- complete (consumes technician stock)

    private static async Task<Results<Ok<ServiceDetailDto>, NotFound, BadRequest<string>, ForbidHttpResult>> CompleteAsync(
        long id, [FromBody] CompleteRequest? req, ClaimsPrincipal user, AppDbContext db,
        StockLedgerService ledger, SerialService serial, IAuditService audit, HttpContext http, CancellationToken ct)
    {
        var job = await db.Services.Include(s => s.Lines).FirstOrDefaultAsync(s => s.Id == id, ct);
        if (job is null) return TypedResults.NotFound();
        if (!ServiceRoles.IsAssignedTechnician(user, job)) return TypedResults.Forbid();
        if (job.ServiceStatus is not ServiceStatus.InService)
            return TypedResults.BadRequest($"Only an in-service job can be completed (currently {job.ServiceStatus}).");
        if (job.TechnicianId is not { } techId)
            return TypedResults.BadRequest("Assign a technician before completing the job.");

        user.TryGetUserId(out var uid);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            // Consume the technician's issued stock for each part-bearing line.
            foreach (var line in job.Lines.Where(l => l.PartId.HasValue
                && l.LineType is ServiceLineType.Component or ServiceLineType.Replacement))
                await ledger.ConsumeAsync(line.PartId!.Value, techId, line.Qty, uid, "SERVICE", job.Id, ct);

            // Move any serial-tracked parts fitted/handed to the customer into the serial ledger.
            await InstallJobSerialsAsync(db, serial, job, uid, ct);

            if (req?.TechnicianRemarks is { } remarks) job.TechnicianRemarks = remarks.Trim();
            // A total-loss job routes to replacement-approval instead of plain pending-dispatch.
            var to = job.IsTotalLoss ? ServiceStatus.ReplacementApprovalPending : ServiceStatus.Completed;
            WriteTransition(db, job, to, uid, job.IsTotalLoss ? "Completed — total loss, replacement pending" : "Service completed");
            audit.Log(uid, "service.complete", "service", job.Id, details: job.IsTotalLoss ? "total-loss" : null, ip: http.GetIp());
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (StockException ex) { await tx.RollbackAsync(ct); return TypedResults.BadRequest(ex.Message); }

        return TypedResults.Ok(await BuildDetailAsync(db, job, ServiceRoles.CanSeePricing(user), ct));
    }

    // ---------------------------------------------------------------- replace whole unit (decrements warehouse)

    private static async Task<Results<Ok<ServiceDetailDto>, NotFound, BadRequest<string>>> ReplaceAsync(
        long id, [FromBody] ReplaceRequest req, ClaimsPrincipal user, AppDbContext db,
        StockLedgerService ledger, SerialService serial, IAuditService audit, HttpContext http, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.ReplacementSerialNo))
            return TypedResults.BadRequest("Replacement serial number is required.");
        var job = await db.Services.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (job is null) return TypedResults.NotFound();
        if (job.ServiceStatus is not ServiceStatus.ReplacementApprovalPending)
            return TypedResults.BadRequest($"A replacement can only be issued for a total-loss job awaiting replacement (currently {job.ServiceStatus}).");

        var qty = req.Qty < 1 ? 1 : req.Qty;
        Part? part = null;
        if (req.ReplacementPartId is { } pid)
        {
            part = await db.Parts.FirstOrDefaultAsync(p => p.Id == pid, ct);
            if (part is null) return TypedResults.BadRequest("Replacement part not found.");
        }

        user.TryGetUserId(out var uid);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            // Ship the replacement unit out of the warehouse only when it maps to a catalog part.
            if (part is not null)
            {
                await ledger.ReplacementOutAsync(part.Id, qty, uid, job.Id,
                    req.ReplacementSerialNo.Trim(), $"Replacement for service {job.ServiceNo}", ct);
                // A serial-tracked replacement unit is now deployed to the customer.
                if (part.IsSerialTracked)
                    await serial.InstallToCustomerAsync(part.Id, req.ReplacementSerialNo.Trim(), part.Name,
                        await PartyLabelAsync(db, job, ct), SerialStatus.Used, uid, ct);
            }

            job.ReplacementSerialNo = req.ReplacementSerialNo.Trim();
            job.ReplacementPartId = part?.Id;
            WriteTransition(db, job, ServiceStatus.Replaced, uid,
                req.Note ?? $"Unit replaced (SN {job.ReplacementSerialNo})");
            audit.Log(uid, "service.replace", "service", job.Id, details: job.ReplacementSerialNo, ip: http.GetIp());
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (StockException ex) { await tx.RollbackAsync(ct); return TypedResults.BadRequest(ex.Message); }

        return TypedResults.Ok(await BuildDetailAsync(db, job, ServiceRoles.CanSeePricing(user), ct));
    }

    /// <summary>On completion, transition every serial-tracked part fitted/handed to the customer to
    /// INSTALLED (component) / USED (replacement) with owner CUSTOMER. The per-line serial lives in
    /// <see cref="ServiceLine.ReplacementSerialNo"/> (captured when the line was added).</summary>
    private static async Task InstallJobSerialsAsync(
        AppDbContext db, SerialService serial, ServiceJob job, long uid, CancellationToken ct)
    {
        var lines = job.Lines.Where(l => l.PartId.HasValue
            && l.LineType is ServiceLineType.Component or ServiceLineType.Replacement
            && !string.IsNullOrWhiteSpace(l.ReplacementSerialNo)).ToList();
        if (lines.Count == 0) return;

        var party = await PartyLabelAsync(db, job, ct);
        var partCache = new Dictionary<long, Part?>();
        foreach (var line in lines)
        {
            var pid = line.PartId!.Value;
            if (!partCache.TryGetValue(pid, out var part))
                partCache[pid] = part = await db.Parts.FirstOrDefaultAsync(p => p.Id == pid, ct);
            if (part is null || !part.IsSerialTracked) continue;

            var newStatus = line.LineType == ServiceLineType.Replacement ? SerialStatus.Used : SerialStatus.Installed;
            await serial.InstallToCustomerAsync(pid, line.ReplacementSerialNo!.Trim(), part.Name, party, newStatus, uid, ct);
        }
    }
}
