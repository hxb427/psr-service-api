using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PSR.Service.Api.Audit;
using PSR.Service.Api.Auth;
using PSR.Service.Api.Data;
using PSR.Service.Api.Data.Entities;

namespace PSR.Service.Api.Stock;

/// <summary>Field operations by field technicians (legacy service_records / sales_transactions):
/// on-site services and direct sales. No state machine — creating one is a completed fact that
/// consumes technician stock and drives serial transitions. Pricing is server-set and stripped
/// from responses for non-pricing roles.</summary>
public static class FieldOpsEndpoints
{
    private static readonly string[] PricingRoles =
        { RoleNames.Admin, RoleNames.Manager, RoleNames.Supervisor, RoleNames.Viewer };

    public static IEndpointRouteBuilder MapFieldOpsEndpoints(this IEndpointRouteBuilder app)
    {
        var services = app.MapGroup("/field-services").WithTags("field-services").RequireAuthorization();
        services.MapGet("/", ListServicesAsync);
        services.MapPost("/", CreateServiceAsync);

        var sales = app.MapGroup("/field-sales").WithTags("field-sales").RequireAuthorization();
        sales.MapGet("/", ListSalesAsync);
        sales.MapPost("/", CreateSaleAsync);

        // Pick list: serials the caller has in hand (RECEIVED) for a part.
        app.MapGet("/serials/available", AvailableSerialsAsync).RequireAuthorization();

        return app;
    }

    // ---------------------------------------------------------------- field services

    private static async Task<Ok<List<FieldServiceDto>>> ListServicesAsync(
        AppDbContext db, ClaimsPrincipal user, long? technicianId, int? limit, CancellationToken ct)
    {
        user.TryGetUserId(out var uid);
        var manage = StockRoles.CanManage(user);
        var take = limit is null or < 1 or > 500 ? 100 : limit.Value;

        var q = db.FieldServices.AsNoTracking().Include(f => f.Lines).AsQueryable();
        if (!manage) q = q.Where(f => f.TechnicianId == uid);
        else if (technicianId is { } tid) q = q.Where(f => f.TechnicianId == tid);

        var rows = await q.OrderByDescending(f => f.Id).Take(take).ToListAsync(ct);
        var pricing = CanSeePricing(user);
        var dtos = new List<FieldServiceDto>();
        foreach (var f in rows) dtos.Add(await ServiceToDtoAsync(db, f, pricing, ct));
        return TypedResults.Ok(dtos);
    }

    private static async Task<Results<Created<FieldServiceDto>, BadRequest<string>>> CreateServiceAsync(
        [FromBody] CreateFieldServiceRequest req, ClaimsPrincipal user, AppDbContext db,
        NumberSequenceService seq, StockLedgerService ledger, SerialService serial,
        IAuditService audit, HttpContext http, CancellationToken ct)
    {
        user.TryGetUserId(out var uid);
        var tech = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == uid, ct);
        if (tech is null) return TypedResults.BadRequest("User not found.");
        var techName = tech.FullName ?? tech.Username;
        var isField = tech.IsFieldTechnician;

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        FieldService fs;
        try
        {
            var no = await seq.NextAsync(SequenceKeys.FieldService, ct);
            fs = new FieldService
            {
                ServiceNo = no, TechnicianId = uid, CustomerName = req.CustomerName.Trim(),
                Phone = req.Phone?.Trim(), Place = req.Place?.Trim(), MachineSerial = req.MachineSerial?.Trim(),
                Complaint = req.Complaint?.Trim(), WorkDone = req.WorkDone?.Trim(), Remarks = req.Remarks?.Trim(),
                CreatedByUserId = uid,
            };
            db.FieldServices.Add(fs);
            await db.SaveChangesAsync(ct);   // id for line FKs + reference id

            foreach (var lineReq in req.Lines ?? [])
            {
                var (line, err) = await BuildLineAsync(db, ledger, serial, lineReq.Kind, lineReq.PartId,
                    lineReq.Qty, lineReq.SerialNo, lineReq.Defective, uid, techName, isField,
                    fs.CustomerName, "FIELD_SERVICE", fs.Id, ct);
                if (err is not null) { await tx.RollbackAsync(ct); return TypedResults.BadRequest(err); }
                line!.FieldServiceId = fs.Id;
                db.FieldServiceLines.Add(line);
            }

            audit.Log(uid, "field-service.create", "field_service", fs.Id, details: fs.ServiceNo, ip: http.GetIp());
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (StockException ex) { await tx.RollbackAsync(ct); return TypedResults.BadRequest(ex.Message); }

        var loaded = await db.FieldServices.AsNoTracking().Include(f => f.Lines).FirstAsync(f => f.Id == fs.Id, ct);
        return TypedResults.Created($"/field-services/{fs.Id}", await ServiceToDtoAsync(db, loaded, CanSeePricing(user), ct));
    }

