using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PSR.Service.Api.Audit;
using PSR.Service.Api.Auth;
using PSR.Service.Api.Data;
using PSR.Service.Api.Data.Entities;
using PSR.Service.Api.MachineTests;
using PSR.Service.Api.Settings;

namespace PSR.Service.Api.Reference;

public static class DealersEndpoints
{
    public static IEndpointRouteBuilder MapDealerEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/dealers").WithTags("dealers").RequireAuthorization();

        group.MapGet("/", ListAsync);
        group.MapGet("/import-candidates", ImportCandidatesAsync).RequireAuthorization("Admin");
        group.MapPost("/import", ImportAsync).RequireAuthorization("Admin");
        group.MapGet("/{id:long}", GetAsync);
        group.MapPost("/", CreateAsync).RequireAuthorization("Admin");
        group.MapPut("/{id:long}", UpdateAsync).RequireAuthorization("Admin");
        group.MapPost("/{id:long}/activate", (long id, ClaimsPrincipal u, AppDbContext db, IAuditService a, HttpContext h, CancellationToken ct)
            => SetActiveAsync(id, true, u, db, a, h, ct)).RequireAuthorization("Admin");
        group.MapPost("/{id:long}/deactivate", (long id, ClaimsPrincipal u, AppDbContext db, IAuditService a, HttpContext h, CancellationToken ct)
            => SetActiveAsync(id, false, u, db, a, h, ct)).RequireAuthorization("Admin");

