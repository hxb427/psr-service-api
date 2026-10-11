using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using PSR.Service.Api.Auth;
using PSR.Service.Api.Data;

namespace PSR.Service.Api.Reference;

/// <summary>"What have I written here before?" — the caller's own most-used complaint and cause
/// wording, so the field form can offer it instead of asking a technician to retype
/// "Sensor board corroded — coastal unit" on every visit.
///
/// Built on the same shape as <see cref="TopUsedEndpoints"/>, and deliberately NOT folded into it. That
/// one ranks reference ROWS — a part id, a service charge id — and its results are picked to be sent
/// back as ids. These are free text: there is no catalogue of causes, the ranking key is the phrase
/// itself, and what comes back is a suggestion to put in a text field rather than a thing to select.
/// Sharing the endpoint would mean one response whose halves mean two different things.
///
/// Always self-scoped. A technician's phrasing is their own habit, and offering one technician the
/// sentences another writes would quietly spread a diagnosis nobody made.
///
/// Matched case-insensitively? No — grouped exactly as typed. Two spellings of the same cause rank
/// separately, which looks like a flaw and is the better behaviour here: the suggestion is handed
/// straight into a text field that ends up on a customer's receipt, and the version the technician
/// actually writes is the version they should be offered back.</summary>
public static class FieldPhrasesEndpoints
{
    private const int DefaultLimit = 10;
    private const int MaxLimit = 50;

    public static IEndpointRouteBuilder MapFieldPhraseEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/reference/my-top-phrases", MyTopPhrasesAsync)
            .WithTags("reference")
            .RequireAuthorization();

        return app;
    }

    private static async Task<Results<Ok<TopPhrasesResponse>, UnauthorizedHttpResult>> MyTopPhrasesAsync(
        AppDbContext db, ClaimsPrincipal user, int? limit, CancellationToken ct)
    {
        if (!user.TryGetUserId(out var uid)) return TypedResults.Unauthorized();
        var take = limit is null or < 1 ? DefaultLimit : Math.Min(limit.Value, MaxLimit);

        return TypedResults.Ok(new TopPhrasesResponse(
            await TopComplaintsQuery(db, uid).Take(take).ToListAsync(ct),
            await TopCausesQuery(db, uid).Take(take).ToListAsync(ct)));
    }

    // Exposed (rather than inlined above) so the translation tests can call ToQueryString on them — a
    // GroupBy the provider cannot translate is the one way this endpoint breaks, and it would surface
    // only when a technician opened the form against the real database.
    //
    // Ranked by how OFTEN the phrase was used, ties broken on most recent, exactly as the top-used
    // part ranking is. Blank and whitespace-only text is excluded: an empty complaint is the absence
    // of a complaint, not a popular way of describing one.

    public static IQueryable<TopPhraseRow> TopComplaintsQuery(AppDbContext db, long technicianId) =>
        from f in db.FieldServices.AsNoTracking()
        where f.TechnicianId == technicianId && f.Complaint != null && f.Complaint != ""
        group f by f.Complaint! into g
        orderby g.Count() descending, g.Max(x => x.CreatedAt) descending
        select new TopPhraseRow(g.Key, g.Count(), g.Max(x => x.CreatedAt));

    public static IQueryable<TopPhraseRow> TopCausesQuery(AppDbContext db, long technicianId) =>
        from f in db.FieldServices.AsNoTracking()
        where f.TechnicianId == technicianId && f.Cause != null && f.Cause != ""
        group f by f.Cause! into g
        orderby g.Count() descending, g.Max(x => x.CreatedAt) descending
        select new TopPhraseRow(g.Key, g.Count(), g.Max(x => x.CreatedAt));
}
