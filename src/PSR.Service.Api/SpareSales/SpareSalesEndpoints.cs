using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PSR.Service.Api.Audit;
using PSR.Service.Api.Auth;
using PSR.Service.Api.Common;
using PSR.Service.Api.Data;
using PSR.Service.Api.Data.Entities;
using PSR.Service.Api.Stock;

namespace PSR.Service.Api.SpareSales;

internal static class SaleRoles
{
    /// <summary>Who sees the money on a sale. The parts-master pricing roles PLUS accounts, because accounts
    /// raises the invoice. store_manager can see the sale and its quantities — they pick and pack it — but
    /// the rates stay hidden, matching how parts pricing is stripped everywhere else.</summary>
    public static readonly string[] Pricing =
        { RoleNames.Admin, RoleNames.Manager, RoleNames.Supervisor, RoleNames.Viewer, RoleNames.Accounts };

    public static bool CanSeePricing(ClaimsPrincipal u) => Pricing.Any(u.IsInRole);
}

/// <summary>Direct (counter) sales of warehouse stock to a dealer or a walk-in customer — the legacy
/// "Generate Sale PI" page. A sale is entered and priced here; the PI / tax invoice for it is produced by
/// the documents module, and the warehouse is only drawn down when that invoice is generated.</summary>
public static class SpareSalesEndpoints
{
    private const int MaxPageSize = 200;

    public static IEndpointRouteBuilder MapSpareSaleEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/spare-sales").WithTags("spare-sales").RequireAuthorization("SaleView");

        group.MapGet("/", ListAsync);
        group.MapGet("/{id:long}", GetAsync);
        group.MapPost("/", CreateAsync).RequireAuthorization("SaleManage");
        group.MapPut("/{id:long}", UpdateAsync).RequireAuthorization("SaleManage");
        group.MapPost("/{id:long}/cancel", CancelAsync).RequireAuthorization("SaleManage");
        group.MapDelete("/{id:long}", DeleteAsync).RequireAuthorization("SaleManage");
        group.MapPost("/{id:long}/payment", PaymentAsync).RequireAuthorization("PaymentManage");

