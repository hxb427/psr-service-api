using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PSR.Service.Api.Audit;
using PSR.Service.Api.Auth;
using PSR.Service.Api.Common;
using PSR.Service.Api.Data;
using PSR.Service.Api.Data.Entities;

namespace PSR.Service.Api.Stock;

public static class StockEndpoints
{
    private const int MaxPageSize = 200;

    public static IEndpointRouteBuilder MapStockEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/stock").WithTags("stock").RequireAuthorization();

        group.MapGet("/", ListAsync).RequireAuthorization("StockView");
        group.MapGet("/movements", MovementsAsync).RequireAuthorization("StockManage");
        // One number for the whole warehouse, not one page of it. The per-row columns answer "is THIS
        // part on the road"; this answers "how much of the shop's stock is in a van right now", which
        // is the question the desk could not ask at all before.
        group.MapGet("/in-transit", InTransitSummaryAsync).RequireAuthorization("StockView");
        group.MapPost("/receipts", ReceiptAsync).RequireAuthorization("StockManage");
        group.MapPost("/receipts/batch", ReceiptBatchAsync).RequireAuthorization("StockManage");
        group.MapPost("/adjustments", AdjustAsync).RequireAuthorization("StockManage");

        return app;
    }

    private static async Task<Ok<PagedResult<StockRowDto>>> ListAsync(
        AppDbContext db, string? search, bool? activeOnly, int? page, int? pageSize, CancellationToken ct)
    {
        var pageNum = page is null or < 1 ? 1 : page.Value;
        var size = pageSize is null or < 1 or > MaxPageSize ? 50 : pageSize.Value;

        var q = from p in db.Parts.AsNoTracking()
                join b in db.StockBalances.Where(x => x.TechnicianId == StockBalance.Warehouse)
                    on p.Id equals b.PartId into bg
                from b in bg.DefaultIfEmpty()
                select new { p, OnHand = b != null ? b.OnHand : 0 };

        if (activeOnly == true) q = q.Where(x => x.p.IsActive);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim();
            q = q.Where(x => x.p.ItemCode.Contains(s) || x.p.Name.Contains(s));
        }

        var total = await q.CountAsync(ct);
        var rows = await q.OrderBy(x => x.p.ItemCode)
            .Skip((pageNum - 1) * size).Take(size).ToListAsync(ct);

        var (outbound, inbound) = await InTransitAsync(db, ct);

        var items = rows.Select(x => new StockRowDto(x.p.Id, x.p.ItemCode, x.p.Name, x.p.Unit, x.OnHand,
            outbound.GetValueOrDefault(x.p.Id), inbound.GetValueOrDefault(x.p.Id))).ToList();
        return TypedResults.Ok(new PagedResult<StockRowDto>(items, pageNum, size, total));
    }

    /// <summary>Stock that has left one side and not yet arrived at the other, per part.
    ///
    /// This is the quantity that is on nobody's balance. The warehouse was debited as the issue was
    /// dispatched and the technician is not credited until they acknowledge what actually turned up;
    /// in between it is in a van, which is the honest answer and also the one nobody could see. The
    /// per-unit view has always shown it (a serial sits at ISSUED, owner "In transit to …"), but an
    /// untracked part had nothing at all, and nobody could get a total either way.
    ///
    /// Two directions, because they answer different questions. OUT is stock heading away from the
    /// shelf. IN is stock heading back to it — worth knowing before ordering more.
    ///
    /// Not filtered to the current page's parts: the set is small by definition (only things actually
    /// in flight), and filtering by a list of ids runs into the EF Core 9 + .NET 10 funcletizer bug
    /// that the rest of this codebase works around with OR-chains.</summary>
    private static async Task<(Dictionary<long, int> Outbound, Dictionary<long, int> Inbound)>
        InTransitAsync(AppDbContext db, CancellationToken ct)
    {
        // Issues awaiting acknowledgement. CreditedOnIssue false is what says "the technician has not
        // been credited yet"; rows written before dispatch and receipt were split out are true and are
        // already on somebody's balance, so they are not in transit by this definition.
        var outbound = await (from m in db.StockMovements.AsNoTracking()
                              where m.MovementType == MovementType.Issue
                                    && !m.CreditedOnIssue
                                    && !db.StockIssueAcks.Any(a => a.StockMovementId == m.Id)
                              group m by m.PartId into g
                              select new { PartId = g.Key, Qty = g.Sum(x => x.Quantity) })
            .ToDictionaryAsync(x => x.PartId, x => x.Qty, ct);

        // Returns on their way back. Only good stock: a faulty return moves no quantity at either end,
        // so counting it here would promise the shelf units that are not coming to it — they arrive
        // only if the repair job on them is stocked. TechnicianDebitedOnShip false means the shipment
        // predates the split and is still on the technician's balance, so it is not in transit either.
        var inbound = await (from r in db.StockReturns.AsNoTracking()
                             where r.Status == StockReturnStatus.Pending
                                   && r.Kind == StockReturnKind.GoodStock
                                   && r.TechnicianDebitedOnShip
                             group r by r.PartId into g
                             select new { PartId = g.Key, Qty = g.Sum(x => x.Qty) })
            .ToDictionaryAsync(x => x.PartId, x => x.Qty, ct);

        return (outbound, inbound);
    }

    private static async Task<Ok<InTransitSummaryDto>> InTransitSummaryAsync(
        AppDbContext db, CancellationToken ct)
    {
        var (outbound, inbound) = await InTransitAsync(db, ct);
        return TypedResults.Ok(new InTransitSummaryDto(
            outbound.Values.Sum(), outbound.Count, inbound.Values.Sum(), inbound.Count));
    }

    private static async Task<Ok<PagedResult<StockMovementDto>>> MovementsAsync(
        AppDbContext db, long? partId, string? movementType, long? technicianId,
        DateTime? fromDate, DateTime? toDate, int? page, int? pageSize, CancellationToken ct)
    {
        var pageNum = page is null or < 1 ? 1 : page.Value;
        var size = pageSize is null or < 1 or > MaxPageSize ? 50 : pageSize.Value;

        var q = from m in db.StockMovements.AsNoTracking()
                join p in db.Parts on m.PartId equals p.Id
                select new { m, p.ItemCode };

        if (partId is { } pid) q = q.Where(x => x.m.PartId == pid);
        if (technicianId is { } tid) q = q.Where(x => x.m.TechnicianId == tid);
        if (!string.IsNullOrWhiteSpace(movementType) && Enum.TryParse<MovementType>(movementType, true, out var mt))
            q = q.Where(x => x.m.MovementType == mt);
        if (fromDate is { } fd) q = q.Where(x => x.m.CreatedAt >= fd);
        if (toDate is { } td) q = q.Where(x => x.m.CreatedAt < td.AddDays(1));

        q = q.OrderByDescending(x => x.m.CreatedAt);
        var total = await q.CountAsync(ct);
        var raw = await q.Skip((pageNum - 1) * size).Take(size).ToListAsync(ct);
        var items = raw.Select(x => new StockMovementDto(
            x.m.Id, x.m.PartId, x.ItemCode, x.m.MovementType.ToString(), x.m.Quantity,
            x.m.TechnicianId, x.m.ReferenceType, x.m.ReferenceId, x.m.InvoiceNo, x.m.Source,
            x.m.Remarks, x.m.CreatedAt)).ToList();

        return TypedResults.Ok(new PagedResult<StockMovementDto>(items, pageNum, size, total));
    }

    private static async Task<Results<Ok<StockRowDto>, NotFound, BadRequest<string>>> ReceiptAsync(
        [FromBody] ReceiptRequest req, ClaimsPrincipal user, AppDbContext db,
        StockLedgerService ledger, IAuditService audit, HttpContext http, CancellationToken ct)
    {
        var part = await db.Parts.FirstOrDefaultAsync(p => p.Id == req.PartId, ct);
        if (part is null) return TypedResults.NotFound();
        user.TryGetUserId(out var uid);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            await ledger.ReceiptAsync(req.PartId, req.Qty, uid, req.Remarks, req.InvoiceNo, req.Source, ct);
            audit.Log(uid, "stock.receipt", "part", req.PartId, details: $"+{req.Qty}", ip: http.GetIp());
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (StockException ex) { await tx.RollbackAsync(ct); return TypedResults.BadRequest(ex.Message); }

        return TypedResults.Ok(await WarehouseRowAsync(db, part, ct));
    }

    /// <summary>Book in a whole delivery — every line under one invoice, in one transaction.
    ///
    /// Each part becomes its own ledger movement, exactly as the one-part route writes it, so nothing
    /// reading the ledger has to learn about deliveries. What the batch adds is the all-or-nothing: a
    /// ninth line the ledger refuses takes the eight before it back out, rather than leaving the
    /// storekeeper to work out how far down the invoice the count got.</summary>
    private static async Task<Results<Ok<List<StockRowDto>>, NotFound, BadRequest<string>>> ReceiptBatchAsync(
        [FromBody] ReceiptBatchRequest req, ClaimsPrincipal user, AppDbContext db,
        StockLedgerService ledger, IAuditService audit, HttpContext http, CancellationToken ct)
    {
        if (req.Lines is not { Count: > 0 }) return TypedResults.BadRequest("Add at least one item to receive.");

        // Every part is looked up before anything is written, so a bad line is refused by name instead
        // of surfacing as a failed transaction the storekeeper has to interpret.
        var parts = new List<Part>();
        foreach (var line in req.Lines)
        {
            if (line.Qty < 1) return TypedResults.BadRequest("Quantity must be at least 1.");

            var part = await db.Parts.FirstOrDefaultAsync(p => p.Id == line.PartId, ct);
            if (part is null) return TypedResults.NotFound();
            if (!part.IsActive) return TypedResults.BadRequest($"{part.ItemCode} is inactive.");

            parts.Add(part);
        }

        user.TryGetUserId(out var uid);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            // One movement and one audit line per part, the same rows the one-part route writes — a
            // delivery is a way of entering stock, not a new kind of thing in the ledger.
            foreach (var line in req.Lines)
            {
                await ledger.ReceiptAsync(line.PartId, line.Qty, uid, req.Remarks, req.InvoiceNo, req.Source, ct);
                audit.Log(uid, "stock.receipt", "part", line.PartId, details: $"+{line.Qty}", ip: http.GetIp());
            }
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (StockException ex) { await tx.RollbackAsync(ct); return TypedResults.BadRequest(ex.Message); }

        // Read back after the commit: the same part can appear on two lines of one invoice, and the
        // caller wants the shelf figure it ended on rather than the figure partway through.
        var rows = new List<StockRowDto>();
        foreach (var part in parts.DistinctBy(p => p.Id))
            rows.Add(await WarehouseRowAsync(db, part, ct));

        return TypedResults.Ok(rows);
    }

    private static async Task<Results<Ok<StockRowDto>, NotFound, BadRequest<string>>> AdjustAsync(
        [FromBody] AdjustRequest req, ClaimsPrincipal user, AppDbContext db,
        StockLedgerService ledger, IAuditService audit, HttpContext http, CancellationToken ct)
    {
        var part = await db.Parts.FirstOrDefaultAsync(p => p.Id == req.PartId, ct);
        if (part is null) return TypedResults.NotFound();
        user.TryGetUserId(out var uid);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            await ledger.AdjustAsync(req.PartId, req.Delta, uid, req.Remarks, ct);
            audit.Log(uid, "stock.adjust", "part", req.PartId, details: $"{req.Delta:+#;-#;0}", ip: http.GetIp());
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (StockException ex) { await tx.RollbackAsync(ct); return TypedResults.BadRequest(ex.Message); }

        return TypedResults.Ok(await WarehouseRowAsync(db, part, ct));
    }

    private static async Task<StockRowDto> WarehouseRowAsync(AppDbContext db, Part part, CancellationToken ct)
    {
        var onHand = await db.StockBalances.AsNoTracking()
            .Where(b => b.PartId == part.Id && b.TechnicianId == StockBalance.Warehouse)
            .Select(b => (int?)b.OnHand).FirstOrDefaultAsync(ct) ?? 0;

        // The in-transit figures too, so a row refreshed after a receipt does not come back claiming
        // nothing is on its way and quietly contradict the grid it is being dropped into.
        var outbound = await db.StockMovements.AsNoTracking()
            .Where(m => m.PartId == part.Id && m.MovementType == MovementType.Issue && !m.CreditedOnIssue
                        && !db.StockIssueAcks.Any(a => a.StockMovementId == m.Id))
            .SumAsync(m => (int?)m.Quantity, ct) ?? 0;
        var inbound = await db.StockReturns.AsNoTracking()
            .Where(r => r.PartId == part.Id && r.Status == StockReturnStatus.Pending
                        && r.Kind == StockReturnKind.GoodStock && r.TechnicianDebitedOnShip)
            .SumAsync(r => (int?)r.Qty, ct) ?? 0;

        return new StockRowDto(part.Id, part.ItemCode, part.Name, part.Unit, onHand, outbound, inbound);
    }
}