    /// <summary>Validates + consumes stock + drives serials for one Used/Collected line.</summary>
    private static async Task<(FieldServiceLine? line, string? error)> BuildLineAsync(
        AppDbContext db, StockLedgerService ledger, SerialService serial,
        string kindRaw, long partId, int qty, string? serialNo, bool defective,
        long uid, string techName, bool isField, string customerName,
        string referenceType, long referenceId, CancellationToken ct)
    {
        if (!Enum.TryParse<FieldLineKind>(kindRaw, true, out var kind))
            return (null, $"Unknown line kind '{kindRaw}'.");
        var part = await db.Parts.AsNoTracking().FirstOrDefaultAsync(p => p.Id == partId, ct);
        if (part is null) return (null, $"Part {partId} not found.");

        var sn = serialNo?.Trim();
        var line = new FieldServiceLine
        {
            Kind = kind, PartId = partId, Qty = qty, SerialNo = sn, Defective = defective,
            UnitPrice = kind == FieldLineKind.Used ? part.CustomerRate : 0m,
        };
        line.Amount = line.UnitPrice * qty;

        if (kind == FieldLineKind.Used)
        {
            // Serial-tracked units are consumed one per line with their serial named.
            if (part.IsSerialTracked && isField)
            {
                if (string.IsNullOrWhiteSpace(sn))
                    return (null, $"{part.ItemCode} is serial-tracked — name the fitted serial.");
                if (qty != 1)
                    return (null, $"{part.ItemCode} is serial-tracked — one line per unit (qty 1).");
                var err = await serial.ValidateFittedSerialAsync(partId, sn!, uid, ct);
                if (err is not null) return (null, err);
            }
            await ledger.ConsumeAsync(partId, uid, qty, uid, referenceType, referenceId, ct);
            if (part.IsSerialTracked && isField)
                await serial.InstallToCustomerAsync(partId, sn!, part.Name, customerName, SerialStatus.Installed, uid, ct);
        }
        else // Collected — faulty unit taken from the customer; no stock consumption.
        {
            if (string.IsNullOrWhiteSpace(sn))
                return (null, "Collected lines need the collected unit's serial number.");
            await serial.CollectFromCustomerAsync(partId, sn!, part.Name, uid, techName, defective, uid, ct);
        }
        return (line, null);
    }

    // ---------------------------------------------------------------- field sales

    private static async Task<Ok<List<FieldSaleDto>>> ListSalesAsync(
        AppDbContext db, ClaimsPrincipal user, long? technicianId, int? limit, CancellationToken ct)
    {
        user.TryGetUserId(out var uid);
        var manage = StockRoles.CanManage(user);
        var take = limit is null or < 1 or > 500 ? 100 : limit.Value;

        var q = db.FieldSales.AsNoTracking().Include(f => f.Lines).AsQueryable();
        if (!manage) q = q.Where(f => f.TechnicianId == uid);
        else if (technicianId is { } tid) q = q.Where(f => f.TechnicianId == tid);

        var rows = await q.OrderByDescending(f => f.Id).Take(take).ToListAsync(ct);
        var pricing = CanSeePricing(user);
        var dtos = new List<FieldSaleDto>();
        foreach (var f in rows) dtos.Add(await SaleToDtoAsync(db, f, pricing, ct));
        return TypedResults.Ok(dtos);
    }

    private static async Task<Results<Created<FieldSaleDto>, BadRequest<string>>> CreateSaleAsync(
        [FromBody] CreateFieldSaleRequest req, ClaimsPrincipal user, AppDbContext db,
        NumberSequenceService seq, StockLedgerService ledger, SerialService serial,
        IAuditService audit, HttpContext http, CancellationToken ct)
    {
        user.TryGetUserId(out var uid);
        var tech = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == uid, ct);
        if (tech is null) return TypedResults.BadRequest("User not found.");
        var isField = tech.IsFieldTechnician;

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        FieldSale sale;
        try
        {
            var no = await seq.NextAsync(SequenceKeys.FieldSale, ct);
            sale = new FieldSale
            {
                SaleNo = no, TechnicianId = uid, CustomerName = req.CustomerName.Trim(),
                Phone = req.Phone?.Trim(), Place = req.Place?.Trim(), Remarks = req.Remarks?.Trim(),
                CreatedByUserId = uid,
            };
            db.FieldSales.Add(sale);
            await db.SaveChangesAsync(ct);

            foreach (var lineReq in req.Lines)
            {
                var part = await db.Parts.AsNoTracking().FirstOrDefaultAsync(p => p.Id == lineReq.PartId, ct);
                if (part is null) { await tx.RollbackAsync(ct); return TypedResults.BadRequest($"Part {lineReq.PartId} not found."); }
                var sn = lineReq.SerialNo?.Trim();

                if (part.IsSerialTracked && isField)
                {
                    if (string.IsNullOrWhiteSpace(sn))
                    { await tx.RollbackAsync(ct); return TypedResults.BadRequest($"{part.ItemCode} is serial-tracked — name the sold serial."); }
                    if (lineReq.Qty != 1)
                    { await tx.RollbackAsync(ct); return TypedResults.BadRequest($"{part.ItemCode} is serial-tracked — one line per unit (qty 1)."); }
                    var err = await serial.ValidateFittedSerialAsync(lineReq.PartId, sn!, uid, ct);
                    if (err is not null) { await tx.RollbackAsync(ct); return TypedResults.BadRequest(err); }
                }

                await ledger.ConsumeAsync(lineReq.PartId, uid, lineReq.Qty, uid, "FIELD_SALE", sale.Id, ct);
                if (part.IsSerialTracked && isField)
                    await serial.InstallToCustomerAsync(lineReq.PartId, sn!, part.Name, sale.CustomerName,
                        SerialStatus.Used, uid, ct);

                var unit = part.CustomerRate;
                db.FieldSaleLines.Add(new FieldSaleLine
                {
                    FieldSaleId = sale.Id, PartId = lineReq.PartId, Qty = lineReq.Qty,
                    SerialNo = sn, UnitPrice = unit, Amount = unit * lineReq.Qty,
                });
            }

            audit.Log(uid, "field-sale.create", "field_sale", sale.Id, details: sale.SaleNo, ip: http.GetIp());
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (StockException ex) { await tx.RollbackAsync(ct); return TypedResults.BadRequest(ex.Message); }

        var loaded = await db.FieldSales.AsNoTracking().Include(f => f.Lines).FirstAsync(f => f.Id == sale.Id, ct);
        return TypedResults.Created($"/field-sales/{sale.Id}", await SaleToDtoAsync(db, loaded, CanSeePricing(user), ct));
    }

