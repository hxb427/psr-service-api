using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
// EmptyBodyBehavior, for mark-sold: a sale with nothing serial-tracked on it is marked sold with no
// body at all, the way it always was.
using Microsoft.AspNetCore.Mvc.ModelBinding;
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
    /// the rates stay hidden, matching how parts pricing is stripped everywhere else.
    ///
    /// sales is here because a counter sale IS a price: strip the rates and the register lists sales with
    /// no totals and a reopened sale comes back with empty money on every line. This list is the gate on
    /// the sale's own figures; the rate that lands on a new line when a part is picked comes from the
    /// parts master instead, so <see cref="Auth.PricingVisibility"/> has to admit the role as well. Both,
    /// or the form is half-priced.</summary>
    public static readonly string[] Pricing =
        { RoleNames.Admin, RoleNames.Manager, RoleNames.Supervisor, RoleNames.Viewer, RoleNames.Accounts,
          RoleNames.Sales };

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
        // The one action that moves warehouse stock on a sale. Gated on SaleMarkSold rather than
        // SaleManage: it is a warehouse movement, and the store manager who hands the goods over is
        // the person who knows they went out.
        //
        // SaleMarkSold is the StockManage roles plus sales, and exists only for these two routes.
        // StockManage is what they sat on until the sales desk arrived, and it was too wide to admit a
        // counter role to — it carries direct-issue, transfers and stock adjustments — while SaleManage
        // is the wrong gate for a stock movement. For every role that had the button before, the two
        // policies are the same list.
        //
        // It now marks the sale paid as well, which means this policy settles a counter sale's payment
        // without PaymentManage. That is deliberate and not a hole: the person handing the goods across
        // the counter is the person taking the cash for them, and the register no longer offers a
        // separate payment step for anyone else to take. /payment below still needs PaymentManage, for
        // the sale that has to be corrected afterwards.
        group.MapPost("/{id:long}/mark-sold", MarkSoldAsync).RequireAuthorization("SaleMarkSold");
        group.MapPost("/{id:long}/unmark-sold", UnmarkSoldAsync).RequireAuthorization("SaleMarkSold");
        group.MapPost("/{id:long}/clear-pi", ClearPiAsync).RequireAuthorization("SaleManage");
        // Gated on DocumentManage rather than SaleManage: it is the generate form's own scratch pad,
        // and accounts raises documents without being able to edit the sale behind them.
        group.MapPost("/{id:long}/courier", SaveCourierAsync).RequireAuthorization("DocumentManage");
        group.MapPost("/{id:long}/returns", CreateReturnAsync).RequireAuthorization("SaleManage");
        // Asked per row by the sale form as the user types, so it stays a single-part lookup.
        group.MapGet("/availability/{partId:long}", AvailabilityAsync);
        // There are no serial routes beside these. A sale is entered as quantities and names its units
        // in the mark-sold body, so there is no window in which a serial sits on an unsold sale — and
        // therefore nothing for a pre-submit conflict check to find, and no claim on another sale to
        // release. The one serial rule left is "not already tracked", which mark-sold applies itself.

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

    // ---------------------------------------------------------------- serials

    /// <summary>The serials a sale's lines carry, line id → serials in entry order.</summary>
    private static async Task<Dictionary<long, List<string>>> SerialsByLineAsync(
        AppDbContext db, IReadOnlyCollection<long> lineIds, CancellationToken ct)
    {
        if (lineIds.Count == 0) return new Dictionary<long, List<string>>();
        var rows = await db.SpareSaleLineSerials.AsNoTracking()
            .Where(s => lineIds.Contains(s.SpareSaleLineId))
            .OrderBy(s => s.Id)
            .Select(s => new { s.SpareSaleLineId, s.SerialNumber })
            .ToListAsync(ct);
        return rows.GroupBy(r => r.SpareSaleLineId)
            .ToDictionary(g => g.Key, g => g.Select(r => r.SerialNumber).ToList());
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
        var sale = await db.SpareSales.Include(s => s.Lines).ThenInclude(l => l.Serials)
            .FirstOrDefaultAsync(s => s.Id == id && !s.IsDeleted, ct);
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
            // The serial claims go with the lines they hung off, explicitly rather than by letting the
            // database cascade do it: the rows this edit writes are keyed on the same (part, serial)
            // pairs, and a delete the context does not know about leaves EF free to insert the new
            // claim before the old one is gone.
            db.SpareSaleLineSerials.RemoveRange(sale.Lines.SelectMany(l => l.Serials));
            db.SpareSaleLines.RemoveRange(sale.Lines);
            await db.SaveChangesAsync(ct);

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
        // Nothing to do about serials. A sale can only be cancelled while it is unsold, and an unsold
        // sale holds none — they arrive with mark-sold.
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
        // Same as cancel: only an unsold sale can be removed, and an unsold sale names no units.
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
    /// read here can be gone by the time the row is written.
    ///
    /// <b>This is also where a serial-tracked item is named.</b> The sale itself carries quantities
    /// only: the counter does not know which boxes it will pull when the sale is written, and a sale
    /// that is never completed should name nothing. So the units arrive in the body of THIS request,
    /// one serial per unit per tracked line, and in one transaction they are written onto the sale,
    /// created in the serial ledger as deployed at the party, and debited off the warehouse. All of it
    /// or none of it, which is the only way the quantity and the ledger can agree about a sale
    /// afterwards. See <see cref="Data.Entities.SpareSaleLineSerial"/>.
    ///
    /// Naming the units here rather than at entry is also what removes the duplicate problem instead
    /// of policing it. A serial reaches a sale and the ledger in the same instant, so there is no
    /// window in which two unsold sales hold the same number: the second counter to try it is refused
    /// by <see cref="SerialRefusalAsync"/>, which says where the unit already is.</summary>
    private static async Task<Results<Ok<SpareSaleDetailDto>, NotFound, BadRequest<string>>> MarkSoldAsync(
        long id, [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] MarkSaleSoldRequest? req,
        ClaimsPrincipal user, AppDbContext db, StockLedgerService ledger,
        SerialService serials, IAuditService audit, HttpContext http, CancellationToken ct)
    {
        var sale = await db.SpareSales.Include(s => s.Lines).ThenInclude(l => l.Serials)
            .FirstOrDefaultAsync(s => s.Id == id && !s.IsDeleted, ct);
        if (sale is null) return TypedResults.NotFound();
        if (sale.SoldAt is not null) return TypedResults.BadRequest("This sale is already marked sold.");
        if (sale.Status == SpareSaleStatus.Cancelled) return TypedResults.BadRequest("This sale is cancelled.");
        if (sale.Lines.Count == 0) return TypedResults.BadRequest("This sale has no items on it.");

        // The units being handed over, per line, trimmed — and everything wrong with them named at
        // once, before any stock moves. A tracked line with no serials against it is refused here
        // rather than silently handed over untracked, which is the whole point of the change: a sale
        // of a tracked item cannot complete without saying which boxes went.
        //
        // The body is optional so a sale with nothing tracked on it is still the one-click action it
        // always was, and so an older client — which sends no body at all — keeps working for exactly
        // those sales and is refused, by name, for the ones it cannot answer for.
        var byLine = (req?.Lines ?? [])
            .GroupBy(l => l.LineId)
            .ToDictionary(g => g.Key, g => SpareSaleService.Normalize(g.SelectMany(l => l.Serials)));

        if (await SerialRefusalAsync(db, sale, byLine, ct) is { } serialRefusal)
            return TypedResults.BadRequest(serialRefusal);

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
        var partyName = await PartyNameAsync(db, sale, ct);
        // Which parts are tracked, and their own names. The NAME is taken from the master rather than
        // from the line's description: the description defaults to it but is editable per line, and a
        // serial record labelled with one counter's wording for an item is a record the SN screen lists
        // under a name no other screen uses.
        var parts = await db.Parts.AsNoTracking().Where(p => partIds.Contains(p.Id))
            .Select(p => new { p.Id, p.Name, p.IsSerialTracked })
            .ToDictionaryAsync(p => p.Id, p => p, ct);
        var trackedParts = parts.ToDictionary(p => p.Key, p => p.Value.IsSerialTracked);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            foreach (var g in sale.Lines.GroupBy(l => l.PartId))
                await ledger.SaleOutAsync(g.Key, g.First().ItemCode, g.Sum(l => l.Qty), uid, sale.Id,
                    $"Sold ({sale.SaleNo})", ct);

            // The units are written onto the sale and into the ledger together, because the two
            // statements are the same fact: this box went to this party on this sale. Owner CUSTOMER
            // and status USED, which is the state the enum already describes for a unit sold across
            // the counter — the same end state a unit fitted on a job or handed over as a replacement
            // reaches, because where the unit is is the same fact however it got there. A dealer sale
            // carries the dealer's name as the owner label and no customer id: there is no row in the
            // customers table to point at, and OwnerRef is what every screen reads.
            // Anything already on the lines is cleared first. A sale written on the previous build was
            // entered WITH its serials — they were claims then — and those rows mean "went out" now, so
            // adding this hand-over's numbers beside them would say twice as many units left as did.
            // Ordinarily there is nothing to clear: an unsold sale names no units.
            if (sale.Lines.Any(l => l.Serials.Count > 0))
            {
                db.SpareSaleLineSerials.RemoveRange(sale.Lines.SelectMany(l => l.Serials));
                foreach (var line in sale.Lines) line.Serials.Clear();
                await db.SaveChangesAsync(ct);
            }

            // Only a TRACKED line's numbers are written, which is what makes serials sent for an
            // untracked one ignored rather than refused: the part may have had tracking turned off
            // between the sale being written and the counter answering the prompt, and refusing then
            // would leave a sale nobody could complete. Writing them instead would be worse — a unit
            // record for a part the shop does not track by unit, on no screen that lists it.
            var tracked = 0;
            foreach (var line in sale.Lines.Where(l => trackedParts.GetValueOrDefault(l.PartId)))
                foreach (var sn in byLine.GetValueOrDefault(line.Id) ?? [])
                {
                    line.Serials.Add(new SpareSaleLineSerial { PartId = line.PartId, SerialNumber = sn });
                    await serials.InstallToCustomerAsync(line.PartId, sn,
                        parts.GetValueOrDefault(line.PartId)?.Name, partyName, uid, ct, sale.CustomerId);
                    tracked++;
                }

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
                         + (tracked > 0 ? $", {tracked} serial(s) now tracked at {partyName}" : string.Empty)
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
        catch (DbUpdateException) when (sale.Lines.Any(l => l.Serials.Count > 0))
        {
            // The last line of defence on a serial, and the only thing that can reach it is a true
            // race: two counters marking sales sold for the same unit in the same instant, both past
            // the check above. The unique index on component_serials (part_id, serial_number) is what
            // stops the second one, and this turns that into a sentence instead of a 500 — on the one
            // page where "it went out twice" is the thing that must never quietly happen.
            await tx.RollbackAsync(ct);
            return TypedResults.BadRequest(
                $"{sale.SaleNo} was not handed over: one of its serial numbers was taken by another "
                + "sale at the same moment. Nothing has been taken out and nothing has been tracked — "
                + "open the sale, check which unit is already tracked, and name a different box.");
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

    /// <summary>Why the units named on this hand-over cannot be taken, listing every one that is wrong
    /// — or null when they can all be taken.
    ///
    /// Three rules, and between them they are the whole of what "serial-tracked" means on a sale:
    ///
    /// <list type="bullet">
    /// <item><b>One serial per unit, on every tracked line.</b> A line for 5 of a tracked part needs 5
    /// numbers. This is what makes the sale traceable at all, so there is no completing the sale
    /// without it — and a tracked line answered with nothing is refused by name rather than quietly
    /// handed over untracked, which is what a sale written before this was asked for would otherwise
    /// do.</item>
    /// <item><b>No number twice.</b> Within a line, and across the lines of the same part — a part can
    /// sit on two rows (list price and a discounted one) and one box named on both is one unit sold
    /// twice. Across different parts is fine: a serial is only ever unique within its part.</item>
    /// <item><b>Nothing already in the ledger.</b> A unit the shop knows about — with a technician, at
    /// a customer, on the shelf, scrapped — is not warehouse stock, so it is not what is being sold
    /// here, and this is where a serial that another counter sold a moment ago is caught. There is no
    /// override: the ledger says where that unit is, and selling it again from the warehouse would mean
    /// the shop had two of it. The message says where it is, so a mistyped number can be told apart
    /// from a real one.</item>
    /// </list>
    ///
    /// Serials sent for a line that is NOT tracked are ignored rather than refused. The part may have
    /// had tracking turned off between the sale being written and the counter answering the prompt, and
    /// refusing then would leave a sale nobody could complete; the numbers simply have nowhere to go.
    ///
    /// Everything wrong is listed at once, for the same reason the shortfall message lists every short
    /// item: the point is to be told once what to fix.</summary>
    private static async Task<string?> SerialRefusalAsync(
        AppDbContext db, SpareSale sale, IReadOnlyDictionary<long, List<string>> byLine,
        CancellationToken ct)
    {
        var partIds = sale.Lines.Select(l => l.PartId).Distinct().ToList();
        var tracked = await db.Parts.AsNoTracking().Where(p => partIds.Contains(p.Id))
            .Select(p => new { p.Id, p.IsSerialTracked }).ToDictionaryAsync(p => p.Id, p => p.IsSerialTracked, ct);

        var trackedLines = sale.Lines.Where(l => tracked.GetValueOrDefault(l.PartId)).ToList();

        // Counted per part, not per line: the same part on two rows is one pool of units, and 2 + 3 of
        // it needs 5 numbers between them however they are split up. A line the prompt never answered
        // contributes nothing, which is what makes it show up as short.
        var problems = SerialCountProblems(trackedLines
            .GroupBy(l => l.PartId)
            .Select(g => (g.First().ItemCode,
                          Qty: g.Sum(l => l.Qty),
                          Serials: g.Sum(l => (byLine.GetValueOrDefault(l.Id) ?? []).Count))));

        // Same part, every number given for it — this is the grouping a duplicate has to be found in,
        // since two rows of one part draw on one pool.
        foreach (var g in trackedLines.GroupBy(l => l.PartId))
        {
            var forPart = g.SelectMany(l => byLine.GetValueOrDefault(l.Id) ?? []).ToList();
            foreach (var dupe in forPart.GroupBy(s => s, StringComparer.OrdinalIgnoreCase)
                         .Where(x => x.Count() > 1).Select(x => x.Key))
                problems.Add($"  {g.First().ItemCode} {dupe} — entered more than once. One unit cannot "
                           + "be sold twice; check whether a box was scanned twice.");
        }

        var given = trackedLines
            .SelectMany(l => (byLine.GetValueOrDefault(l.Id) ?? []).Select(sn => (l.ItemCode, l.PartId, Sn: sn)))
            .ToList();
        if (given.Count > 0)
        {
            // Fetched per part and matched in memory: MySQL's own collation would otherwise decide
            // whether "psr-01" and "PSR-01" are the same unit, and that is the one thing that must not
            // vary. They are.
            var existing = (await db.ComponentSerials.AsNoTracking()
                    .Where(c => partIds.Contains(c.PartId))
                    .Select(c => new { c.PartId, c.SerialNumber, c.Status, c.OwnerRef })
                    .ToListAsync(ct))
                .GroupBy(c => (c.PartId, c.SerialNumber.ToUpperInvariant()))
                .ToDictionary(g => g.Key, g => g.First());

            foreach (var (itemCode, partId, sn) in given)
                if (existing.TryGetValue((partId, sn.ToUpperInvariant()), out var unit))
                    problems.Add($"  {itemCode} {sn} — already tracked: this unit is "
                               + (unit.OwnerRef is { Length: > 0 } who ? $"with {who}" : "at the service centre")
                               + $" (status {unit.Status}). It is not warehouse stock, so it cannot be "
                               + "sold here — read the serial off a different box.");
        }

        if (problems.Count == 0) return null;

        return $"{sale.SaleNo} cannot be handed over — its serial numbers do not add up:\n"
            + string.Join("\n", problems)
            + "\n\nNothing has been taken out and nothing has been tracked.";
    }

    /// <summary>Which serial-tracked parts on a sale do not have one serial per unit, one line each.
    /// Empty when they all do.
    ///
    /// Separate from the endpoint, and taking counts rather than entities, for the same reason
    /// <see cref="ShortfallRefusal"/> is: this is the rule that decides whether a sale of tracked stock
    /// may be handed over, and it should be exercisable without a database behind it.
    ///
    /// Not equal, rather than fewer than. Answering with MORE numbers than units is just as wrong and
    /// in a more interesting way — a prompt sized to 5 that came back with 6 means one box in the pile
    /// is not on this sale.</summary>
    internal static List<string> SerialCountProblems(
        IEnumerable<(string ItemCode, int Qty, int Serials)> trackedParts) =>
        trackedParts
            .Where(t => t.Serials != t.Qty)
            .Select(t => $"  {t.ItemCode} — {t.Qty} being sold, {t.Serials} serial number(s) given. "
                       + "It is serial-tracked: read one serial off each box being handed over.")
            .ToList();

    /// <summary>Who the sale is billed to, as a label for the units it hands over. The party master is
    /// read rather than the sale, because this is a display name on a serial record and not a figure
    /// the sale was billed on — and a sale carries no party name of its own anyway.</summary>
    private static async Task<string> PartyNameAsync(AppDbContext db, SpareSale sale, CancellationToken ct)
    {
        if (sale.DealerId is { } did)
            return await db.Dealers.AsNoTracking().Where(d => d.Id == did).Select(d => d.Name)
                .FirstOrDefaultAsync(ct) ?? sale.SaleNo;
        if (sale.CustomerId is { } cid)
            return await db.Customers.AsNoTracking().Where(c => c.Id == cid).Select(c => c.Name)
                .FirstOrDefaultAsync(ct) ?? sale.SaleNo;
        return sale.SaleNo;
    }

    /// <summary>Undo a Mark as sold that should not have happened, putting the units back on the shelf.
    ///
    /// Not a return: nothing reached the customer, so the sale keeps no return record and the ledger
    /// carries a SaleUnsold movement instead. Blocked once anything HAS come back as a return — at that
    /// point the two would fight over the same units, and the return is the record that describes what
    /// physically happened.
    ///
    /// <b>No longer reachable from the register.</b> The Undo sale button is gone from the sales page,
    /// because a counter sale marked sold has been handed over and taking it back silently was too easy
    /// to do by accident — the confirmation on Mark as sold is where a mis-click is meant to be caught
    /// now. The endpoint stays as the correction of last resort, and it is kept CORRECT rather than
    /// merely left: a sale that put units into serial tracking takes them back out of it here, or it
    /// refuses. An endpoint that undid the quantity and left the serial ledger saying the units are at
    /// a customer would be worse than no endpoint at all.</summary>
    private static async Task<Results<Ok<SpareSaleDetailDto>, NotFound, BadRequest<string>>> UnmarkSoldAsync(
        long id, ClaimsPrincipal user, AppDbContext db, StockLedgerService ledger,
        SerialService serials, IAuditService audit, HttpContext http, CancellationToken ct)
    {
        var sale = await db.SpareSales.Include(s => s.Lines).ThenInclude(l => l.Serials)
            .FirstOrDefaultAsync(s => s.Id == id && !s.IsDeleted, ct);
        if (sale is null) return TypedResults.NotFound();
        if (sale.SoldAt is null) return TypedResults.BadRequest("This sale is not marked sold.");
        if (await db.SpareSaleReturns.AnyAsync(r => r.SpareSaleId == sale.Id, ct))
            return TypedResults.BadRequest(
                "Items have already been returned against this sale — it can no longer be un-marked.");

        // Every tracked unit is checked before anything is written, so a sale with one unit that has
        // moved on is refused whole rather than half-undone.
        var sold = sale.Lines.SelectMany(l => l.Serials.Select(s => (l.ItemCode, s.PartId, s.SerialNumber)))
            .ToList();
        var blocked = new List<string>();
        foreach (var (itemCode, partId, sn) in sold)
        {
            var state = await serials.SaleWithdrawalStateAsync(partId, sn, ct);
            if (state is null || state.Value.Withdrawable) continue;
            blocked.Add($"  {itemCode} {sn} — status {state.Value.Status}"
                + (state.Value.OwnerRef is { Length: > 0 } who ? $", with {who}" : string.Empty));
        }
        if (blocked.Count > 0)
            return TypedResults.BadRequest(
                $"{sale.SaleNo} can no longer be un-marked — something has happened to the units since "
                + "it was sold:\n" + string.Join("\n", blocked)
                + "\n\nThose records are where those units actually are, and undoing the sale would "
                + "erase them. Record a return against the sale instead, or correct the units from the "
                + "SN management screen.");

        user.TryGetUserId(out var uid);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        foreach (var g in sale.Lines.GroupBy(l => l.PartId))
            await ledger.SaleUnsoldInAsync(g.Key, g.Sum(l => l.Qty), uid, sale.Id, $"Un-marked ({sale.SaleNo})", ct);

        // The sale goes back to being a sale of quantities. Both halves of what mark-sold wrote are
        // undone — the unit's record in the serial ledger, and the row saying it went out on this line
        // — because an unsold sale names no units, and leaving the numbers behind would make this the
        // one sale on the register that disagreed. Marking it sold again asks for them afresh, which is
        // right: the boxes are back on the shelf and the next hand-over may well pull different ones.
        foreach (var (_, partId, sn) in sold)
            await serials.WithdrawSaleUnitAsync(partId, sn, ct);
        db.SpareSaleLineSerials.RemoveRange(sale.Lines.SelectMany(l => l.Serials));

        sale.SoldAt = null;
        sale.SoldByUserId = null;
        // The mirror of mark-sold, which is now what records the payment too. Nothing on the register
        // sets a sale's payment status on its own any more, so undoing the hand-over has to take back
        // what it implied — otherwise a mis-click leaves a sale reading Paid with no way to correct it.
        // An invoiced sale keeps its Paid: that payment is on a tax document, not on this stamp.
        if (string.IsNullOrWhiteSpace(sale.InvNo)) sale.PaymentStatus = PaymentStatus.Pending;
        audit.Log(uid, "spare-sale.unmark-sold", "spare_sale", sale.Id,
            details: $"{sale.SaleNo} — {sale.Lines.Sum(l => l.Qty)} unit(s) back in the warehouse"
                     + (sold.Count > 0
                         ? $", {sold.Count} serial(s) taken back out of tracking: "
                           + string.Join(", ", sold.Select(s => s.SerialNumber))
                         : string.Empty),
            ip: http.GetIp());
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
        // Fetched for the whole sale, like the stock and the returns above it — the alternative is a
        // round trip per row every time a sale is opened.
        var serialsByLine = await SerialsByLineAsync(db, sale.Lines.Select(l => l.Id).ToList(), ct);
        var trackedParts = await db.Parts.AsNoTracking().Where(p => partIds.Contains(p.Id))
            .Select(p => new { p.Id, p.IsSerialTracked }).ToDictionaryAsync(p => p.Id, p => p.IsSerialTracked, ct);

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
                have, have, returned.GetValueOrDefault(l.PartId),
                // Not stripped for a non-pricing role: a store manager picking and packing the sale is
                // exactly who needs to read the serials off the boxes. It is the money that is hidden
                // from them, not what they are handing over.
                trackedParts.GetValueOrDefault(l.PartId),
                serialsByLine.GetValueOrDefault(l.Id) ?? new List<string>());
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
