using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PSR.Service.Api.Audit;
using PSR.Service.Api.Auth;
using PSR.Service.Api.Data;
using PSR.Service.Api.Data.Entities;
using PSR.Service.Api.Documents;

namespace PSR.Service.Api.Services;

public static partial class ServicesEndpoints
{
    // ---------------------------------------------------------------- component pricing

    /// <summary>Turn the job's "add component prices" setting on or off — whether a PI / invoice bills the
    /// parts fitted, or only the service charges. A billing decision, so it sits with the people who raise
    /// the documents (DocumentManage), and it can be set at any stage up to the PI: the office often knows
    /// before the bench is done whether a customer pays for parts.
    ///
    /// Refused once a PI or an invoice has priced the job, because the invoice has to bill on the basis the
    /// PI quoted — <see cref="BillingService.ComponentPricingFixedBy"/>. Refused on a retained unit too: it is
    /// the shop's own and is never billed, so the setting would mean nothing.
    ///
    /// The lines themselves are untouched. A component left off the bill is still priced on its line, so
    /// the job still shows what was fitted and what it was worth.</summary>
    private static async Task<Results<Ok<ServiceDetailDto>, NotFound, BadRequest<string>>> SetComponentPricingAsync(
        long id, [FromBody] ComponentPricingRequest req, ClaimsPrincipal user, AppDbContext db,
        IAuditService audit, HttpContext http, CancellationToken ct)
    {
        var job = await db.Services.FirstOrDefaultAsync(s => s.Id == id && !s.IsDeleted, ct);
        if (job is null) return TypedResults.NotFound();

        var pricing = ServiceRoles.CanSeePricing(user);
        // Asked for what it already is: nothing to record, and not worth an error either — two people
        // ticking the same box at once both get what they asked for.
        if (job.IncludeComponentPrices == req.IncludeComponentPrices)
            return TypedResults.Ok(await BuildDetailAsync(db, job, pricing, ct));

        if (job.JobKind == JobKind.SwapRetained)
            return TypedResults.BadRequest(
                $"{job.ServiceNo} carries the service centre's own unit and is never billed.");
        if (BillingService.ComponentPricingFixedBy(job) is { } fixedBy)
            return TypedResults.BadRequest(
                $"{job.ServiceNo}: {fixedBy} has already priced this job "
                + $"{(job.IncludeComponentPrices ? "with" : "without")} the component prices, and the invoice "
                + "has to bill on the same basis.");

        user.TryGetUserId(out var uid);
        job.IncludeComponentPrices = req.IncludeComponentPrices;
        var label = req.IncludeComponentPrices ? "component prices billed" : "component prices not billed";
        // A note, not a transition — the workflow status does not move. WriteNote bumps RowVersion.
        WriteNote(db, job, "Billing", uid, $"Set to {label} on PI / invoice");
        audit.Log(uid, "service.component-pricing", "service", job.Id, details: label, ip: http.GetIp());
        await db.SaveChangesAsync(ct);

        return TypedResults.Ok(await BuildDetailAsync(db, job, pricing, ct));
    }
}
