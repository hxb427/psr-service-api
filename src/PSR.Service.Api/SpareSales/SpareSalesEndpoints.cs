using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PSR.Service.Api.Audit;
using PSR.Service.Api.Auth;
using PSR.Service.Api.Common;
using PSR.Service.Api.Data;
using PSR.Service.Api.Data.Entities;
using PSR.Service.Api.Documents;
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
/// "Generate Sale PI" page. A sale is entered and priced here and the PI / tax invoice for it is produced
/// by the documents module.
///
/// Stock is a separate axis from paperwork: nothing on the document or payment chain touches the
/// warehouse, and the goods leave on <c>mark-sold</c> alone. Everything below that talks about stock —
/// what may still be edited, what may be cancelled, what may be returned — reads
/// <see cref="Data.Entities.SpareSale.SoldAt"/> and not the status.</summary>
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
        // The one action that moves warehouse stock on a sale. Gated on StockManage rather than
        // SaleManage: it is a warehouse movement, and the store manager who hands the goods over is
        // the person who knows they went out.
        //
        // It now marks the sale paid as well, which means StockManage settles a counter sale's payment
        // without PaymentManage. That is deliberate and not a hole: the person handing the goods across
        // the counter is the person taking the cash for them, and the register no longer offers a
        // separate payment step for anyone else to take. /payment below still needs PaymentManage, for
        // the sale that has to be corrected afterwards.
        group.MapPost("/{id:long}/mark-sold", MarkSoldAsync).RequireAuthorization("StockManage");
        group.MapPost("/{id:long}/unmark-sold", UnmarkSoldAsync).RequireAuthorization("StockManage");
        group.MapPost("/{id:long}/clear-pi", ClearPiAsync).RequireAuthorization("SaleManage");
        // Gated on DocumentManage rather than SaleManage: it is the generate form's own scratch pad,
        // and accounts raises documents without being able to edit the sale behind them.
        group.MapPost("/{id:long}/courier", SaveCourierAsync).RequireAuthorization("DocumentManage");
        group.MapPost("/{id:long}/returns", CreateReturnAsync).RequireAuthorization("SaleManage");
        // Asked per row by the sale form as the user types, so it stays a single-part lookup.
        group.MapGet("/availability/{partId:long}", AvailabilityAsync);

        return app;
    }

    /// <summary>What the warehouse holds for one part. <c>excludeSaleId</c> is still accepted and ignored:
    /// it only ever mattered to the claim arithmetic that is gone, and the sale form on the previous
    /// build still sends it.</summary>
    private static async Task<Ok<PartAvailabilityDto>> AvailabilityAsync(
        long partId, long? excludeSaleId, SpareSaleService sales, CancellationToken ct)
    {
        var onHand = await sales.WarehouseOnHandAsync(partId, ct);
        // Committed 0, Available == OnHand — see PartAvailabilityDto.
        return TypedResults.Ok(new PartAvailabilityDto(partId, onHand, 0, onHand));
    }

    // ---------------------------------------------------------------- queries

    /// <param name="states">GST state codes, comma separated — the register narrowed to the regions it is
    /// being read for. Several at once because that is the question: everything billed into Tamil Nadu or
    /// Karnataka was two passes and a mental merge before.</param>
    private static async Task<Ok<PagedResult<SpareSaleListItemDto>>> ListAsync(
        AppDbContext db, ClaimsPrincipal user, string? status, string? payment, string? search,
        string? customer, DateTime? fromDate, DateTime? toDate, bool? excludeCancelled,
        string? states, int? page, int? pageSize, CancellationToken ct)
    {
        var pageNum = page is null or < 1 ? 1 : page.Value;
        var size = pageSize is null or < 1 or > MaxPageSize ? 50 : pageSize.Value;

        // Narrowed before the party joins, so the statewise index does the work rather than a filter
        // over the joined rows.
        var sales = ApplyStateFilter(db.SpareSales.AsNoTracking().Where(s => !s.IsDeleted), states);

        // Left-join both party tables — a sale has exactly one of them.
        var q = from s in sales
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

        // "Sold" / "Unsold" are stock filters rather than statuses, so they are read off SoldAt. The
        // register mixes the two axes in one drop-down because that is how the counter thinks about it.
        if (string.Equals(status, "Sold", StringComparison.OrdinalIgnoreCase))
            q = q.Where(x => x.Sale.SoldAt != null);
        else if (string.Equals(status, "Unsold", StringComparison.OrdinalIgnoreCase))
            q = q.Where(x => x.Sale.SoldAt == null && x.Sale.Status != SpareSaleStatus.Cancelled);
        else if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<SpareSaleStatus>(status, true, out var st))
            q = q.Where(x => x.Sale.Status == st);
        // A cancelled sale is the record of something that did not happen, and it sat in the middle of the
        // register the counter reads every day. The Show cancelled toggle is what asks for them — off, the
        // list leaves them out; on, it asks for nothing else, which the status filter above already does.
        if (excludeCancelled == true)
            q = q.Where(x => x.Sale.Status != SpareSaleStatus.Cancelled);
        if (!string.IsNullOrWhiteSpace(payment) && Enum.TryParse<PaymentStatus>(payment, true, out var pay))
            q = q.Where(x => x.Sale.PaymentStatus == pay);
        if (fromDate is { } fd) q = q.Where(x => x.Sale.SaleDate >= fd.Date);
        if (toDate is { } td) q = q.Where(x => x.Sale.SaleDate < td.Date.AddDays(1));
        // Narrows to the party on its own, where the wide search can only reach it together with the
        // sale, PI and invoice numbers. Same contains-match, against the dealer or customer name the
        // join above resolved.
        if (!string.IsNullOrWhiteSpace(customer))
        {
            var name = customer.Trim();
            q = q.Where(x => x.PartyName.Contains(name));
        }
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
            x.LineCount, pricing ? x.Sale.TotalAmount : null, x.Sale.SoldAt)).ToList();

        return TypedResults.Ok(new PagedResult<SpareSaleListItemDto>(items, pageNum, size, total));
    }

    /// <summary>The register narrowed to the states it is being read for, several at a time.
    ///
    /// Read off the state recorded ON THE SALE, not off the party master: that is what the counter
    /// entered and what the documents were raised on, so a dealer who moves state does not rewrite where
    /// last year's sales were billed. A sale carrying no state falls outside the filter rather than
    /// being guessed at — the entry form requires one, and the migration backfilled the sales that were
    /// already on the books from the master the generate form had been reading all along.
    ///
    /// Asking for no states, or for nothing that parses as one, leaves the query alone. A stray comma
    /// should not empty the register.</summary>
    internal static IQueryable<SpareSale> ApplyStateFilter(IQueryable<SpareSale> sales, string? states)
    {
        var codes = ParseStateCodes(states);
        return codes.Count == 0
            ? sales
            : sales.Where(s => s.PartyStateCode != null && codes.Contains(s.PartyStateCode));
    }

    /// <summary>The statewise filter's codes: normalized to the two-digit form every state comparison
    /// here is made in, and de-duplicated. Anything that is not a code is dropped rather than narrowing
    /// the register to nothing.</summary>
    internal static List<string> ParseStateCodes(string? states) =>
        (states ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(GstStates.NormalizeCode)
            .OfType<string>()
            .Distinct()
            .ToList();

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

            // Saved once inside the transaction so the audit row can carry the sale's id, not just
            // its number.
            await db.SaveChangesAsync(ct);
            audit.Log(uid, "spare-sale.create", "spare_sale", sale.Id,
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

        // Once the goods have gone out, the lines are a record of what left the shelf — rewriting them
        // would leave the ledger describing a sale that no longer exists. Un-mark it first.
        if (sale.SoldAt is not null)
            return TypedResults.BadRequest(
                "This sale is marked sold and the goods have left the warehouse — un-mark it before editing.");
        if (sale.Status == SpareSaleStatus.Cancelled)
            return TypedResults.BadRequest("This sale is cancelled and can no longer be edited.");
        // Once a PI exists the figures have been sent to the customer, and an invoice is a tax document.
        if (!string.IsNullOrWhiteSpace(sale.InvNo))
            return TypedResults.BadRequest($"Invoice {sale.InvNo} has been raised for this sale.");
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
        if (sale.SoldAt is not null)
            return TypedResults.BadRequest(
                "The goods on this sale have left the warehouse — un-mark it as sold first, or record a return.");
        // Splitting stock off the document chain does not make a raised tax invoice cancellable.
        if (!string.IsNullOrWhiteSpace(sale.InvNo))
            return TypedResults.BadRequest($"Invoice {sale.InvNo} has been raised — this sale can no longer be cancelled.");

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
        if (sale.SoldAt is not null)
            return TypedResults.BadRequest("The goods on this sale have left the warehouse — un-mark it as sold first.");
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
        // A counter sale is paid or it is not. Partial exists on the shared enum for service jobs, which
        // are billed against a job that can be part-settled; a sale cannot be invoiced until it is Paid,
        // so Partial here is a state that can never move forward and only reads as "not paid yet".
        if (!Enum.TryParse<PaymentStatus>(req.Status, true, out var status)
            || status == PaymentStatus.Partial)
            return TypedResults.BadRequest($"Unknown payment status '{req.Status}'. Use Pending or Paid.");

        sale.PaymentStatus = status;
        user.TryGetUserId(out var uid);
        audit.Log(uid, "spare-sale.payment", "spare_sale", sale.Id,
            details: $"{sale.SaleNo} → {status}", ip: http.GetIp());
        await db.SaveChangesAsync(ct);

        return TypedResults.Ok((await BuildDetailAsync(db, sale.Id, SaleRoles.CanSeePricing(user), ct))!);
    }

    /// <summary>Hand the goods over: draw every line down from the warehouse and stamp the sale sold.
    ///
    /// This is the only path on a spare sale that moves stock. It is deliberately independent of the
    /// PI, the invoice and the payment status — a counter sale is often handed over and settled in cash
    /// with no paperwork at all, and a sale invoiced in advance should not empty the shelf before anyone
    /// has picked it.
    ///
    /// The decrement is guarded per line, so a part that sold out since the sale was entered fails the
    /// whole transaction rather than driving a balance negative. Quantities are grouped by part first:
    /// the same part can sit on two lines (list price and a discounted one) and each passing on its own
    /// would still overdraw the balance between them.
    ///
    /// Before any of that, the whole sale is re-checked against the warehouse and every short item is
    /// named at once. The guarded decrement alone is correct but it is a poor way to be told: it stops
    /// at the FIRST part that will not come out, so a sale short of three things had to be marked,
    /// refused, edited and marked again three times to discover that. Days can pass between entering a
    /// sale and handing it over, and the stock behind it moves in that gap. The check stays advisory —
    /// the conditional UPDATE underneath is still what makes overselling impossible, because anything
    /// read here can be gone by the time the row is written.</summary>
    private static async Task<Results<Ok<SpareSaleDetailDto>, NotFound, BadRequest<string>>> MarkSoldAsync(
        long id, ClaimsPrincipal user, AppDbContext db, StockLedgerService ledger,
        IAuditService audit, HttpContext http, CancellationToken ct)
    {
        var sale = await db.SpareSales.Include(s => s.Lines)
            .FirstOrDefaultAsync(s => s.Id == id && !s.IsDeleted, ct);
        if (sale is null) return TypedResults.NotFound();
        if (sale.SoldAt is not null) return TypedResults.BadRequest("This sale is already marked sold.");
        if (sale.Status == SpareSaleStatus.Cancelled) return TypedResults.BadRequest("This sale is cancelled.");
        if (sale.Lines.Count == 0) return TypedResults.BadRequest("This sale has no items on it.");

        // What this sale wants, one row per part — the same grouping the decrement below uses, so the
        // figure checked is the figure that will be asked for.
        var wanted = sale.Lines.GroupBy(l => l.PartId)
            .Select(g => (PartId: g.Key, g.First().ItemCode, Qty: g.Sum(l => l.Qty)))
            .ToList();
        var partIds = wanted.Select(w => w.PartId).ToList();
        var onHand = await db.StockBalances.AsNoTracking()
            .Where(b => b.TechnicianId == StockBalance.Warehouse && partIds.Contains(b.PartId))
            .ToDictionaryAsync(b => b.PartId, b => b.OnHand, ct);

        if (ShortfallRefusal(sale.SaleNo, wanted, onHand) is { } refusal)
            return TypedResults.BadRequest(refusal);

        user.TryGetUserId(out var uid);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            foreach (var g in sale.Lines.GroupBy(l => l.PartId))
                await ledger.SaleOutAsync(g.Key, g.First().ItemCode, g.Sum(l => l.Qty), uid, sale.Id,
                    $"Sold ({sale.SaleNo})", ct);

            sale.SoldAt = DateTime.UtcNow;
            sale.SoldByUserId = uid;
            // Handing the goods over IS the payment on a counter sale — it is settled in cash across the
            // counter, and the separate "mark payment received" step only restated what marking the sale
            // sold already meant. One action says both now, which is why the register no longer offers
            // the other one. The payment endpoint stays, for the rare sale that has to be corrected.
            var settledNow = sale.PaymentStatus != PaymentStatus.Paid;
            sale.PaymentStatus = PaymentStatus.Paid;
            audit.Log(uid, "spare-sale.mark-sold", "spare_sale", sale.Id,
                details: $"{sale.SaleNo} — {sale.Lines.Sum(l => l.Qty)} unit(s) out of the warehouse"
                         + (settledNow ? ", payment received" : string.Empty),
                ip: http.GetIp());
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (StockException ex)
        {
            await tx.RollbackAsync(ct);
            return TypedResults.BadRequest(ex.Message);
        }

        return TypedResults.Ok((await BuildDetailAsync(db, sale.Id, SaleRoles.CanSeePricing(user), ct))!);
    }

    /// <summary>Why this sale cannot be handed over, naming every item the warehouse is short of — or
    /// null when it can cover all of them. Separate from the endpoint so the rule can be exercised
    /// without a database.
    ///
    /// Measured against RAW on-hand, not availability. What other unsold sales have spoken for is not
    /// what stops this one: these units are leaving now, and the first sale to the counter is the one
    /// that gets them. Those claims belong on the entry form, where they stop two sales being written
    /// for the same goods in the first place.
    ///
    /// Every short item is listed rather than the first, because the point is to be told once what to
    /// edit. A part with no balance row at all reads as nothing in stock, which is what it is.</summary>
    internal static string? ShortfallRefusal(
        string saleNo, IEnumerable<(long PartId, string ItemCode, int Qty)> wanted,
        IReadOnlyDictionary<long, int> onHand)
    {
        var shortfalls = wanted
            .Where(w => w.Qty > onHand.GetValueOrDefault(w.PartId))
            .Select(w => $"  {w.ItemCode} — {onHand.GetValueOrDefault(w.PartId)} in warehouse stock, "
                       + $"{w.Qty} on this sale")
            .ToList();
        if (shortfalls.Count == 0) return null;

        return $"{saleNo} cannot be handed over — the warehouse no longer holds all of it:\n"
            + string.Join("\n", shortfalls)
            + "\n\nNothing has been taken out. Edit the sale down to what is there, or book the stock "
            + "in first, then mark it sold.";
    }

    /// <summary>Undo a Mark as sold that should not have happened, putting the units back on the shelf.
    ///
    /// Not a return: nothing reached the customer, so the sale keeps no return record and the ledger
    /// carries a SaleUnsold movement instead. Blocked once anything HAS come back as a return — at that
    /// point the two would fight over the same units, and the return is the record that describes what
    /// physically happened.</summary>
    private static async Task<Results<Ok<SpareSaleDetailDto>, NotFound, BadRequest<string>>> UnmarkSoldAsync(
        long id, ClaimsPrincipal user, AppDbContext db, StockLedgerService ledger,
        IAuditService audit, HttpContext http, CancellationToken ct)
    {
        var sale = await db.SpareSales.Include(s => s.Lines)
            .FirstOrDefaultAsync(s => s.Id == id && !s.IsDeleted, ct);
        if (sale is null) return TypedResults.NotFound();
        if (sale.SoldAt is null) return TypedResults.BadRequest("This sale is not marked sold.");
        if (await db.SpareSaleReturns.AnyAsync(r => r.SpareSaleId == sale.Id, ct))
            return TypedResults.BadRequest(
                "Items have already been returned against this sale — it can no longer be un-marked.");

        user.TryGetUserId(out var uid);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        foreach (var g in sale.Lines.GroupBy(l => l.PartId))
            await ledger.SaleUnsoldInAsync(g.Key, g.Sum(l => l.Qty), uid, sale.Id, $"Un-marked ({sale.SaleNo})", ct);

        sale.SoldAt = null;
        sale.SoldByUserId = null;
        // The mirror of mark-sold, which is now what records the payment too. Nothing on the register
        // sets a sale's payment status on its own any more, so undoing the hand-over has to take back
        // what it implied — otherwise a mis-click leaves a sale reading Paid with no way to correct it.
        // An invoiced sale keeps its Paid: that payment is on a tax document, not on this stamp.
        if (string.IsNullOrWhiteSpace(sale.InvNo)) sale.PaymentStatus = PaymentStatus.Pending;
        audit.Log(uid, "spare-sale.unmark-sold", "spare_sale", sale.Id,
            details: $"{sale.SaleNo} — {sale.Lines.Sum(l => l.Qty)} unit(s) back in the warehouse", ip: http.GetIp());
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return TypedResults.Ok((await BuildDetailAsync(db, sale.Id, SaleRoles.CanSeePricing(user), ct))!);
    }

    /// <summary>Un-stamp a PI so the sale can be corrected and re-quoted.
    ///
    /// The issued PI document is deliberately NOT deleted — it was sent to the customer, and a quote
    /// that vanishes from the record is worse than one that was superseded. Clearing only removes the
    /// stamp that locks editing, so the sale's document list keeps both the old PI and the new one.</summary>
    private static async Task<Results<Ok<SpareSaleDetailDto>, NotFound, BadRequest<string>>> ClearPiAsync(
        long id, ClaimsPrincipal user, AppDbContext db, IAuditService audit, HttpContext http, CancellationToken ct)
    {
        var sale = await db.SpareSales.FirstOrDefaultAsync(s => s.Id == id && !s.IsDeleted, ct);
        if (sale is null) return TypedResults.NotFound();
        if (string.IsNullOrWhiteSpace(sale.PiNo)) return TypedResults.BadRequest("This sale has no PI to clear.");
        if (sale.Status == SpareSaleStatus.Cancelled) return TypedResults.BadRequest("This sale is cancelled.");
        if (sale.Status == SpareSaleStatus.Invoiced)
            return TypedResults.BadRequest($"Invoice {sale.InvNo} has been raised — the PI can no longer be cleared.");

        var cleared = sale.PiNo;
        sale.PiNo = null;
        sale.PiDate = null;

        user.TryGetUserId(out var uid);
        audit.Log(uid, "spare-sale.clear-pi", "spare_sale", sale.Id,
            details: $"{sale.SaleNo} — cleared PI {cleared}; the document itself is kept", ip: http.GetIp());
        await db.SaveChangesAsync(ct);

        return TypedResults.Ok((await BuildDetailAsync(db, sale.Id, SaleRoles.CanSeePricing(user), ct))!);
    }

    /// <summary>Remember the courier details the generate form was given.
    ///
    /// The courier mode and charge belong to the DOCUMENT — this does not change what the sale bills,
    /// and no total here moves. It is the form's scratch pad: someone raising a PI types the courier,
    /// previews it, decides the figures need another look and closes it, and the next attempt used to
    /// start from an empty pair of boxes. Kept on the sale, they come back with it.
    ///
    /// No audit row: nothing commercial has happened yet, and one line per abandoned preview would
    /// bury the entries that record what was actually issued.</summary>
    private static async Task<Results<Ok<SpareSaleDetailDto>, NotFound, BadRequest<string>>> SaveCourierAsync(
        long id, [FromBody] SaveSaleCourierRequest req, ClaimsPrincipal user, AppDbContext db,
        CancellationToken ct)
    {
        var sale = await db.SpareSales.FirstOrDefaultAsync(s => s.Id == id && !s.IsDeleted, ct);
        if (sale is null) return TypedResults.NotFound();
        if (sale.Status == SpareSaleStatus.Cancelled) return TypedResults.BadRequest("This sale is cancelled.");

        var mode = req.CourierMode?.Trim();
        sale.CourierMode = string.IsNullOrWhiteSpace(mode) ? null : mode;
        sale.CourierCharges = req.CourierCharges;
        await db.SaveChangesAsync(ct);

        return TypedResults.Ok((await BuildDetailAsync(db, sale.Id, SaleRoles.CanSeePricing(user), ct))!);
    }

    /// <summary>Record goods coming back from a sale and put them back in the warehouse.
    ///
    /// Only a sale marked sold can be returned against, because that is the only state in which stock
    /// actually left. Quantities are capped at what was sold minus what has already come back, so
    /// repeated returns cannot inflate the warehouse past what went out.</summary>
    private static async Task<Results<Ok<SpareSaleDetailDto>, NotFound, BadRequest<string>>> CreateReturnAsync(
        long id, [FromBody] CreateSaleReturnRequest req, ClaimsPrincipal user, AppDbContext db,
        StockLedgerService ledger, NumberSequenceService seq, IAuditService audit,
        HttpContext http, CancellationToken ct)
    {
        var sale = await db.SpareSales.Include(s => s.Lines)
            .FirstOrDefaultAsync(s => s.Id == id && !s.IsDeleted, ct);
        if (sale is null) return TypedResults.NotFound();
        if (sale.SoldAt is null)
            return TypedResults.BadRequest(
                "This sale has not been marked sold — nothing has left the warehouse to come back.");
        if (string.IsNullOrWhiteSpace(req.Reason))
            return TypedResults.BadRequest("Give a reason for the return.");

        var soldPerPart = sale.Lines.GroupBy(l => l.PartId).ToDictionary(g => g.Key, g => g.Sum(l => l.Qty));
        var alreadyReturned = await (from rl in db.SpareSaleReturnLines.AsNoTracking()
                                     join r in db.SpareSaleReturns.AsNoTracking() on rl.SpareSaleReturnId equals r.Id
                                     where r.SpareSaleId == sale.Id
                                     group rl by rl.PartId into g
                                     select new { PartId = g.Key, Qty = g.Sum(x => x.Qty) })
            .ToDictionaryAsync(x => x.PartId, x => x.Qty, ct);

        // Fold duplicate lines for the same part before checking, so two rows of 3 cannot slip past a
        // cap of 5 by being under it individually.
        var asked = new Dictionary<long, int>();
        foreach (var l in req.Lines)
        {
            if (l.Qty < 1) return TypedResults.BadRequest("Return quantity must be at least 1.");
            asked[l.PartId] = asked.GetValueOrDefault(l.PartId) + l.Qty;
        }

        foreach (var (partId, qty) in asked)
        {
            if (!soldPerPart.TryGetValue(partId, out var sold))
                return TypedResults.BadRequest("A returned item is not on this sale.");
            var remaining = sold - alreadyReturned.GetValueOrDefault(partId);
            if (qty > remaining)
            {
                var code = sale.Lines.First(l => l.PartId == partId).ItemCode;
                return TypedResults.BadRequest(remaining <= 0
                    ? $"{code} has already been returned in full."
                    : $"{code} — {remaining} of {sold} still outstanding, {qty} being returned.");
            }
        }

        user.TryGetUserId(out var uid);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        SpareSaleReturn ret;
        try
        {
            ret = new SpareSaleReturn
            {
                SpareSaleId = sale.Id,
                ReturnDate = ShopClock.BusinessDate(req.ReturnDate) ?? ShopClock.Today,
                Reason = req.Reason.Trim(),
                CreatedByUserId = uid,
                // Throws when the SPARE_SALE_RETURN sequence row is missing, which is a deployment
                // problem rather than a bad request — caught below so it reads as a message instead
                // of a 500 with nothing in it for the user.
                ReturnNo = await seq.NextAsync(SequenceKeys.SpareSaleReturn, ct),
            };
            foreach (var (partId, qty) in asked)
                ret.Lines.Add(new SpareSaleReturnLine
                {
                    PartId = partId,
                    ItemCode = sale.Lines.First(l => l.PartId == partId).ItemCode,
                    Qty = qty,
                });
            db.SpareSaleReturns.Add(ret);

            // Needs the id for the ledger's reference, and the stock move belongs to the same transaction.
            await db.SaveChangesAsync(ct);
            foreach (var l in ret.Lines)
                await ledger.SaleReturnInAsync(l.PartId, l.Qty, uid, ret.Id, $"{ret.ReturnNo} ({sale.SaleNo})", ct);

            audit.Log(uid, "spare-sale.return", "spare_sale", sale.Id,
                details: $"{sale.SaleNo} — {ret.ReturnNo}, {ret.Lines.Sum(l => l.Qty)} unit(s) back: {ret.Reason}",
                ip: http.GetIp());
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (StockException ex)
        {
            await tx.RollbackAsync(ct);
            return TypedResults.BadRequest(ex.Message);
        }

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

        // What the sale itself carries wins: it is what the counter entered and what the documents were
        // raised on, and a master edited since then must not rewrite it — the same rule a line's
        // snapshotted item code and HSN follow. A sale entered before these columns existed carries
        // none of them and falls through to the master, exactly as every sale used to.
        address = sale.PartyAddress ?? address;
        state = sale.PartyState ?? state;
        stateCode = sale.PartyStateCode ?? stateCode;

        var createdBy = await db.Users.AsNoTracking().Where(u => u.Id == sale.CreatedByUserId)
            .Select(u => (string?)(u.FullName ?? u.Username)).FirstOrDefaultAsync(ct);
        var soldBy = sale.SoldByUserId is { } sbid
            ? await db.Users.AsNoTracking().Where(u => u.Id == sbid)
                .Select(u => (string?)(u.FullName ?? u.Username)).FirstOrDefaultAsync(ct)
            : null;

        // Stock and returns are fetched for the whole sale at once. Asking per line meant a round trip
        // per row every time a sale was opened.
        var partIds = sale.Lines.Select(l => l.PartId).Distinct().ToList();
        var onHand = await new SpareSaleService(db).WarehouseOnHandAsync(partIds, ct);

        var returned = await (from rl in db.SpareSaleReturnLines.AsNoTracking()
                              join r in db.SpareSaleReturns.AsNoTracking() on rl.SpareSaleReturnId equals r.Id
                              where r.SpareSaleId == sale.Id
                              group rl by rl.PartId into g
                              select new { PartId = g.Key, Qty = g.Sum(x => x.Qty) })
            .ToDictionaryAsync(x => x.PartId, x => x.Qty, ct);

        var lines = sale.Lines.OrderBy(l => l.Id).Select(l =>
        {
            var have = onHand.GetValueOrDefault(l.PartId);
            return new SpareSaleLineDto(
                l.Id, l.PartId, l.ItemCode, l.Description, l.HsnCode, l.Unit, l.Qty, l.RateType.ToString(),
                pricing ? l.UnitRate : null, l.GstPercent,
                pricing ? l.TaxableAmount : null, pricing ? l.TaxAmount : null, pricing ? l.LineTotal : null,
                // Available is the same figure — see SpareSaleLineDto.
                have, have, returned.GetValueOrDefault(l.PartId));
        }).ToList();

        var returns = await db.SpareSaleReturns.AsNoTracking().Include(r => r.Lines)
            .Where(r => r.SpareSaleId == sale.Id).OrderByDescending(r => r.Id).ToListAsync(ct);
        var returnUserNames = await db.Users.AsNoTracking()
            .Where(u => returns.Select(r => r.CreatedByUserId).Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.FullName ?? u.Username, ct);
        var returnDtos = returns.Select(r => new SaleReturnDto(
            r.Id, r.ReturnNo, r.ReturnDate, r.Reason,
            returnUserNames.GetValueOrDefault(r.CreatedByUserId), r.CreatedAt,
            r.Lines.OrderBy(l => l.Id).Select(l => new SaleReturnLineDto(l.PartId, l.ItemCode, l.Qty)).ToList())).ToList();

        return new SpareSaleDetailDto(
            sale.Id, sale.SaleNo, sale.SaleDate, sale.CustomerType, sale.DealerId, sale.CustomerId,
            partyName, address, gstin, state, stateCode,
            sale.Status.ToString(), sale.PaymentStatus.ToString(),
            sale.PiNo, sale.PiDate, sale.InvNo, sale.InvDate,
            pricing ? sale.TaxableAmount : null, pricing ? sale.TaxAmount : null, pricing ? sale.TotalAmount : null,
            sale.Remarks, createdBy, sale.CreatedAt, lines, returnDtos, sale.SoldAt, soldBy,
            sale.CourierMode, pricing ? sale.CourierCharges : null);
    }
}
