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
