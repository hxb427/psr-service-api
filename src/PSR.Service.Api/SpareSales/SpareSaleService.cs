using Microsoft.EntityFrameworkCore;
using PSR.Service.Api.Audit;
using PSR.Service.Api.Data;
using PSR.Service.Api.Data.Entities;

namespace PSR.Service.Api.SpareSales;

/// <summary>A sale was rejected for a business reason (bad party, unpriced item, not enough stock).
/// Surfaces as a 400 with the message shown to the user.</summary>
public class SaleValidationException(string message) : Exception(message);

/// <summary>Party resolution, line pricing and totals for a spare sale. Shared by create and edit so a
/// sale priced at entry and a sale re-priced after an edit go through exactly the same rules.</summary>
public class SpareSaleService(AppDbContext db)
{
    /// <summary>Overwrite a sale's party and lines from a request, re-pricing every line and recomputing
    /// totals. Call inside a transaction — resolving a walk-in customer may insert a customer row.</summary>
    public async Task ApplyAsync(SpareSale sale, SaveSpareSaleRequest req, long userId,
        IAuditService audit, string? ip, CancellationToken ct)
    {
        var isDealer = await ApplyPartyAsync(sale, req, userId, audit, ip, ct);

        sale.SaleDate = req.SaleDate ?? DateTime.UtcNow;
        sale.Remarks = req.Remarks?.Trim();

        sale.Lines.Clear();
        // A part may legitimately appear twice (part at list price, part discounted), so availability is
        // checked against the TOTAL asked for that part, not line by line.
        var askedPerPart = new Dictionary<long, int>();

        foreach (var lineReq in req.Lines)
        {
            if (lineReq.Qty < 1) throw new SaleValidationException("Quantity must be at least 1.");

            var part = await db.Parts.AsNoTracking().FirstOrDefaultAsync(p => p.Id == lineReq.PartId, ct)
                ?? throw new SaleValidationException($"Part {lineReq.PartId} was not found.");
            if (!part.IsActive)
                throw new SaleValidationException($"{part.ItemCode} is inactive and cannot be sold.");

            var (rateType, unitRate) = ResolveRate(part, lineReq, isDealer);

            askedPerPart[part.Id] = askedPerPart.GetValueOrDefault(part.Id) + lineReq.Qty;
            // Checked against AVAILABLE, not raw on-hand: units already spoken for by other pending
            // sales are not ours to sell. Without this two sales could each be entered for the last of
            // something, both be marked paid, and the second only fail at invoicing — after the money.
            var avail = await AvailabilityAsync(part.Id, sale.Id, ct);
            if (askedPerPart[part.Id] > avail.Available)
                throw new SaleValidationException(
                    $"{part.ItemCode} — {avail.Available} available, {askedPerPart[part.Id]} asked for"
                    + (avail.Committed > 0
                        ? $" ({avail.OnHand} in stock, {avail.Committed} on other pending sales)."
                        : "."));

            var taxable = Math.Round(unitRate * lineReq.Qty, 2);
            var tax = Math.Round(taxable * part.GstPercent / 100m, 2);

            sale.Lines.Add(new SpareSaleLine
            {
                PartId = part.Id,
                ItemCode = part.ItemCode,
                Description = string.IsNullOrWhiteSpace(lineReq.Description) ? part.Name : lineReq.Description.Trim(),
                HsnCode = part.HsnCode,
                Unit = part.Unit,
                Qty = lineReq.Qty,
                RateType = rateType,
                UnitRate = unitRate,
                GstPercent = part.GstPercent,
                TaxableAmount = taxable,
                TaxAmount = tax,
                LineTotal = taxable + tax,
            });
        }

        sale.TaxableAmount = sale.Lines.Sum(l => l.TaxableAmount);
        sale.TaxAmount = sale.Lines.Sum(l => l.TaxAmount);
        sale.TotalAmount = sale.TaxableAmount + sale.TaxAmount;
    }

    /// <summary>Sets the sale's party. Returns true when billing a dealer (which picks the dealer rate column).</summary>
    private async Task<bool> ApplyPartyAsync(SpareSale sale, SaveSpareSaleRequest req, long userId,
        IAuditService audit, string? ip, CancellationToken ct)
    {
        var type = req.CustomerType?.Trim() ?? string.Empty;

        if (string.Equals(type, "Dealer", StringComparison.OrdinalIgnoreCase))
        {
            if (req.DealerId is not { } did)
                throw new SaleValidationException("Choose the dealer being billed.");
            if (!await db.Dealers.AnyAsync(d => d.Id == did, ct))
                throw new SaleValidationException("That dealer was not found.");

            sale.CustomerType = "Dealer";
            sale.DealerId = did;
            sale.CustomerId = null;
            return true;
        }

        if (string.Equals(type, "Direct", StringComparison.OrdinalIgnoreCase))
        {
            var customerId = await Services.ServicesEndpoints.ResolveCustomerAsync(
                db, req.CustomerId, req.CustomerName, null, req.Phone, null, req.Address,
                ct, audit, userId, ip, origin: "sale");
            if (customerId is null)
                throw new SaleValidationException("Name the customer being billed.");

            sale.CustomerType = "Direct";
            sale.CustomerId = customerId;
            sale.DealerId = null;
            return false;
        }

        throw new SaleValidationException($"Unknown customer type '{req.CustomerType}'. Use Dealer or Direct.");
    }

