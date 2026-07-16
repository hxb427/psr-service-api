using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using PSR.Service.Api.Data;

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

    /// <summary>Resolve a unit by any of its serials. Optional dealerId computes warranty (InvDate +
    /// the dealer's warranty months). 404 when not found; 503 when the passtest source is unreachable.</summary>
    private static async Task<Results<Ok<MachineTestDto>, NotFound, StatusCodeHttpResult>> BySerialAsync(
        string serial, long? dealerId, AppDbContext db, PasstestRepository passtest, CancellationToken ct)
    {
        if (!passtest.Configured) return TypedResults.StatusCode(StatusCodes.Status503ServiceUnavailable);

        int? months = dealerId is { } did
            ? await db.Dealers.AsNoTracking().Where(d => d.Id == did).Select(d => (int?)d.WarrantyMonths).FirstOrDefaultAsync(ct)
            : null;

        var dto = await passtest.FindBySerialAsync(serial, months, ct);
        return dto is null ? TypedResults.NotFound() : TypedResults.Ok(dto);
    }

    private static async Task<Ok<MachineCustomersDto>> CustomersAsync(
        PasstestRepository passtest, CancellationToken ct)
        => TypedResults.Ok(new MachineCustomersDto(await passtest.CustomersAsync(ct)));
}
