using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using PSR.Service.Api.Data;

namespace PSR.Service.Api.Reference;

/// <summary>States and districts, for the pickers on the field entry forms.
///
/// Read-only and open to any signed-in caller: this is the geography of India, not the shop's data, and
/// there is nothing here to protect or to edit. New rows arrive by migration, the way the seed did.
///
/// Districts are asked for per state rather than all at once. There are a few hundred of them in a
/// fully seeded table and a technician only ever needs the one state's worth, which matters on a phone
/// on a village connection.</summary>
public static class GeographyEndpoints
{
    public static IEndpointRouteBuilder MapGeographyEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/reference").WithTags("reference").RequireAuthorization();

        group.MapGet("/states", StatesAsync);
        group.MapGet("/districts", DistrictsAsync);

        return app;
    }

    private static async Task<Ok<List<StateDto>>> StatesAsync(AppDbContext db, CancellationToken ct) =>
        TypedResults.Ok(await db.IndianStates.AsNoTracking()
            .OrderBy(x => x.SortOrder).ThenBy(x => x.Name)
            .Select(x => new StateDto(x.Id, x.Name))
            .ToListAsync(ct));

    /// <summary>The districts of one state. Without <paramref name="stateId"/> this returns nothing
    /// rather than everything: a picker that opens on 700 districts from 36 states is not a picker, and
    /// a caller that wants the lot can ask state by state.</summary>
    private static async Task<Ok<List<DistrictDto>>> DistrictsAsync(
        AppDbContext db, int? stateId, CancellationToken ct)
    {
        if (stateId is not { } sid) return TypedResults.Ok(new List<DistrictDto>());
        return TypedResults.Ok(await db.Districts.AsNoTracking()
            .Where(x => x.StateId == sid)
            .OrderBy(x => x.Name)
            .Select(x => new DistrictDto(x.Id, x.StateId, x.Name))
            .ToListAsync(ct));
    }
}