    /// <summary>An explicit rate is always honoured (and recorded as Manual); otherwise the rate comes from
    /// the parts master, defaulting to the dealer column for a dealer sale and the customer column otherwise.</summary>
    private static (SaleRateType, decimal) ResolveRate(Part part, SpareSaleLineRequest lineReq, bool isDealer)
    {
        if (lineReq.UnitRate is { } manual)
        {
            if (manual <= 0) throw new SaleValidationException($"Enter a rate above zero for {part.ItemCode}.");
            return (SaleRateType.Manual, manual);
        }

        var requested = Enum.TryParse<SaleRateType>(lineReq.RateType, true, out var parsed)
            ? parsed
            : isDealer ? SaleRateType.Dealer : SaleRateType.Customer;

        // Manual with no rate supplied is meaningless — fall back to the party's default column.
        if (requested == SaleRateType.Manual) requested = isDealer ? SaleRateType.Dealer : SaleRateType.Customer;

        var rate = requested == SaleRateType.Dealer ? part.DealerRate : part.CustomerRate;
        if (rate <= 0)
            throw new SaleValidationException(
                $"No {requested.ToString().ToLowerInvariant()} rate is set for {part.ItemCode} — type a rate instead.");
        return (requested, rate);
    }

    public Task<int> WarehouseOnHandAsync(long partId, CancellationToken ct) =>
        db.StockBalances.AsNoTracking()
            .Where(b => b.PartId == partId && b.TechnicianId == StockBalance.Warehouse)
            .Select(b => b.OnHand).FirstOrDefaultAsync(ct);

    /// <summary>What a part's warehouse balance looks like to a sale being written.
    ///
    /// Stock only leaves when a sale is marked sold, so an unsold sale holds nothing — but the units on
    /// it are already promised to someone, and selling them twice is how a customer pays for goods that
    /// are not there. Available treats every other unsold sale as a claim. The sale being edited is
    /// excluded so its own lines do not count against it (pass 0 for a sale not yet saved).</summary>
    public async Task<PartAvailability> AvailabilityAsync(long partId, long excludeSaleId, CancellationToken ct)
    {
        var map = await AvailabilityAsync([partId], excludeSaleId, ct);
        return map.GetValueOrDefault(partId, new PartAvailability(partId, 0, 0));
    }

    /// <summary>Availability for many parts in two queries rather than two per part.</summary>
    public async Task<Dictionary<long, PartAvailability>> AvailabilityAsync(
        IReadOnlyCollection<long> partIds, long excludeSaleId, CancellationToken ct)
    {
        if (partIds.Count == 0) return new Dictionary<long, PartAvailability>();

        var onHand = await db.StockBalances.AsNoTracking()
            .Where(b => b.TechnicianId == StockBalance.Warehouse && partIds.Contains(b.PartId))
            .ToDictionaryAsync(b => b.PartId, b => b.OnHand, ct);

        var committed = await CommittedQuery(db, partIds, excludeSaleId)
            .ToDictionaryAsync(x => x.PartId, x => x.Qty, ct);

        return partIds.Distinct().ToDictionary(
            id => id,
            id => new PartAvailability(id, onHand.GetValueOrDefault(id), committed.GetValueOrDefault(id)));
    }

    /// <summary>Units of each part already claimed by other sales that have not taken their stock yet.
    /// Grouped in SQL — pulling the lines back to sum them in memory would drag every open sale line
    /// across for one lookup. Exposed so a test can force the provider to translate it.
    ///
    /// The filter is SoldAt, not Status: a sale that has been marked sold has already drawn its units
    /// out of the balance and must not be counted a second time, while one that has been invoiced but
    /// not yet marked still owes the warehouse those units. Cancelled sales never ship, so they claim
    /// nothing either way.</summary>
    public static IQueryable<PartCommitment> CommittedQuery(
        AppDbContext db, IReadOnlyCollection<long> partIds, long excludeSaleId) =>
        from l in db.SpareSaleLines.AsNoTracking()
        join s in db.SpareSales.AsNoTracking() on l.SpareSaleId equals s.Id
        where partIds.Contains(l.PartId)
              && !s.IsDeleted
              && s.SoldAt == null
              && s.Status != SpareSaleStatus.Cancelled
              && s.Id != excludeSaleId
        group l by l.PartId into g
        select new PartCommitment(g.Key, g.Sum(x => x.Qty));
}

public record PartCommitment(long PartId, int Qty);

/// <summary>A part's warehouse position from a sale's point of view. Available can go negative if stock
/// was adjusted down under sales that were already entered — reported as-is rather than clamped, because
/// a negative is exactly the shortfall someone has to resolve.</summary>
public readonly record struct PartAvailability(long PartId, int OnHand, int Committed)
{
    public int Available => OnHand - Committed;
}
