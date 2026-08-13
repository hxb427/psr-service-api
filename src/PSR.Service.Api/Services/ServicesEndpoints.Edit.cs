using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PSR.Service.Api.Audit;
using PSR.Service.Api.Auth;
using PSR.Service.Api.Data;
using PSR.Service.Api.Data.Entities;
using PSR.Service.Api.Settings;

namespace PSR.Service.Api.Services;

/// <summary>The legacy Global Search "Edit Service Record": correcting what a booked job SAYS, not
/// where it is. Nothing here changes status, technician, payment or lines — those all have their own
/// audited transitions, and routing them through a free-form edit would let the workflow be bypassed.
///
/// Two gates, both required: the caller's role (ServiceRecordEdit) and the admin switch. The switch
/// exists because this rewrites history-of-record for a machine — useful for fixing a mis-typed serial
/// at inward, dangerous as an everyday habit.</summary>
public static partial class ServicesEndpoints
{
    private static async Task<Results<Ok<ServiceDetailDto>, NotFound, BadRequest<string>>> UpdateRecordAsync(
        long id, [FromBody] UpdateServiceRecordRequest req, ClaimsPrincipal user, AppDbContext db,
        AppSettingsService settings, IAuditService audit, HttpContext http, CancellationToken ct)
    {
        if (!await settings.ServiceRecordEditEnabledAsync(ct))
            return TypedResults.BadRequest(
                "Editing service records is switched off. An admin can enable it in Settings → Permissions.");

        var job = await db.Services.FirstOrDefaultAsync(s => s.Id == id && !s.IsDeleted, ct);
        if (job is null) return TypedResults.NotFound();

        var serial = req.SerialNo?.Trim();
        if (string.IsNullOrWhiteSpace(serial)) return TypedResults.BadRequest("Serial number is required.");

        if (!Enum.TryParse<WarrantyStatus>(req.WarrantyStatus, true, out var warranty))
            return TypedResults.BadRequest(
                $"'{req.WarrantyStatus}' is not a warranty status. Use Unknown, InWarranty or OutOfWarranty.");

        user.TryGetUserId(out var uid);
        var diff = new AuditDiff();

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
                    job.CustomerId = await ResolveCustomerAsync(db, null, customerName,
                        org: null, phone: null, email: null, address: null, ct,
                        audit, uid, http.GetIp(), origin: "record edit");
                    diff.Note($"customer '{current}' → '{customerName}'");
                }
            }
        }

        diff.Set("serial", job.SerialNo, serial, v => job.SerialNo = v ?? job.SerialNo);
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

        WriteNote(db, job, "Edited", uid, diff.Summary);
        audit.Log(uid, "service.record-edit", "service", job.Id,
            details: diff.Describe(job.ServiceNo), ip: http.GetIp());
        await db.SaveChangesAsync(ct);

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