        return app;
    }

    // ---------------------------------------------------------------- queries

    private static async Task<Ok<PagedResult<SpareSaleListItemDto>>> ListAsync(
        AppDbContext db, ClaimsPrincipal user, string? status, string? payment, string? search,
        DateTime? fromDate, DateTime? toDate, int? page, int? pageSize, CancellationToken ct)
    {
        var pageNum = page is null or < 1 ? 1 : page.Value;
        var size = pageSize is null or < 1 or > MaxPageSize ? 50 : pageSize.Value;

        // Left-join both party tables — a sale has exactly one of them.
        var q = from s in db.SpareSales.AsNoTracking()
                where !s.IsDeleted
                join d in db.Dealers on s.DealerId equals d.Id into dg
                from d in dg.DefaultIfEmpty()
                join c in db.Customers on s.CustomerId equals c.Id into cg
                from c in cg.DefaultIfEmpty()
                select new
                {
                    Sale = s,
                    PartyName = d != null ? d.Name : (c != null ? c.Name : string.Empty),
                    LineCount = db.SpareSaleLines.Count(l => l.SpareSaleId == s.Id),
                };

        if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<SpareSaleStatus>(status, true, out var st))
            q = q.Where(x => x.Sale.Status == st);
        if (!string.IsNullOrWhiteSpace(payment) && Enum.TryParse<PaymentStatus>(payment, true, out var pay))
            q = q.Where(x => x.Sale.PaymentStatus == pay);
        if (fromDate is { } fd) q = q.Where(x => x.Sale.SaleDate >= fd.Date);
        if (toDate is { } td) q = q.Where(x => x.Sale.SaleDate < td.Date.AddDays(1));
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            q = q.Where(x => x.Sale.SaleNo.Contains(term)
                          || x.PartyName.Contains(term)
                          || (x.Sale.PiNo != null && x.Sale.PiNo.Contains(term))
                          || (x.Sale.InvNo != null && x.Sale.InvNo.Contains(term)));
        }

        var total = await q.CountAsync(ct);
        var rows = await q.OrderByDescending(x => x.Sale.Id)
            .Skip((pageNum - 1) * size).Take(size).ToListAsync(ct);

        // Enum → string happens here, client-side: EF cannot translate enum.ToString() in a projection.
        var pricing = SaleRoles.CanSeePricing(user);
        var items = rows.Select(x => new SpareSaleListItemDto(
            x.Sale.Id, x.Sale.SaleNo, x.Sale.SaleDate, x.Sale.CustomerType, x.PartyName,
            x.Sale.Status.ToString(), x.Sale.PaymentStatus.ToString(), x.Sale.PiNo, x.Sale.InvNo,
            x.LineCount, pricing ? x.Sale.TotalAmount : null)).ToList();

        return TypedResults.Ok(new PagedResult<SpareSaleListItemDto>(items, pageNum, size, total));
    }

    private static async Task<Results<Ok<SpareSaleDetailDto>, NotFound>> GetAsync(
        long id, AppDbContext db, ClaimsPrincipal user, CancellationToken ct)
    {
        var dto = await BuildDetailAsync(db, id, SaleRoles.CanSeePricing(user), ct);
        return dto is null ? TypedResults.NotFound() : TypedResults.Ok(dto);
    }

    // ---------------------------------------------------------------- writes

    private static async Task<Results<Created<SpareSaleDetailDto>, BadRequest<string>>> CreateAsync(
        [FromBody] SaveSpareSaleRequest req, ClaimsPrincipal user, AppDbContext db,
        SpareSaleService sales, NumberSequenceService seq, IAuditService audit,
        HttpContext http, CancellationToken ct)
    {
        user.TryGetUserId(out var uid);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        SpareSale sale;
        try
        {
            sale = new SpareSale { SaleNo = await seq.NextAsync(SequenceKeys.SpareSale, ct), CreatedByUserId = uid };
            await sales.ApplyAsync(sale, req, uid, audit, http.GetIp(), ct);
            db.SpareSales.Add(sale);

            audit.Log(uid, "spare-sale.create", "spare_sale", null,
                details: $"{sale.SaleNo} — {sale.Lines.Count} item(s)", ip: http.GetIp());
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (SaleValidationException ex) { await tx.RollbackAsync(ct); return TypedResults.BadRequest(ex.Message); }
        catch (StockException ex) { await tx.RollbackAsync(ct); return TypedResults.BadRequest(ex.Message); }

        var dto = await BuildDetailAsync(db, sale.Id, SaleRoles.CanSeePricing(user), ct);
        return TypedResults.Created($"/spare-sales/{sale.Id}", dto!);
    }

    private static async Task<Results<Ok<SpareSaleDetailDto>, NotFound, BadRequest<string>>> UpdateAsync(
        long id, [FromBody] SaveSpareSaleRequest req, ClaimsPrincipal user, AppDbContext db,
        SpareSaleService sales, IAuditService audit, HttpContext http, CancellationToken ct)
    {
        var sale = await db.SpareSales.Include(s => s.Lines).FirstOrDefaultAsync(s => s.Id == id && !s.IsDeleted, ct);
        if (sale is null) return TypedResults.NotFound();

        // Once a PI exists the figures have been sent to the customer, and once invoiced the stock has moved.
        if (sale.Status != SpareSaleStatus.Pending)
            return TypedResults.BadRequest($"This sale is {sale.Status.ToString().ToLowerInvariant()} and can no longer be edited.");
        if (!string.IsNullOrWhiteSpace(sale.PiNo))
            return TypedResults.BadRequest($"A PI ({sale.PiNo}) has already been generated for this sale.");

        user.TryGetUserId(out var uid);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            db.SpareSaleLines.RemoveRange(sale.Lines);
            await sales.ApplyAsync(sale, req, uid, audit, http.GetIp(), ct);

            audit.Log(uid, "spare-sale.update", "spare_sale", sale.Id, details: sale.SaleNo, ip: http.GetIp());
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (SaleValidationException ex) { await tx.RollbackAsync(ct); return TypedResults.BadRequest(ex.Message); }

        return TypedResults.Ok((await BuildDetailAsync(db, sale.Id, SaleRoles.CanSeePricing(user), ct))!);
    }

    private static async Task<Results<Ok<SpareSaleDetailDto>, NotFound, BadRequest<string>>> CancelAsync(
        long id, ClaimsPrincipal user, AppDbContext db, IAuditService audit, HttpContext http, CancellationToken ct)
    {
        var sale = await db.SpareSales.FirstOrDefaultAsync(s => s.Id == id && !s.IsDeleted, ct);
        if (sale is null) return TypedResults.NotFound();
        if (sale.Status == SpareSaleStatus.Cancelled) return TypedResults.BadRequest("This sale is already cancelled.");
        if (sale.Status == SpareSaleStatus.Invoiced)
            return TypedResults.BadRequest(
                $"Invoice {sale.InvNo} has been raised and the goods have left the warehouse — book a stock adjustment instead.");

        sale.Status = SpareSaleStatus.Cancelled;
        user.TryGetUserId(out var uid);
        audit.Log(uid, "spare-sale.cancel", "spare_sale", sale.Id, details: sale.SaleNo, ip: http.GetIp());
        await db.SaveChangesAsync(ct);

        return TypedResults.Ok((await BuildDetailAsync(db, sale.Id, SaleRoles.CanSeePricing(user), ct))!);
    }

    private static async Task<Results<NoContent, NotFound, BadRequest<string>>> DeleteAsync(
        long id, ClaimsPrincipal user, AppDbContext db, IAuditService audit, HttpContext http, CancellationToken ct)
    {
        var sale = await db.SpareSales.FirstOrDefaultAsync(s => s.Id == id && !s.IsDeleted, ct);
        if (sale is null) return TypedResults.NotFound();
        if (sale.Status == SpareSaleStatus.Invoiced)
            return TypedResults.BadRequest($"Invoice {sale.InvNo} has been raised — an invoiced sale cannot be removed.");

        sale.IsDeleted = true;
        user.TryGetUserId(out var uid);
        audit.Log(uid, "spare-sale.delete", "spare_sale", sale.Id, details: sale.SaleNo, ip: http.GetIp());
        await db.SaveChangesAsync(ct);

        return TypedResults.NoContent();
    }

    private static async Task<Results<Ok<SpareSaleDetailDto>, NotFound, BadRequest<string>>> PaymentAsync(
        long id, [FromBody] SalePaymentRequest req, ClaimsPrincipal user, AppDbContext db,
        IAuditService audit, HttpContext http, CancellationToken ct)
    {
        var sale = await db.SpareSales.FirstOrDefaultAsync(s => s.Id == id && !s.IsDeleted, ct);
        if (sale is null) return TypedResults.NotFound();
        if (sale.Status == SpareSaleStatus.Cancelled) return TypedResults.BadRequest("This sale is cancelled.");
        if (!Enum.TryParse<PaymentStatus>(req.Status, true, out var status))
            return TypedResults.BadRequest($"Unknown payment status '{req.Status}'. Use Pending, Partial or Paid.");

        sale.PaymentStatus = status;
        user.TryGetUserId(out var uid);
        audit.Log(uid, "spare-sale.payment", "spare_sale", sale.Id,
            details: $"{sale.SaleNo} → {status}", ip: http.GetIp());
        await db.SaveChangesAsync(ct);

        return TypedResults.Ok((await BuildDetailAsync(db, sale.Id, SaleRoles.CanSeePricing(user), ct))!);
    }

    // ---------------------------------------------------------------- mapping

    internal static async Task<SpareSaleDetailDto?> BuildDetailAsync(
        AppDbContext db, long id, bool pricing, CancellationToken ct)
    {
        var sale = await db.SpareSales.AsNoTracking().Include(s => s.Lines)
            .FirstOrDefaultAsync(s => s.Id == id && !s.IsDeleted, ct);
        if (sale is null) return null;

        string partyName = string.Empty;
        string? address = null, gstin = null, state = null, stateCode = null;
        if (sale.DealerId is { } did)
        {
            var d = await db.Dealers.AsNoTracking().Where(x => x.Id == did)
                .Select(x => new { x.Name, x.Address, x.Gstin, x.State, x.StateCode }).FirstOrDefaultAsync(ct);
            partyName = d?.Name ?? string.Empty;
            address = d?.Address; gstin = d?.Gstin; state = d?.State; stateCode = d?.StateCode;
        }
        else if (sale.CustomerId is { } cid)
        {
            var c = await db.Customers.AsNoTracking().Where(x => x.Id == cid)
                .Select(x => new { x.Name, x.Address }).FirstOrDefaultAsync(ct);
            partyName = c?.Name ?? string.Empty;
            address = c?.Address;
        }

        var createdBy = await db.Users.AsNoTracking().Where(u => u.Id == sale.CreatedByUserId)
            .Select(u => (string?)(u.FullName ?? u.Username)).FirstOrDefaultAsync(ct);

        var lines = new List<SpareSaleLineDto>();
        foreach (var l in sale.Lines.OrderBy(l => l.Id))
        {
            var onHand = await db.StockBalances.AsNoTracking()
                .Where(b => b.PartId == l.PartId && b.TechnicianId == StockBalance.Warehouse)
                .Select(b => b.OnHand).FirstOrDefaultAsync(ct);
            lines.Add(new SpareSaleLineDto(
                l.Id, l.PartId, l.ItemCode, l.Description, l.HsnCode, l.Unit, l.Qty, l.RateType.ToString(),
                pricing ? l.UnitRate : null, l.GstPercent,
                pricing ? l.TaxableAmount : null, pricing ? l.TaxAmount : null, pricing ? l.LineTotal : null,
                onHand));
        }

        return new SpareSaleDetailDto(
            sale.Id, sale.SaleNo, sale.SaleDate, sale.CustomerType, sale.DealerId, sale.CustomerId,
            partyName, address, gstin, state, stateCode,
            sale.Status.ToString(), sale.PaymentStatus.ToString(),
            sale.PiNo, sale.PiDate, sale.InvNo, sale.InvDate,
            pricing ? sale.TaxableAmount : null, pricing ? sale.TaxAmount : null, pricing ? sale.TotalAmount : null,
            sale.Remarks, createdBy, sale.CreatedAt, lines);
    }
}
