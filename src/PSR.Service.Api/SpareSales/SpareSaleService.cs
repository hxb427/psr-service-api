using Microsoft.EntityFrameworkCore;
using PSR.Service.Api.Audit;
using PSR.Service.Api.Data;
using PSR.Service.Api.Data.Entities;
using PSR.Service.Api.Documents;

using PSR.Service.Api.Common;

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

        sale.SaleDate = ShopClock.BusinessDate(req.SaleDate) ?? ShopClock.Today;
        sale.Remarks = req.Remarks?.Trim();

        sale.Lines.Clear();
        // A part may legitimately appear twice (part at list price, part discounted), so the stock is
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
            // Raw warehouse on-hand, and nothing else. This used to subtract what other unsold sales had
            // claimed, on the reasoning that promised units are not ours to sell twice — but writing a
            // sale takes nothing out of the warehouse, so those claims were a figure the counter could
            // not see in any stock screen and could not act on. One forgotten draft sale for 44 units
            // took an item with 22 in stock down to "-22 available" and blocked every sale of it until
            // somebody went looking for a sale nobody remembered. The entry form says what is on the
            // shelf; mark-sold is where the stock is actually claimed, and it re-checks before it moves.
            var onHand = await WarehouseOnHandAsync(part.Id, ct);
            if (askedPerPart[part.Id] > onHand)
                throw new SaleValidationException(
                    $"{part.ItemCode} — {onHand} in warehouse stock, {askedPerPart[part.Id]} asked for.");

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
        // Up to the next whole rupee, the same rule the document totals use — the counter quotes this figure
        // and then bills it, so the two must not differ by paise. Courier is not on it: that is charged on
        // the document, which rounds its own total again once the charge is added.
        sale.TotalAmount = BillMoney.RoundUp(sale.TaxableAmount + sale.TaxAmount);
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
            // The GSTIN comes back with the row because the state is resolved off it when the form sent
            // no code — a dealer's own registration cannot have been issued in another state.
            var dealer = await db.Dealers.AsNoTracking().Where(d => d.Id == did)
                             .Select(d => new { d.Gstin }).FirstOrDefaultAsync(ct)
                         ?? throw new SaleValidationException("That dealer was not found.");

            sale.CustomerType = "Dealer";
            sale.DealerId = did;
            sale.CustomerId = null;
            ApplyBillingDetails(sale, req, dealer.Gstin);
            return true;
        }

        if (string.Equals(type, "Direct", StringComparison.OrdinalIgnoreCase))
        {
            var party = await Services.ServicesEndpoints.ResolveCustomerAsync(
                db, req.CustomerId, req.CustomerName, null, req.Phone, null, req.Address,
                ct, audit, userId, ip, origin: "sale");
            // A name already on the dealer list is refused with its own wording — it says which dealer
            // and why, which "Name the customer being billed" would throw away.
            if (party.CustomerId is null)
                throw new SaleValidationException(party.Error ?? "Name the customer being billed.");

            sale.CustomerType = "Direct";
            sale.CustomerId = party.CustomerId;
            sale.DealerId = null;
            // No GSTIN to fall back on — the customer master does not carry one — so a walk-in's state
            // is whatever the form was told, which is why the form is made to ask.
            ApplyBillingDetails(sale, req, null);
            return false;
        }

        throw new SaleValidationException($"Unknown customer type '{req.CustomerType}'. Use Dealer or Direct.");
    }

    /// <summary>The party's billing address and state, kept on the sale as the entry form showed them.
    ///
    /// The state is required and is resolved exactly as a document resolves it — the code picked, else
    /// the one the party's GSTIN opens with, else the one the state name implies — so a sale and the PI
    /// raised against it can never disagree about the tax split. It used to be asked for on the generate
    /// form alone, which meant the question that decides CGST + SGST against IGST was put once per
    /// document, to whoever happened to be raising it, long after the customer had left the counter.</summary>
    private static void ApplyBillingDetails(SpareSale sale, SaveSpareSaleRequest req, string? partyGstin)
    {
        var code = GstStates.ResolveCode(req.PartyStateCode, req.PartyState, partyGstin)
            ?? throw new SaleValidationException(
                "Pick the party's state. Its GST code is what decides whether the documents raised for "
                + "this sale carry CGST + SGST or IGST.");

        sale.PartyAddress = string.IsNullOrWhiteSpace(req.Address) ? null : req.Address.Trim();
        sale.PartyStateCode = code;
        // The list's own name for the code, so what the register filters on and what a document prints
        // are the same words. A code the list does not name keeps whatever was typed instead of
        // travelling nameless.
        sale.PartyState = GstStates.ByCode(code)?.Name
            ?? (string.IsNullOrWhiteSpace(req.PartyState) ? null : req.PartyState.Trim());
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

    /// <summary>What the warehouse holds for one part. The only stock figure a sale deals in now, on the
    /// entry form and again at mark-sold.</summary>
    public Task<int> WarehouseOnHandAsync(long partId, CancellationToken ct) =>
        db.StockBalances.AsNoTracking()
            .Where(b => b.PartId == partId && b.TechnicianId == StockBalance.Warehouse)
            .Select(b => b.OnHand).FirstOrDefaultAsync(ct);

    /// <summary>The same figure for many parts in one query, for a sale's whole line set. A part with no
    /// balance row is absent from the dictionary and reads as nothing in stock, which is what it is.
    ///
    /// There is no second half to this any more. It used to also sum what other unsold sales had claimed
    /// and subtract it, so the form showed on-hand minus other people's drafts — see the note in
    /// <see cref="ApplyAsync"/> for why that is gone. Nothing reserves warehouse stock: it leaves on
    /// mark-sold and not before, and that is the one place a shortfall can be real.</summary>
    public async Task<Dictionary<long, int>> WarehouseOnHandAsync(
        IReadOnlyCollection<long> partIds, CancellationToken ct)
    {
        if (partIds.Count == 0) return new Dictionary<long, int>();

        return await db.StockBalances.AsNoTracking()
            .Where(b => b.TechnicianId == StockBalance.Warehouse && partIds.Contains(b.PartId))
            .ToDictionaryAsync(b => b.PartId, b => b.OnHand, ct);
    }
}
