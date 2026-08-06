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
            var onHand = await WarehouseOnHandAsync(part.Id, ct);
            if (askedPerPart[part.Id] > onHand)
                throw new SaleValidationException(
                    $"{part.ItemCode} — only {onHand} in warehouse stock, {askedPerPart[part.Id]} asked for.");

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
}
