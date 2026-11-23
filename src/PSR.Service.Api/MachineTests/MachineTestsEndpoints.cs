using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using PSR.Service.Api.Data;

namespace PSR.Service.Api.MachineTests;

/// <summary>Machine factory-test lookups (legacy passtestdata), proxied from Hostinger. JWT-protected;
/// clients call these, never Hostinger directly. Used by inward autofill + warranty check + field
/// service entry. When passtestdata migrates to RDS this contract stays; only the client swaps.
///
/// Warranty is InvDate + the dealer's term. The dealer is found by matching the record's Customer to
/// the dealer master (<see cref="DealerWarrantyMatcher"/>); when that finds none or several, the
/// verdict is UNKNOWN and the client asks the operator to pick one, then calls /warranty with it.</summary>
public static class MachineTestsEndpoints
{
    public static IEndpointRouteBuilder MapMachineTestEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/machine-tests").WithTags("machine-tests").RequireAuthorization();

        group.MapGet("/by-serial/{serial}", BySerialAsync);
        group.MapGet("/matches/{serial}", MatchesBySerialAsync);
        group.MapGet("/customers", CustomersAsync);
        group.MapGet("/search", SearchAsync);
        group.MapGet("/warranty", WarrantyAsync);

        return app;
    }

    /// <summary>Partial search over passtestdata for the SN Info page: matches any serial column, the
    /// customer or the model, and returns the whole row for each hit so the page can show every field
    /// without a second round trip. 503 when the passtest source is unreachable.</summary>
    private static async Task<Results<Ok<MachineSearchDto>, BadRequest<string>, StatusCodeHttpResult>> SearchAsync(
        string? q, int? limit, AppDbContext db, PasstestRepository passtest, CancellationToken ct)
    {
        if (!passtest.Configured) return TypedResults.StatusCode(StatusCodes.Status503ServiceUnavailable);

        var term = q?.Trim() ?? "";
        // Two characters would match a large slice of the table and the page would be unusable.
        if (term.Length < 3) return TypedResults.BadRequest("Enter at least 3 characters to search.");

        var take = Math.Clamp(limit ?? 50, 1, 200);
        // One extra row is fetched purely to know whether the cap hid anything.
        var rows = await passtest.SearchAsync(term, take + 1, ct);
        if (rows is null) return TypedResults.StatusCode(StatusCodes.Status503ServiceUnavailable);

        var dealers = await DealerWarrantyMatcher.LoadAsync(db, null, ct);
        var truncated = rows.Count > take;
        var records = rows.Take(take).Select(r => MachineRecordMapper.Map(r, term, dealers)).ToList();
        return TypedResults.Ok(new MachineSearchDto(records, take, truncated));
    }

    /// <summary>Resolve a unit by any of its serials. <paramref name="dealerId"/> is a dealer the
    /// operator picked; it overrides the match on the record's customer. 404 when not found; 503 when
    /// passtest is unreachable.</summary>
    private static async Task<Results<Ok<MachineTestDto>, NotFound, StatusCodeHttpResult>> BySerialAsync(
        string serial, long? dealerId, AppDbContext db, PasstestRepository passtest, CancellationToken ct)
    {
        if (!passtest.Configured) return TypedResults.StatusCode(StatusCodes.Status503ServiceUnavailable);

        var dealers = await DealerWarrantyMatcher.LoadAsync(db, dealerId, ct);
        var dto = await passtest.FindBySerialAsync(serial, dealers, ct);
        return dto is null ? TypedResults.NotFound() : TypedResults.Ok(dto);
    }

    /// <summary>Every factory record carrying this serial, newest invoice first — the set the inward
    /// form picks from when a serial resolves to more than one machine. Same warranty resolution as
    /// the single-record route, so a match chosen here reads exactly as it would there. An empty list
    /// (not a 404) when nothing matched: the caller is filling a form and has a "no record" message of
    /// its own. 503 when passtest is unreachable.</summary>
    private static async Task<Results<Ok<MachineTestMatchesDto>, StatusCodeHttpResult>> MatchesBySerialAsync(
        string serial, long? dealerId, int? limit, AppDbContext db, PasstestRepository passtest,
        CancellationToken ct)
    {
        if (!passtest.Configured) return TypedResults.StatusCode(StatusCodes.Status503ServiceUnavailable);

        // Capped below the repository's own limit of 50, so the extra row fetched to detect
        // truncation is never the one the clamp swallows.
        var take = Math.Clamp(limit ?? 20, 1, 40);
        var dealers = await DealerWarrantyMatcher.LoadAsync(db, dealerId, ct);
        // One extra row is fetched purely to know whether the cap hid anything.
        var matches = await passtest.FindMatchesBySerialAsync(serial, dealers, take + 1, ct);

        var truncated = matches.Count > take;
        return TypedResults.Ok(new MachineTestMatchesDto(matches.Take(take).ToList(), truncated));
    }

    /// <summary>The verdict for a purchase date under a dealer the operator picked — what a lookup that
    /// could not match the record's customer is settled with. Takes the invoice date the client already
    /// holds rather than re-reading the factory record, so the answer is for the exact row on screen
    /// (a serial can carry several). No passtest round trip, so it works while Hostinger is down.
    /// 404 when the dealer does not exist.</summary>
    private static async Task<Results<Ok<WarrantyVerdictDto>, NotFound>> WarrantyAsync(
        long dealerId, DateTime? invoiceDate, AppDbContext db, CancellationToken ct)
    {
        var dealer = await db.Dealers.AsNoTracking()
            .Where(d => d.Id == dealerId)
            .Select(d => new { d.Id, d.Name, d.WarrantyMonths })
            .FirstOrDefaultAsync(ct);
        if (dealer is null) return TypedResults.NotFound();

        int? months = dealer.WarrantyMonths > 0 ? dealer.WarrantyMonths : null;
        var (status, expiry) = WarrantyVerdict.Compute(invoiceDate?.Date, months);
        return TypedResults.Ok(new WarrantyVerdictDto(status, expiry, months, dealer.Id, dealer.Name));
    }

    private static async Task<Ok<MachineCustomersDto>> CustomersAsync(
        PasstestRepository passtest, CancellationToken ct)
        => TypedResults.Ok(new MachineCustomersDto(await passtest.CustomersAsync(ct)));
}
