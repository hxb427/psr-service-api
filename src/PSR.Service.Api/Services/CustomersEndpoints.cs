using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PSR.Service.Api.Audit;
using PSR.Service.Api.Auth;
using PSR.Service.Api.Data;
using PSR.Service.Api.Data.Entities;

namespace PSR.Service.Api.Services;

public static class CustomersEndpoints
{
    public static IEndpointRouteBuilder MapCustomerEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/customers").WithTags("customers").RequireAuthorization();

        group.MapGet("/", ListAsync);
        group.MapGet("/{id:long}", GetAsync);
        // Ahead of nothing and behind nothing: "/addresses" cannot be mistaken for "/{id:long}"
        // because the constraint rules it out, so the two can be declared in either order.
        group.MapGet("/addresses", AddressesAsync);
        group.MapPost("/", CreateAsync).RequireAuthorization("InwardManage");

        return app;
    }

    private static async Task<Ok<List<CustomerDto>>> ListAsync(
        AppDbContext db, string? search, bool? activeOnly, CancellationToken ct)
    {
        var q = db.Customers.AsNoTracking().AsQueryable();
        if (activeOnly == true) q = q.Where(c => c.IsActive);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim();
            q = q.Where(c => c.Name.Contains(s)
                          || (c.OrganizationName != null && c.OrganizationName.Contains(s))
                          || (c.Phone != null && c.Phone.Contains(s)));
        }

        var rows = await q.OrderBy(c => c.Name).Take(100).ToListAsync(ct);
        return TypedResults.Ok(rows.Select(ToDto).ToList());
    }

    /// <summary>Distinct customer addresses matching what has been typed, for an address box that
    /// suggests rather than dictates.
    ///
    /// Distinct strings and not customers: several customers share an address (a co-operative's three
    /// societies, a dealer and the shop next door), and an address list that repeats the same street
    /// four times because four accounts sit on it is a list nobody scrolls. The caller has already
    /// decided they want to type an address rather than pick an account — the account link is what
    /// the customer lookup is for.
    ///
    /// Two characters minimum, matching the customer search: one character matches most of the table
    /// and the suggestion list stops being a suggestion.</summary>
    private static async Task<Ok<List<string>>> AddressesAsync(
        AppDbContext db, string? search, int? limit, CancellationToken ct)
    {
        var s = search?.Trim() ?? "";
        if (s.Length < 2) return TypedResults.Ok(new List<string>());

        var take = limit is null or < 1 or > 50 ? 20 : limit.Value;
        return TypedResults.Ok(await AddressQuery(db, s, take).ToListAsync(ct));
    }

    /// <summary>The address suggestion selection, as one query.
    ///
    /// Internal and separate so a test can make the provider translate it without a database. The
    /// shape is the reason: DISTINCT over a projection, then ordered and limited. EF will not always
    /// order after a Distinct, and finding that out in front of somebody typing an address is the
    /// expensive way to find it out.</summary>
    internal static IQueryable<string> AddressQuery(AppDbContext db, string search, int take) =>
        db.Customers.AsNoTracking()
            .Where(c => c.IsActive && c.Address != null && c.Address != "" && c.Address.Contains(search))
            .Select(c => c.Address!)
            .Distinct()
            .OrderBy(a => a)
            .Take(take);

    private static async Task<Results<Ok<CustomerDto>, NotFound>> GetAsync(long id, AppDbContext db, CancellationToken ct)
    {
        var c = await db.Customers.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        return c is null ? TypedResults.NotFound() : TypedResults.Ok(ToDto(c));
    }

    private static async Task<Results<Created<CustomerDto>, ValidationProblem>> CreateAsync(
        [FromBody] CreateCustomerRequest req, ClaimsPrincipal user, AppDbContext db,
        IAuditService audit, HttpContext http, CancellationToken ct)
    {
        var c = new Customer
        {
            Name = req.Name.Trim(),
            OrganizationName = req.OrganizationName?.Trim(),
            Phone = req.Phone?.Trim(),
            Email = req.Email?.Trim(),
            Address = req.Address?.Trim(),
        };
        db.Customers.Add(c);
        // Saved first so the audit row can carry the id of the customer it created.
        await db.SaveChangesAsync(ct);

        user.TryGetUserId(out var uid);
        audit.Log(uid, "customer.create", "customer", c.Id,
            details: $"'{c.Name}'" + (c.Phone is { Length: > 0 } ? $" {c.Phone}" : ""), ip: http.GetIp());
        await db.SaveChangesAsync(ct);

        return TypedResults.Created($"/customers/{c.Id}", ToDto(c));
    }

    internal static CustomerDto ToDto(Customer c) =>
        new(c.Id, c.Name, c.OrganizationName, c.Phone, c.Email, c.Address, c.IsActive);
}
