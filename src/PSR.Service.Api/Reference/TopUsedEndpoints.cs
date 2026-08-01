using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using PSR.Service.Api.Auth;
using PSR.Service.Api.Data;
using PSR.Service.Api.Data.Entities;

namespace PSR.Service.Api.Reference;

/// <summary>"What do I reach for most?" — the caller's own most-frequently-added components and
/// service charges, so the add-line picker can float them to the top instead of making a technician
/// hunt the same handful of items out of the full catalogue on every job.
///
/// Always self-scoped: this is a personal habit list, never another user's, so unlike the reports
/// endpoints there is no technicianId filter to widen it.
/// </summary>
public static class TopUsedEndpoints
{
    private const int DefaultLimit = 10;
    private const int MaxLimit = 50;

    public static IEndpointRouteBuilder MapTopUsedEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/reference/my-top-used", MyTopUsedAsync)
            .WithTags("reference")
            .RequireAuthorization();

        return app;
    }

    private static async Task<Results<Ok<TopUsedResponse>, UnauthorizedHttpResult>> MyTopUsedAsync(
        AppDbContext db, ClaimsPrincipal user, int? limit, CancellationToken ct)
    {
        if (!user.TryGetUserId(out var uid)) return TypedResults.Unauthorized();
        var take = limit is null or < 1 ? DefaultLimit : Math.Min(limit.Value, MaxLimit);

        return TypedResults.Ok(new TopUsedResponse(
            await TopPartsQuery(db, uid).Take(take).ToListAsync(ct),
            await TopChargesQuery(db, uid).Take(take).ToListAsync(ct)));
    }

    // The two ranking queries are exposed (rather than inlined above) so the translation tests can
    // call ToQueryString on them — a GroupBy that silently fails to translate is the one way this
    // endpoint breaks, and it would only surface at runtime.
    //
    // Both rank by how OFTEN the item was picked (line count), not by quantity: a part fitted once in
    // bulk is not a habit, whereas one reached for on twenty jobs is. Ties break on most-recent use.
    //
    // Grouped in SQL, unlike the reports endpoints which project narrow rows and group in memory. The
    // difference is intent: those slice a bounded date range, this walks a technician's whole unbounded
    // history to keep only the top few rows, so materializing every line first would be waste. Both
    // aggregates are plain key + COUNT/MAX, well within what the provider translates.
    //
    // Joining Parts/ServiceCharges into the grouping (rather than resolving names afterwards) also keeps
    // deactivated reference rows from occupying a slot in the top N.

    public static IQueryable<TopUsedPartRow> TopPartsQuery(AppDbContext db, long technicianId) =>
        from l in db.ServiceLines.AsNoTracking()
        join s in db.Services on l.ServiceId equals s.Id
        join p in db.Parts on l.PartId equals (long?)p.Id
        where s.TechnicianId == technicianId && !s.IsDeleted && p.IsActive
              && l.LineType == ServiceLineType.Component
        group l by new { p.Id, p.ItemCode, p.Name } into g
        orderby g.Count() descending, g.Max(x => x.CreatedAt) descending
        select new TopUsedPartRow(g.Key.Id, g.Key.ItemCode, g.Key.Name, g.Count(), g.Max(x => x.CreatedAt));

    public static IQueryable<TopUsedChargeRow> TopChargesQuery(AppDbContext db, long technicianId) =>
        from l in db.ServiceLines.AsNoTracking()
        join s in db.Services on l.ServiceId equals s.Id
        join c in db.ServiceCharges on l.ServiceChargeId equals (long?)c.Id
        where s.TechnicianId == technicianId && !s.IsDeleted && c.IsActive
              && l.LineType == ServiceLineType.ServiceCharge
        group l by new { c.Id, c.Name } into g
        orderby g.Count() descending, g.Max(x => x.CreatedAt) descending
        select new TopUsedChargeRow(g.Key.Id, g.Key.Name, g.Count(), g.Max(x => x.CreatedAt));
}
