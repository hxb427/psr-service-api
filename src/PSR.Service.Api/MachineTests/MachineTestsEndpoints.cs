using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using PSR.Service.Api.Data;
using PSR.Service.Api.Settings;

namespace PSR.Service.Api.MachineTests;

/// <summary>Machine factory-test lookups (legacy passtestdata), proxied from Hostinger. JWT-protected;
/// clients call these, never Hostinger directly. Used by inward autofill + warranty check + field
/// service entry. When passtestdata migrates to RDS this contract stays; only the client swaps.</summary>
public static class MachineTestsEndpoints
{
    public static IEndpointRouteBuilder MapMachineTestEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/machine-tests").WithTags("machine-tests").RequireAuthorization();

        group.MapGet("/by-serial/{serial}", BySerialAsync);
        group.MapGet("/customers", CustomersAsync);

        return app;
    }

    /// <summary>Resolve a unit by any of its serials. Warranty is InvDate + warranty months, so the
    /// verdict is only as good as the months figure — see ResolveWarrantyMonthsAsync. 404 when not
    /// found; 503 when the passtest source is unreachable.</summary>
    private static async Task<Results<Ok<MachineTestDto>, NotFound, StatusCodeHttpResult>> BySerialAsync(
        string serial, long? dealerId, AppDbContext db, PasstestRepository passtest,
        AppSettingsService settings, CancellationToken ct)
    {
        if (!passtest.Configured) return TypedResults.StatusCode(StatusCodes.Status503ServiceUnavailable);

        var months = await ResolveWarrantyMonthsAsync(serial, dealerId, db, settings, ct);
        var dto = await passtest.FindBySerialAsync(serial, months, ct);
        return dto is null ? TypedResults.NotFound() : TypedResults.Ok(dto);
    }

    /// <summary>Warranty months, best source first.
    ///
    /// Callers that know the dealer (the inward form in dealer mode) pass it. The ones that cannot —
    /// the dashboard's quick check, the global-search serial filter, and a direct-customer inward —
    /// used to pass nothing, and a null months figure makes PasstestRepository return UNKNOWN every
    /// time. So fall back: a serial that has been through here before carries its dealer on the
    /// service job, and failing that the configured house default answers for the rest.
    ///
    /// 0 is "not known" throughout (Dealer.WarrantyMonths defaults to it), so a zero at any step
    /// falls through to the next rather than being taken as an answer. All three failing returns
    /// null, which is the original UNKNOWN behaviour.</summary>
    private static async Task<int?> ResolveWarrantyMonthsAsync(
        string serial, long? dealerId, AppDbContext db, AppSettingsService settings, CancellationToken ct)
    {
        if (dealerId is { } did)
        {
            var m = await db.Dealers.AsNoTracking()
                .Where(d => d.Id == did).Select(d => d.WarrantyMonths).FirstOrDefaultAsync(ct);
            if (m > 0) return m;
        }

        var jobMonths = await DealerMonthsForSerialQuery(db, serial).FirstOrDefaultAsync(ct);
        if (jobMonths > 0) return jobMonths;

        var fallback = await settings.DefaultWarrantyMonthsAsync(ct);
        return fallback > 0 ? fallback : null;
    }

    /// <summary>Warranty months of the dealer on the most recent live job for a serial.
    ///
    /// The ordering is applied to the joined rows rather than to the jobs before the join: ordering a
    /// source and then joining it leaves which row surfaces up to the provider, and picking the wrong
    /// job here means quoting the wrong dealer's warranty term. Exposed so a test can assert the SQL
    /// still carries the ORDER BY.</summary>
    public static IQueryable<int> DealerMonthsForSerialQuery(AppDbContext db, string serial) =>
        from j in db.Services.AsNoTracking()
        join d in db.Dealers.AsNoTracking() on j.DealerId equals d.Id
        where !j.IsDeleted && j.SerialNo == serial
        orderby j.Id descending
        select d.WarrantyMonths;

    private static async Task<Ok<MachineCustomersDto>> CustomersAsync(
        PasstestRepository passtest, CancellationToken ct)
        => TypedResults.Ok(new MachineCustomersDto(await passtest.CustomersAsync(ct)));
}