    // ---------------------------------------------------------------- available serials

    private static async Task<Ok<List<AvailableSerialDto>>> AvailableSerialsAsync(
        AppDbContext db, ClaimsPrincipal user, SerialService serial, long? partId, bool? forReturn, CancellationToken ct)
    {
        user.TryGetUserId(out var uid);
        var rows = await serial.AvailableForTechnicianAsync(uid, partId, forReturn == true, ct);
        var partIds = rows.Select(r => r.PartId).Distinct().ToList();
        var codes = await db.Parts.AsNoTracking().Where(p => partIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => p.ItemCode, ct);
        return TypedResults.Ok(rows.Select(r => new AvailableSerialDto(
            r.Id, r.PartId, codes.GetValueOrDefault(r.PartId, $"#{r.PartId}"),
            r.ItemName, r.SerialNumber, r.Status.ToString())).ToList());
    }

    // ---------------------------------------------------------------- mapping

    private static bool CanSeePricing(ClaimsPrincipal user) => PricingRoles.Any(user.IsInRole);

    private static async Task<FieldServiceDto> ServiceToDtoAsync(
        AppDbContext db, FieldService f, bool pricing, CancellationToken ct)
    {
        var techName = await TechNameAsync(db, f.TechnicianId, ct);
        var parts = await PartsForAsync(db, f.Lines.Select(l => l.PartId), ct);
        var lines = f.Lines.Select(l => new FieldServiceLineDto(
            l.Id, l.Kind.ToString(), l.PartId,
            parts.TryGetValue(l.PartId, out var p) ? p.ItemCode : $"#{l.PartId}", p?.Name ?? string.Empty,
            l.Qty, l.SerialNo, l.Defective,
            pricing ? l.UnitPrice : null, pricing ? l.Amount : null)).ToList();
        return new FieldServiceDto(
            f.Id, f.ServiceNo, f.TechnicianId, techName, f.CustomerName, f.Phone, f.Place,
            f.MachineSerial, f.Complaint, f.WorkDone, f.Remarks, f.CreatedAt,
            pricing ? f.Lines.Sum(l => l.Amount) : null, lines);
    }

    private static async Task<FieldSaleDto> SaleToDtoAsync(
        AppDbContext db, FieldSale f, bool pricing, CancellationToken ct)
    {
        var techName = await TechNameAsync(db, f.TechnicianId, ct);
        var parts = await PartsForAsync(db, f.Lines.Select(l => l.PartId), ct);
        var lines = f.Lines.Select(l => new FieldSaleLineDto(
            l.Id, l.PartId,
            parts.TryGetValue(l.PartId, out var p) ? p.ItemCode : $"#{l.PartId}", p?.Name ?? string.Empty,
            l.Qty, l.SerialNo,
            pricing ? l.UnitPrice : null, pricing ? l.Amount : null)).ToList();
        return new FieldSaleDto(
            f.Id, f.SaleNo, f.TechnicianId, techName, f.CustomerName, f.Phone, f.Place, f.Remarks, f.CreatedAt,
            pricing ? f.Lines.Sum(l => l.Amount) : null, lines);
    }

    private static Task<string?> TechNameAsync(AppDbContext db, long id, CancellationToken ct)
        => db.Users.AsNoTracking().Where(u => u.Id == id)
            .Select(u => (string?)(u.FullName ?? u.Username)).FirstOrDefaultAsync(ct);

    private static Task<Dictionary<long, Part>> PartsForAsync(
        AppDbContext db, IEnumerable<long> partIds, CancellationToken ct)
    {
        var ids = partIds.Distinct().ToList();
        return db.Parts.AsNoTracking().Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
    }
}
