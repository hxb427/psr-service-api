using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PSR.Service.Api.Audit;
using PSR.Service.Api.Auth;
using PSR.Service.Api.Data;
using PSR.Service.Api.Data.Entities;
using PSR.Service.Api.Settings;
using PSR.Service.Api.Stock;

namespace PSR.Service.Api.Services;

/// <summary>The legacy Global Search "Edit Service Record": correcting what a booked job SAYS, not
/// where it is. Nothing here changes status, technician, payment or lines — those all have their own
/// audited transitions, and routing them through a free-form edit would let the workflow be bypassed.
///
/// Two gates, both required: the caller's role (ServiceRecordEdit) and the admin switch. The switch
/// exists because this rewrites history-of-record for a machine — useful for fixing a mis-typed serial
/// at inward, dangerous as an everyday habit.
///
/// THE SERIAL AND THE PS CODE ARE NOT DESCRIPTIVE, and that is the one thing this file has to get
/// right. Together they are what identifies the unit in <c>component_serials</c>, so correcting them
/// on the job alone left two records naming one machine differently: global search showed the fixed
/// serial, the SN screen showed the typo, and stocking the finished job put the typo on the shelf
/// where an issue would offer a serial printed on nothing. Fixing a mis-typed serial is the reason
/// this route exists, so the ledger moves with it or the edit is refused — never half-applied.</summary>
public static partial class ServicesEndpoints
{
    private static async Task<Results<Ok<ServiceDetailDto>, NotFound, BadRequest<string>>> UpdateRecordAsync(
        long id, [FromBody] UpdateServiceRecordRequest req, ClaimsPrincipal user, AppDbContext db,
        AppSettingsService settings, SerialService serial, IAuditService audit, HttpContext http,
        CancellationToken ct)
    {
        if (!await settings.ServiceRecordEditEnabledAsync(ct))
            return TypedResults.BadRequest(
                "Editing service records is switched off. An admin can enable it in Settings → Permissions.");

        var job = await db.Services.FirstOrDefaultAsync(s => s.Id == id && !s.IsDeleted, ct);
        if (job is null) return TypedResults.NotFound();

        var serialNo = req.SerialNo?.Trim();
        if (string.IsNullOrWhiteSpace(serialNo)) return TypedResults.BadRequest("Serial number is required.");

        if (!Enum.TryParse<WarrantyStatus>(req.WarrantyStatus, true, out var warranty))
            return TypedResults.BadRequest(
                $"'{req.WarrantyStatus}' is not a warranty status. Use Unknown, InWarranty or OutOfWarranty.");

        // Whether this edit moves what IDENTIFIES the unit, and the part the corrected PS code names.
        // Resolved before anything is written, because a correction the ledger cannot follow must not
        // land on the job either — a job and a unit disagreeing about one machine is the state this
        // whole route is supposed to fix.
        var newPsCode = Trimmed(req.PsCode);
        // Compared the way AuditDiff compares, so this cannot decide the identity moved while the diff
        // below decides nothing changed — a blank PS code and a null one are the same code.
        var identityMoved = !string.Equals(Trimmed(job.SerialNo), serialNo, StringComparison.Ordinal)
                         || !string.Equals(Trimmed(job.PsCode), newPsCode, StringComparison.Ordinal);
        Part? unitPart = null;
        if (job.SourceComponentSerialId is not null && identityMoved)
        {
            unitPart = await ResolveStockPartAsync(db, newPsCode, ct);
            // The corrected code has to name an item the shop follows unit by unit, because a unit is
            // already registered against this job. Pointing it at an unknown code, or at one whose
            // units are not identified individually, would leave that record stranded under a part it
            // no longer belongs to — so the answer is the code, not a quiet orphan.
            if (unitPart is null)
                return TypedResults.BadRequest(string.IsNullOrWhiteSpace(newPsCode)
                    ? $"{job.ServiceNo} has a unit in the serial ledger, so it cannot be left without "
                      + "a PS code. Set the PS code of the item this machine is."
                    : $"No catalogue item matches PS code {newPsCode}, and {job.ServiceNo} has a unit "
                      + "in the serial ledger. Correct the code, or add the item to the catalogue first.");
            if (!unitPart.IsSerialTracked)
                return TypedResults.BadRequest(
                    $"{unitPart.ItemCode} — {unitPart.Name} is not serial-tracked, but {job.ServiceNo} "
                    + "already has a unit in the serial ledger. Turn on serial tracking for the item, "
                    + "or resolve the unit from the SN management screen before re-coding the job.");
        }

        user.TryGetUserId(out var uid);
        var diff = new AuditDiff();
        // ResolveCustomerAsync below saves a newly-created customer on its own, and the ledger
        // correction at the end can still refuse. Without a transaction that refusal would leave the
        // new customer behind and the rest abandoned.
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // The party is normalized here, unlike the legacy flat CUSTOMERNAME column. A dealer job's
        // party is the dealer record, so renaming it from this dialog would either rename the dealer
        // for every job it owns or silently detach this one — neither is a correction.
        var customerName = Trimmed(req.CustomerName);
        if (customerName is not null || job.CustomerId is not null)
        {
            if (job.DealerId is not null)
            {
                if (customerName is not null && !await DealerNameMatchesAsync(db, job.DealerId.Value, customerName, ct))
                    return TypedResults.BadRequest(
                        "This job is booked to a dealer. Change the dealer on the record itself rather than here.");
            }
            else
            {
                var current = await CustomerNameAsync(db, job.CustomerId, ct);
                if (!string.Equals(current, customerName, StringComparison.Ordinal))
                {
                    // Same resolve-or-create the inward desk uses, so a corrected name lands on the
                    // existing customer when there is one instead of forking a near-duplicate. Passing
                    // the auditor keeps an implicitly created customer from appearing from nowhere.
                    var party = await ResolveCustomerAsync(db, null, customerName,
                        org: null, phone: null, email: null, address: null, ct,
                        audit, uid, http.GetIp(), origin: "record edit");
                    // Retyping a job's customer as a dealer's name would fork the same firm in two, the
                    // same way the inward desk could. Refused here for the same reason.
                    if (party.Error is { } partyError) return TypedResults.BadRequest(partyError);
                    job.CustomerId = party.CustomerId;
                    diff.Note($"customer '{current}' → '{customerName}'");
                }
            }
        }

        diff.Set("serial", job.SerialNo, serialNo, v => job.SerialNo = v ?? job.SerialNo);
        diff.Set("inward DC", job.InwardDcNo, req.InwardDcNo, v => job.InwardDcNo = v);
        diff.Set("PS code", job.PsCode, req.PsCode, v => job.PsCode = v);
        diff.Set("model", job.ModelName, req.ModelName, v => job.ModelName = v);
        diff.Set("description", job.Description, req.Description, v => job.Description = v);
        diff.Set("reported problem", job.ReportedProblem, req.ReportedProblem, v => job.ReportedProblem = v);
        diff.Set("PI no", job.PiNo, req.PiNo, v => job.PiNo = v);
        diff.Set("outward DC", job.OutwardDcNo, req.OutwardDcNo, v => job.OutwardDcNo = v);
        diff.Set("invoice no", job.InvNo, req.InvNo, v => job.InvNo = v);
        diff.Set("warranty", job.WarrantyStatus, warranty, v => job.WarrantyStatus = v);

        // Saving an unchanged form is not an edit. Writing a history line and an audit row for it
        // would bury the real corrections under a pile of no-ops.
        if (!diff.HasChanges)
            return TypedResults.Ok(await BuildDetailAsync(db, job, ServiceRoles.CanSeePricing(user), ct));

        // The ledger follows the job. Refused rather than half-applied: if the unit cannot be moved to
        // the corrected identity, neither is the job, and the message says which of the two the desk
        // has to sort out. unitPart is non-null exactly when this ran the identity checks above.
        if (job.SourceComponentSerialId is { } unitId && unitPart is not null)
        {
            var problem = await serial.CorrectIdentityAsync(unitId, unitPart.Id, serialNo, unitPart.Name,
                uid, $"Identity corrected on job {job.ServiceNo}", ct);
            if (problem is not null)
            {
                await tx.RollbackAsync(ct);
                return TypedResults.BadRequest(problem);
            }
        }

        WriteNote(db, job, "Edited", uid, diff.Summary);
        audit.Log(uid, "service.record-edit", "service", job.Id,
            details: diff.Describe(job.ServiceNo), ip: http.GetIp());
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return TypedResults.Ok(await BuildDetailAsync(db, job, ServiceRoles.CanSeePricing(user), ct));
    }

    private static string? Trimmed(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();

    private static async Task<string?> CustomerNameAsync(AppDbContext db, long? customerId, CancellationToken ct)
        => customerId is null
            ? null
            : await db.Customers.AsNoTracking().Where(c => c.Id == customerId).Select(c => c.Name).FirstOrDefaultAsync(ct);

    private static async Task<bool> DealerNameMatchesAsync(AppDbContext db, long dealerId, string name, CancellationToken ct)
    {
        var dealer = await db.Dealers.AsNoTracking().Where(d => d.Id == dealerId).Select(d => d.Name).FirstOrDefaultAsync(ct);
        return string.Equals(dealer, name, StringComparison.Ordinal);
    }
}
