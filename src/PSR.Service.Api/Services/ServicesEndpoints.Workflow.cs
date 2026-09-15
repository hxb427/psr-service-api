using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PSR.Service.Api.Audit;
using PSR.Service.Api.Auth;
using PSR.Service.Api.Data;
using PSR.Service.Api.Data.Entities;
using PSR.Service.Api.Stock;

using PSR.Service.Api.Common;

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

    /// <summary>Assign at inward; re-assign is allowed only while still Assigned (before the technician
    /// acknowledges). Idempotent: re-assigning a job to the technician it already has is a no-op success,
    /// so a replayed request cannot turn into a spurious failure.</summary>
    internal static ApplyResult ApplyAssign(ServiceJob job, AssignRequest req, string techUsername,
        long uid, AppDbContext db, IAuditService audit, string? ip)
    {
        if (job.ServiceStatus == ServiceStatus.Assigned && job.TechnicianId == req.TechnicianId)
            return ApplyResult.Applied;
        if (job.ServiceStatus is not (ServiceStatus.Inward or ServiceStatus.Assigned))
            return ApplyResult.Invalid($"A technician can only be (re)assigned before acknowledgement (currently {job.ServiceStatus}).");

        var reassign = job.ServiceStatus == ServiceStatus.Assigned;
        job.TechnicianId = req.TechnicianId;
        if (!string.IsNullOrWhiteSpace(req.Priority) && Enum.TryParse<Priority>(req.Priority, true, out var pr))
            job.Priority = pr;
        if (ShopClock.BusinessDate(req.PromisedDate) is { } pd) job.PromisedDate = pd;
        WriteTransition(db, job, ServiceStatus.Assigned, uid, $"{(reassign ? "Re-assigned" : "Assigned")} to {techUsername}");
        audit.Log(uid, reassign ? "service.reassign" : "service.assign", "service", job.Id, details: techUsername, ip: ip);
        return ApplyResult.Applied;
    }

    private static async Task<Results<Ok<ServiceDetailDto>, NotFound, BadRequest<string>, ForbidHttpResult>> AssignAsync(
        long id, [FromBody] AssignRequest req, ClaimsPrincipal user, AppDbContext db, IAuditService audit, HttpContext http, CancellationToken ct)
    {
        var job = await db.Services.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (job is null) return TypedResults.NotFound();

        var tech = await db.Users.FirstOrDefaultAsync(u => u.Id == req.TechnicianId, ct);
        if (tech is null || !tech.IsActive) return TypedResults.BadRequest("Technician not found or inactive.");
        if (!await UserHasRoleAsync(db, req.TechnicianId, RoleNames.Technician, ct))
            return TypedResults.BadRequest("Selected user is not a technician.");

        user.TryGetUserId(out var uid);
        if (ApplyAssign(job, req, tech.Username, uid, db, audit, http.GetIp()).ToProblem() is { } problem)
            return problem;
        await db.SaveChangesAsync(ct);

        return TypedResults.Ok(await BuildDetailAsync(db, job, ServiceRoles.CanSeePricing(user), ct));
    }

    /// <summary>Acknowledge = the technician confirms receipt; it does NOT start the work (that's /start).
    /// <see cref="AckStatus.Acknowledged"/> is set here and never cleared, so it doubles as the marker
    /// that makes a replay a no-op success rather than a "currently InService" failure.</summary>
    internal static ApplyResult ApplyAcknowledge(ServiceJob job, ClaimsPrincipal user, string? note,
        long uid, AppDbContext db, IAuditService audit, string? ip)
    {
        if (!ServiceRoles.IsAssignedTechnician(user, job))   // only the assigned technician
            return ApplyResult.Forbidden("Only the assigned technician can acknowledge this job.");
        if (job.AckStatus == AckStatus.Acknowledged) return ApplyResult.Applied;
        if (job.ServiceStatus is not ServiceStatus.Assigned)
            return ApplyResult.Invalid($"Only an assigned job can be acknowledged (currently {job.ServiceStatus}).");

        job.AckStatus = AckStatus.Acknowledged;
        WriteTransition(db, job, ServiceStatus.Acknowledged, uid, note ?? "Received by technician");
        audit.Log(uid, "service.acknowledge", "service", job.Id, ip: ip);
        return ApplyResult.Applied;
    }

    private static async Task<Results<Ok<ServiceDetailDto>, NotFound, BadRequest<string>, ForbidHttpResult>> AcknowledgeAsync(
        long id, [FromBody] NoteRequest? req, ClaimsPrincipal user, AppDbContext db, IAuditService audit, HttpContext http, CancellationToken ct)
    {
        var job = await db.Services.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (job is null) return TypedResults.NotFound();

        user.TryGetUserId(out var uid);
        if (ApplyAcknowledge(job, user, req?.Note, uid, db, audit, http.GetIp()).ToProblem() is { } problem)
            return problem;
        await db.SaveChangesAsync(ct);

        return TypedResults.Ok(await BuildDetailAsync(db, job, ServiceRoles.CanSeePricing(user), ct));
    }

    /// <summary>Idempotent against a job that has already moved to InService or beyond — a replayed
    /// start must not report failure just because the first attempt landed.</summary>
    internal static ApplyResult ApplyStart(ServiceJob job, ClaimsPrincipal user, string? note,
        long uid, AppDbContext db, IAuditService audit, string? ip)
    {
        if (!ServiceRoles.IsAssignedTechnician(user, job))
            return ApplyResult.Forbidden("Only the assigned technician can start this job.");
        if (job.ServiceStatus == ServiceStatus.InService || CompletedOrLater.Contains(job.ServiceStatus))
            return ApplyResult.Applied;
        if (job.ServiceStatus is not ServiceStatus.Acknowledged)
            return ApplyResult.Invalid($"Acknowledge the job before starting service (currently {job.ServiceStatus}).");

        WriteTransition(db, job, ServiceStatus.InService, uid, note ?? "Service started by technician");
        audit.Log(uid, "service.start", "service", job.Id, ip: ip);
        return ApplyResult.Applied;
    }

    private static async Task<Results<Ok<ServiceDetailDto>, NotFound, BadRequest<string>, ForbidHttpResult>> StartAsync(
        long id, [FromBody] NoteRequest? req, ClaimsPrincipal user, AppDbContext db, IAuditService audit, HttpContext http, CancellationToken ct)
    {
        var job = await db.Services.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (job is null) return TypedResults.NotFound();

        user.TryGetUserId(out var uid);
        if (ApplyStart(job, user, req?.Note, uid, db, audit, http.GetIp()).ToProblem() is { } problem)
            return problem;
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

    internal static ApplyResult ApplyDispatch(ServiceJob job, DispatchRequest req,
        long uid, AppDbContext db, IAuditService audit, string? ip)
    {
        // Idempotent: a job already dispatched is the state the caller asked for.
        if (job.ServiceStatus is ServiceStatus.Dispatched) return ApplyResult.Applied;
        if (job.ServiceStatus is not ServiceStatus.Completed)
            return ApplyResult.Invalid($"Only a completed job can be dispatched (currently {job.ServiceStatus}).");

        // Blank means "leave it alone", never "clear it". The reference is stamped by its own action and
        // the DC number by generating the DC document, so by the time anything is dispatched both are
        // usually already on the job — asking for them again at this point only risked overwriting a
        // generated DC number with an empty box, which is what this used to do.
        if (!string.IsNullOrWhiteSpace(req.ReferenceNo)) job.OutwardReferenceNo = req.ReferenceNo.Trim();
        if (!string.IsNullOrWhiteSpace(req.OutwardDcNo)) job.OutwardDcNo = req.OutwardDcNo.Trim();
        // Only stamp a dispatch date when the job has none: a DC generated last week dated the movement,
        // and re-dating it to now would misreport the turnaround.
        job.DcDate = ShopClock.BusinessDate(req.DcDate) ?? job.DcDate ?? ShopClock.Today;

        // A dispatched unit has to be traceable to a document. The dialog that used to demand a
        // reference number is gone, so the requirement is enforced here against what the job actually
        // carries — any one of the PI, the delivery challan or the outward reference will do, because
        // each of them is a number the goods can be followed by. Checked after the assignments above so
        // a request that supplies one of them satisfies it in the same call.
        if (string.IsNullOrWhiteSpace(job.PiNo)
            && string.IsNullOrWhiteSpace(job.OutwardDcNo)
            && string.IsNullOrWhiteSpace(job.OutwardReferenceNo))
            return ApplyResult.Invalid(
                "This job has no PI, delivery challan or outward reference — generate one, or set the "
                + "outward reference, before dispatching it.");

        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(job.OutwardReferenceNo)) parts.Add($"ref {job.OutwardReferenceNo}");
        if (!string.IsNullOrWhiteSpace(job.OutwardDcNo)) parts.Add($"DC {job.OutwardDcNo}");
        if (parts.Count == 0 && !string.IsNullOrWhiteSpace(job.PiNo)) parts.Add($"PI {job.PiNo}");
        var note = $"Dispatched ({string.Join(", ", parts)})";
        WriteTransition(db, job, ServiceStatus.Dispatched, uid, note);
        audit.Log(uid, "service.dispatch", "service", job.Id, details: note, ip: ip);
        return ApplyResult.Applied;
    }

    private static async Task<Results<Ok<ServiceDetailDto>, NotFound, BadRequest<string>, ForbidHttpResult>> DispatchAsync(
        long id, [FromBody] DispatchRequest req, ClaimsPrincipal user, AppDbContext db, IAuditService audit, HttpContext http, CancellationToken ct)
    {
        var job = await db.Services.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (job is null) return TypedResults.NotFound();

        user.TryGetUserId(out var uid);
        if (ApplyDispatch(job, req, uid, db, audit, http.GetIp()).ToProblem() is { } problem)
            return problem;
        await db.SaveChangesAsync(ct);

        return TypedResults.Ok(await BuildDetailAsync(db, job, ServiceRoles.CanSeePricing(user), ct));
    }

    internal static ApplyResult ApplyStock(ServiceJob job, string? note,
        long uid, AppDbContext db, IAuditService audit, string? ip)
    {
        if (job.ServiceStatus is ServiceStatus.Stocked) return ApplyResult.Applied;
        if (job.ServiceStatus is not ServiceStatus.Completed)
            return ApplyResult.Invalid($"Cannot move a {job.ServiceStatus} job to {ServiceStatus.Stocked}.");

        WriteTransition(db, job, ServiceStatus.Stocked, uid, note);
        audit.Log(uid, "service.stock", "service", job.Id, ip: ip);
        return ApplyResult.Applied;
    }

    private static async Task<Results<Ok<ServiceDetailDto>, NotFound, BadRequest<string>, ForbidHttpResult>> StockJobAsync(
        long id, [FromBody] NoteRequest? req, ClaimsPrincipal user, AppDbContext db, IAuditService audit, HttpContext http, CancellationToken ct)
    {
        var job = await db.Services.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (job is null) return TypedResults.NotFound();

        user.TryGetUserId(out var uid);
        if (ApplyStock(job, req?.Note, uid, db, audit, http.GetIp()).ToProblem() is { } problem)
            return problem;
        await db.SaveChangesAsync(ct);

        return TypedResults.Ok(await BuildDetailAsync(db, job, ServiceRoles.CanSeePricing(user), ct));
    }

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

    internal static ApplyResult ApplyPayment(ServiceJob job, PaymentStatus status,
        long uid, AppDbContext db, IAuditService audit, string? ip)
    {
        // Idempotent: already at the requested status means nothing to record — and re-recording it
        // would add a meaningless "Paid → Paid" line to the job's history on every replay.
        if (job.PaymentStatus == status) return ApplyResult.Applied;
        if (!CompletedOrLater.Contains(job.ServiceStatus))
            return ApplyResult.Invalid("Payment can only be set once the service is completed.");

        var was = job.PaymentStatus;
        job.PaymentStatus = status;
        WriteNote(db, job, "Payment", uid, $"Payment {was} → {status}");
        audit.Log(uid, "service.payment", "service", job.Id, details: status.ToString(), ip: ip);
        return ApplyResult.Applied;
    }

    private static async Task<Results<Ok<ServiceDetailDto>, NotFound, BadRequest<string>, ForbidHttpResult>> PaymentAsync(
        long id, [FromBody] PaymentRequest req, ClaimsPrincipal user, AppDbContext db, IAuditService audit, HttpContext http, CancellationToken ct)
    {
        if (!Enum.TryParse<PaymentStatus>(req.Status, true, out var ps))
            return TypedResults.BadRequest($"Unknown payment status '{req.Status}'.");
        var job = await db.Services.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (job is null) return TypedResults.NotFound();

        user.TryGetUserId(out var uid);
        if (ApplyPayment(job, ps, uid, db, audit, http.GetIp()).ToProblem() is { } problem)
            return problem;
        await db.SaveChangesAsync(ct);

        return TypedResults.Ok(await BuildDetailAsync(db, job, ServiceRoles.CanSeePricing(user), ct));
    }

    // ---------------------------------------------------------------- manual document / reference stamps

    // Set the courier / gate-pass reference WITHOUT dispatching. The legacy Pending-Dispatch and
    // Global-Search pages both had this: the reference often arrives after the job has already moved on.
    internal static ApplyResult ApplyOutwardReference(ServiceJob job, OutwardReferenceRequest req,
        long uid, AppDbContext db, IAuditService audit, string? ip)
    {
        var reference = req.ReferenceNo.Trim();
        var dc = string.IsNullOrWhiteSpace(req.OutwardDcNo) ? null : req.OutwardDcNo.Trim();
        // Idempotent: the job already carries exactly what was asked for, so there is nothing to stamp
        // and no reason to add another identical history line.
        if (job.OutwardReferenceNo == reference && (dc is null || job.OutwardDcNo == dc))
            return ApplyResult.Applied;

        job.OutwardReferenceNo = reference;
        var note = $"Outward reference set to {job.OutwardReferenceNo}";
        // Only overwrite the DC number when one was supplied — a blank field must not wipe a generated DC.
        if (dc is not null)
        {
            job.OutwardDcNo = dc;
            note += $", DC {job.OutwardDcNo}";
        }
        WriteNote(db, job, "OutwardRef", uid, note);
        audit.Log(uid, "service.outward-reference", "service", job.Id, details: job.OutwardReferenceNo, ip: ip);
        return ApplyResult.Applied;
    }

    private static async Task<Results<Ok<ServiceDetailDto>, NotFound, BadRequest<string>, ForbidHttpResult>> SetOutwardReferenceAsync(
        long id, [FromBody] OutwardReferenceRequest req, ClaimsPrincipal user, AppDbContext db,
        IAuditService audit, HttpContext http, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.ReferenceNo)) return TypedResults.BadRequest("Reference number is required.");
        var job = await db.Services.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (job is null) return TypedResults.NotFound();

        user.TryGetUserId(out var uid);
        if (ApplyOutwardReference(job, req, uid, db, audit, http.GetIp()).ToProblem() is { } problem)
            return problem;
        await db.SaveChangesAsync(ct);

        return TypedResults.Ok(await BuildDetailAsync(db, job, ServiceRoles.CanSeePricing(user), ct));
    }

    // Record an invoice raised outside the app (legacy "Set Invoice No"). Generating an invoice here
    // stamps the same field, so refuse to silently overwrite one that a generated document owns.
    internal static async Task<ApplyResult> ApplyInvoiceNoAsync(ServiceJob job, InvoiceNoRequest req,
        long uid, AppDbContext db, IAuditService audit, string? ip, CancellationToken ct)
    {
        var invNo = req.InvNo.Trim();
        if (job.InvNo == invNo) return ApplyResult.Applied;   // idempotent
        if (!CompletedOrLater.Contains(job.ServiceStatus))
            return ApplyResult.Invalid($"An invoice number can only be recorded once the service is completed (currently {job.ServiceStatus}).");
        // Explicit join rather than the Document nav — the line's relationship is convention-mapped only.
        var generatedInvoice = await (from l in db.ServiceDocumentLines
                                      join d in db.ServiceDocuments on l.DocumentId equals d.Id
                                      where l.ServiceJobId == job.Id && d.DocType == DocumentType.Invoice
                                      select l.Id).AnyAsync(ct);
        if (generatedInvoice) return ApplyResult.Invalid("This job is already covered by a generated invoice.");

        var was = job.InvNo;
        job.InvNo = invNo;
        job.InvDate = ShopClock.BusinessDate(req.InvDate) ?? ShopClock.Today;
        WriteNote(db, job, "InvoiceNo", uid,
            was is null ? $"Invoice number set to {job.InvNo}" : $"Invoice number {was} → {job.InvNo}");
        audit.Log(uid, "service.invoice-no", "service", job.Id, details: job.InvNo, ip: ip);
        return ApplyResult.Applied;
    }

    private static async Task<Results<Ok<ServiceDetailDto>, NotFound, BadRequest<string>, ForbidHttpResult>> SetInvoiceNoAsync(
        long id, [FromBody] InvoiceNoRequest req, ClaimsPrincipal user, AppDbContext db,
        IAuditService audit, HttpContext http, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.InvNo)) return TypedResults.BadRequest("Invoice number is required.");
        var job = await db.Services.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (job is null) return TypedResults.NotFound();

        user.TryGetUserId(out var uid);
        var result = await ApplyInvoiceNoAsync(job, req, uid, db, audit, http.GetIp(), ct);
        if (result.ToProblem() is { } problem) return problem;
        await db.SaveChangesAsync(ct);

        return TypedResults.Ok(await BuildDetailAsync(db, job, ServiceRoles.CanSeePricing(user), ct));
    }

    internal static async Task<ApplyResult> ApplyDeleteAsync(ServiceJob job,
        long uid, AppDbContext db, IAuditService audit, string? ip, CancellationToken ct)
    {
        if (job.IsDeleted) return ApplyResult.Applied;   // idempotent
        // A billed job must not vanish from underneath its paperwork — the document still references it,
        // and every list filters IsDeleted out, so the invoice would lose its line.
        if (await db.ServiceDocumentLines.AnyAsync(l => l.ServiceJobId == job.Id, ct))
            return ApplyResult.Invalid("Cannot delete — a PI, invoice or delivery challan has already been generated for this job.");
        if (!string.IsNullOrWhiteSpace(job.PiNo) || !string.IsNullOrWhiteSpace(job.InvNo)
            || !string.IsNullOrWhiteSpace(job.OutwardDcNo))
            return ApplyResult.Invalid("Cannot delete — this job already carries a PI, invoice or DC number.");

        job.IsDeleted = true;
        WriteNote(db, job, "Deleted", uid, "Job deleted (hidden from all lists)");
        audit.Log(uid, "service.delete", "service", job.Id, details: job.ServiceNo, ip: ip);
        return ApplyResult.Applied;
    }

    private static async Task<Results<NoContent, NotFound, BadRequest<string>>> SoftDeleteAsync(
        long id, ClaimsPrincipal user, AppDbContext db, IAuditService audit, HttpContext http, CancellationToken ct)
    {
        var job = await db.Services.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (job is null) return TypedResults.NotFound();

        user.TryGetUserId(out var uid);
        var result = await ApplyDeleteAsync(job, uid, db, audit, http.GetIp(), ct);
        if (result.Status != ApplyStatus.Applied) return TypedResults.BadRequest(result.Error!);
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