        return app;
    }

    private static async Task<Ok<List<DealerDto>>> ListAsync(AppDbContext db, bool? activeOnly, CancellationToken ct)
    {
        var q = db.Dealers.AsNoTracking().AsQueryable();
        if (activeOnly == true) q = q.Where(x => x.IsActive);
        var items = await q.OrderBy(x => x.Name).ToListAsync(ct);
        return TypedResults.Ok(items.Select(ToDto).ToList());
    }

    private static async Task<Results<Ok<DealerDto>, NotFound>> GetAsync(long id, AppDbContext db, CancellationToken ct)
    {
        var x = await db.Dealers.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id, ct);
        return x is null ? TypedResults.NotFound() : TypedResults.Ok(ToDto(x));
    }

    private static async Task<Results<Created<DealerDto>, Conflict<string>, ValidationProblem>> CreateAsync(
        [FromBody] CreateDealerRequest req, ClaimsPrincipal user, AppDbContext db,
        IAuditService audit, HttpContext http, CancellationToken ct)
    {
        var name = req.Name.Trim();
        if (await db.Dealers.AnyAsync(d => d.Name == name, ct))
            return TypedResults.Conflict($"Dealer '{name}' already exists.");

        var d = new Dealer
        {
            Name = name, WarrantyMonths = req.WarrantyMonths,
            Address = req.Address?.Trim(), Gstin = req.Gstin?.Trim(),
            State = req.State?.Trim(), StateCode = req.StateCode?.Trim(),
            Remarks = req.Remarks?.Trim(),
        };
        db.Dealers.Add(d);
        // Saved first so the audit row can carry the id of the dealer it created.
        await db.SaveChangesAsync(ct);
        user.TryGetUserId(out var actor);
        audit.Log(actor, "dealer.create", "dealer", d.Id,
            details: $"'{name}' warranty {d.WarrantyMonths} month(s)", ip: http.GetIp());
        await db.SaveChangesAsync(ct);
        return TypedResults.Created($"/dealers/{d.Id}", ToDto(d));
    }

    private static async Task<Results<Ok<DealerDto>, NotFound, ValidationProblem>> UpdateAsync(
        long id, [FromBody] UpdateDealerRequest req, ClaimsPrincipal user, AppDbContext db,
        IAuditService audit, HttpContext http, CancellationToken ct)
    {
        var d = await db.Dealers.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (d is null) return TypedResults.NotFound();
        // Warranty months and GSTIN are the two that change money and paperwork downstream, so the
        // audit line has to name them rather than just say the dealer was saved.
        var name = d.Name;
        var diff = new AuditDiff();
        diff.Set("name", d.Name, req.Name, v => d.Name = v ?? d.Name);
        diff.Set("warranty months", d.WarrantyMonths, req.WarrantyMonths, v => d.WarrantyMonths = v);
        diff.Set("address", d.Address, req.Address, v => d.Address = v);
        diff.Set("GSTIN", d.Gstin, req.Gstin, v => d.Gstin = v);
        diff.Set("state", d.State, req.State, v => d.State = v);
        diff.Set("state code", d.StateCode, req.StateCode, v => d.StateCode = v);
        diff.Set("remarks", d.Remarks, req.Remarks, v => d.Remarks = v);

        user.TryGetUserId(out var actor);
        audit.Log(actor, "dealer.update", "dealer", id, details: diff.Describe(name), ip: http.GetIp());
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(ToDto(d));
    }

    private static async Task<Results<NoContent, NotFound>> SetActiveAsync(
        long id, bool active, ClaimsPrincipal user, AppDbContext db, IAuditService audit, HttpContext http, CancellationToken ct)
    {
        var d = await db.Dealers.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (d is null) return TypedResults.NotFound();
        d.IsActive = active;
        user.TryGetUserId(out var actor);
        audit.Log(actor, active ? "dealer.activate" : "dealer.deactivate", "dealer", id, ip: http.GetIp());
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    /// <summary>Scans the legacy Hostinger DB for dealers the master doesn't have yet. The diff runs
    /// here, not on the client: MySQL collapses the whole passtestdata table to distinct names, and
    /// only the handful that are actually new crosses the wire. Nothing is written — the admin picks.</summary>
    private static async Task<Results<Ok<DealerImportCandidatesDto>, StatusCodeHttpResult>> ImportCandidatesAsync(
        AppDbContext db, PasstestRepository passtest, AppSettingsService settings, CancellationToken ct)
    {
        if (!passtest.Configured) return TypedResults.StatusCode(StatusCodes.Status503ServiceUnavailable);

        var legacy = await passtest.ScanLegacyDealersAsync(ct);
        var customers = await passtest.ScanCustomersAsync(ct);
        if (legacy is null && customers is null)
            return TypedResults.StatusCode(StatusCodes.Status503ServiceUnavailable);

        var warnings = new List<string>();
        if (legacy is null)
            warnings.Add("Could not read dealer_warranty — the read-only login probably has no SELECT grant on it. Warranty months are blank below and must be set by hand.");
        if (customers is null)
            warnings.Add("Could not read passtestdata — dealers seen only on machines are not listed.");

        // Existing dealers, keyed by normalized name. Inactive ones count: they exist, so re-importing
        // them would hit the unique index — the admin should reactivate instead.
        var existing = await db.Dealers.AsNoTracking().Select(d => d.Name).ToListAsync(ct);
        var existingKeys = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in existing) existingKeys.TryAdd(DealerNameKey.Normalize(name), name);

        var byKey = new Dictionary<string, DealerImportCandidateDto>(StringComparer.Ordinal);

        // dealer_warranty first — it is the curated master and the only source of warranty months.
        foreach (var row in legacy ?? [])
        {
            var key = DealerNameKey.Normalize(row.Name);
            if (key.Length == 0 || existingKeys.ContainsKey(key)) continue;
            byKey.TryAdd(key, new DealerImportCandidateDto(
                DealerNameKey.Clean(row.Name), row.WarrantyMonths, null, row.Remarks, "dealer_warranty", 0, null));
        }

        // passtestdata names: enrich a dealer_warranty hit with its machine count and address (only
        // passtestdata carries one — dealer_warranty is name + warranty + remarks), else stand alone.
        foreach (var row in customers ?? [])
        {
            var key = DealerNameKey.Normalize(row.Name);
            if (key.Length == 0 || existingKeys.ContainsKey(key)) continue;
            // The term this buyer's machines were last sold on — a real figure, so it beats the house
            // default. dealer_warranty still wins where it has one: that is the negotiated term.
            var machineMonths = PasstestRepository.ParseWarrantyMonths(row.WarrantyText);

            byKey[key] = byKey.TryGetValue(key, out var hit)
                ? hit with
                {
                    Source = "both",
                    MachineCount = hit.MachineCount + row.MachineCount,
                    Address = hit.Address ?? row.Address,
                    WarrantyMonths = hit.WarrantyMonths ?? machineMonths,
                }
                : new DealerImportCandidateDto(
                    DealerNameKey.Clean(row.Name), machineMonths, row.Address, null,
                    "passtestdata", row.MachineCount, null);
        }

        // passtestdata carries no warranty term, so fall back to the admin's default rather than
        // leaving 0 — a dealer imported at 0 months silently reads OUT of warranty on every lookup.
        var defaultMonths = await settings.DefaultWarrantyMonthsAsync(ct);

        // Flag likely duplicates of dealers already in the master so the admin doesn't create a twin.
        var candidates = byKey
            .Select(kv => kv.Value with
            {
                WarrantyMonths = kv.Value.WarrantyMonths ?? (defaultMonths > 0 ? defaultMonths : null),
                PossibleMatch = existingKeys
                    .Where(e => DealerNameKey.IsNearMatch(kv.Key, e.Key))
                    .Select(e => e.Value)
                    .FirstOrDefault(),
            })
            .OrderByDescending(c => c.Source != "passtestdata")   // curated master first
            .ThenByDescending(c => c.MachineCount)                // then by how many units they took
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return TypedResults.Ok(new DealerImportCandidatesDto(
            candidates, existing.Count, legacy?.Count ?? 0, customers?.Count ?? 0, warnings));
    }

    /// <summary>Bulk-creates the dealers an admin approved. Creates only — never updates or
    /// deactivates an existing dealer — so re-running the scan is always safe. Names that already
    /// exist (after normalization) are skipped, not failed.</summary>
    private static async Task<Results<Ok<DealerImportResultDto>, BadRequest<string>>> ImportAsync(
        [FromBody] DealerImportRequest req, ClaimsPrincipal user, AppDbContext db,
        IAuditService audit, HttpContext http, CancellationToken ct)
    {
        if (req.Dealers is null || req.Dealers.Count == 0) return TypedResults.BadRequest("No dealers selected.");
        if (req.Dealers.Count > 1000) return TypedResults.BadRequest("Too many dealers in one import (max 1000).");

        var existing = await db.Dealers.AsNoTracking().Select(d => d.Name).ToListAsync(ct);
        var seen = new HashSet<string>(existing.Select(DealerNameKey.Normalize), StringComparer.Ordinal);

        var toCreate = new List<Dealer>();
        var skipped = new List<string>();

        foreach (var item in req.Dealers)
        {
            var name = DealerNameKey.Clean(item.Name ?? string.Empty);
            if (name.Length == 0) continue;
            if (name.Length > 200) return TypedResults.BadRequest($"Dealer name too long (max 200): '{name[..40]}…'");
            if (item.WarrantyMonths is < 0 or > 600)
                return TypedResults.BadRequest($"Warranty months for '{name}' must be between 0 and 600.");

            if (!seen.Add(DealerNameKey.Normalize(name))) { skipped.Add(name); continue; }

            toCreate.Add(new Dealer
            {
                Name = name,
                WarrantyMonths = item.WarrantyMonths,
                Address = Fit(item.Address, 500),
                Remarks = Fit(item.Remarks, 500),
            });
        }

        if (toCreate.Count > 0)
        {
            db.Dealers.AddRange(toCreate);
            user.TryGetUserId(out var actor);
            audit.Log(actor, "dealer.import", "dealer", null,
                details: $"{toCreate.Count} imported from legacy", ip: http.GetIp());
            await db.SaveChangesAsync(ct);
        }

        return TypedResults.Ok(new DealerImportResultDto(toCreate.Count, skipped.Count, skipped));
    }

    /// <summary>Trims to null and clips to the column width — legacy free text has no length limits.</summary>
    private static string? Fit(string? value, int max)
    {
        var s = value?.Trim();
        return string.IsNullOrEmpty(s) ? null : s[..Math.Min(s.Length, max)];
    }

    private static DealerDto ToDto(Dealer x) =>
        new(x.Id, x.Name, x.WarrantyMonths, x.Address, x.Gstin, x.State, x.StateCode, x.Remarks, x.IsActive);
}
