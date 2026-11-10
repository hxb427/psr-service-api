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

/// <summary>One unsold sale's hold on one serial — see <see cref="SpareSaleService.ClaimQuery"/>.</summary>
public record SaleSerialClaim(long PartId, string SerialNumber, long SaleId, string SaleNo);

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

        // Every serial on the form, checked in one pass before any line is built. Per-line would be
        // wrong twice over: the same part can sit on two lines and a unit named on both is one unit
        // sold twice, and a form short of three serials should say so once rather than be refused
        // three times over three saves. Refuses here as a backstop — the form asks
        // /spare-sales/serials/check first, which is where the counter gets the choice rather than a
        // sentence. See FindSerialConflictsAsync.
        await ValidateSerialsAsync(sale.Id, req, ct);

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

            var line = new SpareSaleLine
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
            };

            // Added through the line's own collection, so EF fills in the line id these rows hang off
            // once the sale is saved — the line itself does not have one yet on a new sale.
            foreach (var sn in Normalize(lineReq.Serials))
                line.Serials.Add(new SpareSaleLineSerial { PartId = part.Id, SerialNumber = sn });

            sale.Lines.Add(line);
        }

        sale.TaxableAmount = sale.Lines.Sum(l => l.TaxableAmount);
        sale.TaxAmount = sale.Lines.Sum(l => l.TaxAmount);
        // Up to the next whole rupee, the same rule the document totals use — the counter quotes this figure
        // and then bills it, so the two must not differ by paise. Courier is not on it: that is charged on
        // the document, which rounds its own total again once the charge is added.
        sale.TotalAmount = BillMoney.RoundUp(sale.TaxableAmount + sale.TaxAmount);
    }

    // ---------------------------------------------------------------- serials

    /// <summary>The serials as they will be stored: trimmed, blanks dropped. A barcode scanner that
    /// sends a trailing newline, and a form that keeps an empty row at the bottom, both arrive here.
    /// Case is NOT folded — the serial is stored as it is printed on the box, and every comparison
    /// below is case-insensitive instead.</summary>
    internal static List<string> Normalize(IEnumerable<string>? serials) =>
        (serials ?? []).Select(s => s?.Trim() ?? string.Empty).Where(s => s.Length > 0).ToList();

    /// <summary>Refuse a sale whose serials do not add up, naming everything that is wrong with them
    /// at once.
    ///
    /// Three separate rules, and they fail for different reasons:
    ///
    /// <list type="bullet">
    /// <item>a serial-tracked part needs one serial per unit — 5 on the line means 5 of them. This is
    /// the rule that makes a sale of tracked stock traceable at all, so it is not optional and there
    /// is no "enter them later".</item>
    /// <item>a part sold by quantity must carry none. A serial against an untracked part would sit in
    /// the table saying a unit was named that nothing will ever track.</item>
    /// <item>no conflicts — see <see cref="FindSerialConflictsAsync"/>, which is also what the form
    /// asks before it submits.</item>
    /// </list>
    ///
    /// This is the backstop, not the way the counter is meant to find out. Reaching it means either a
    /// client that did not check, or a unit that was claimed in the seconds between the check and the
    /// save.</summary>
    private async Task ValidateSerialsAsync(long saleId, SaveSpareSaleRequest req, CancellationToken ct)
    {
        var tracked = await db.Parts.AsNoTracking()
            .Where(p => req.Lines.Select(l => l.PartId).Contains(p.Id))
            .Select(p => new { p.Id, p.ItemCode, p.IsSerialTracked })
            .ToDictionaryAsync(p => p.Id, ct);

        var problems = new List<string>();
        // Grouped by part, because that is the unit a serial belongs to: the same part on two lines is
        // one pool of units, and "5 and 5" across two lines of the same code needs 10 distinct serials.
        foreach (var g in req.Lines.GroupBy(l => l.PartId))
        {
            if (!tracked.TryGetValue(g.Key, out var part)) continue;   // the line loop reports the part
            var serials = Normalize(g.SelectMany(l => l.Serials ?? []));
            var qty = g.Sum(l => l.Qty);

            if (!part.IsSerialTracked)
            {
                if (serials.Count > 0)
                    problems.Add($"{part.ItemCode} is not serial-tracked — it is sold by quantity, so "
                               + "no serial numbers belong on it.");
                continue;
            }

            if (serials.Count != qty)
                problems.Add($"{part.ItemCode} — {qty} being sold, {serials.Count} serial number(s) "
                           + "entered. A serial-tracked item needs one serial per unit.");
        }

        var conflicts = await FindSerialConflictsAsync(saleId == 0 ? null : saleId, req.Lines
            .GroupBy(l => l.PartId)
            .Select(g => new SaleSerialCheckLine(g.Key, Normalize(g.SelectMany(l => l.Serials ?? []))))
            .ToList(), ct);
        problems.AddRange(conflicts.Select(c => $"{c.ItemCode} {c.SerialNumber} — {c.Reason}"));

        if (problems.Count > 0)
            throw new SaleValidationException(string.Join("\n", problems));
    }

    /// <summary>Everything standing between these serials and this sale, one entry per serial.
    ///
    /// Asked by the sale form before it submits, so the counter is given the choice while the customer
    /// is still at the counter, and asked again by <see cref="ValidateSerialsAsync"/> as the backstop.
    /// Modelled on <c>SerialService.FindIssueConflictsAsync</c>, which does the same job for an issue
    /// to a technician, and the three reasons are in the order they are worth hearing:
    ///
    /// <list type="number">
    /// <item><b>Entered twice on this form.</b> One unit cannot be sold twice, and neither can a form
    /// typed with the same box scanned twice.</item>
    /// <item><b>Already in the serial ledger.</b> The shop knows this unit — it is with a technician,
    /// at a customer, on the shelf, scrapped. A sale draws on warehouse stock, which is the quantity
    /// the shop bought in and has never named; a unit that already has a record is not part of it.
    /// There is nothing to decide here, so these are not releasable, and the counter is told which
    /// state it is in so the SN screen can be looked at if the number was simply mistyped.</item>
    /// <item><b>Claimed by another unsold sale.</b> The only one of the three that is a question
    /// rather than an answer. Both sales are still drafts, nothing is tracked yet, and either may
    /// keep the unit — so it comes back releasable, naming the sale, and the counter decides.</item>
    /// </list>
    ///
    /// A cancelled or sold sale is not in the way of anything. Cancelled is the record of something
    /// that did not happen and will never be marked sold, and a SOLD sale's serials are in the ledger
    /// by then — so they are already caught by rule 2, with the state the unit is actually in, rather
    /// than reported twice.</summary>
    public async Task<List<SaleSerialConflictDto>> FindSerialConflictsAsync(
        long? excludeSaleId, IReadOnlyList<SaleSerialCheckLine> lines, CancellationToken ct)
    {
        var wanted = lines.GroupBy(l => l.PartId)
            .ToDictionary(g => g.Key, g => Normalize(g.SelectMany(l => l.Serials)));
        var partIds = wanted.Keys.ToList();
        if (partIds.Count == 0) return new List<SaleSerialConflictDto>();

        var codes = await db.Parts.AsNoTracking().Where(p => partIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => p.ItemCode, ct);

        var conflicts = new List<SaleSerialConflictDto>();

        // Fetched per part and matched in memory, like FindIssueConflictsAsync: MySQL's own collation
        // would decide the case sensitivity of a WHERE on the serial, and the one thing that must not
        // vary is whether "psr-01" and "PSR-01" are the same unit. They are.
        var tracked = (await db.ComponentSerials.AsNoTracking()
                .Where(c => partIds.Contains(c.PartId))
                .Select(c => new { c.PartId, c.SerialNumber, c.Status, c.OwnerType, c.OwnerRef })
                .ToListAsync(ct))
            .GroupBy(c => (c.PartId, c.SerialNumber.ToUpperInvariant()))
            .ToDictionary(g => g.Key, g => g.First());

        var claimed = (await ClaimQuery(db, partIds).ToListAsync(ct))
            .Where(x => x.SaleId != excludeSaleId)
            .GroupBy(x => (x.PartId, x.SerialNumber.ToUpperInvariant()))
            .ToDictionary(g => g.Key, g => g.First());

        foreach (var (partId, serials) in wanted)
        {
            var code = codes.GetValueOrDefault(partId, $"Part {partId}");

            foreach (var dupe in serials.GroupBy(s => s, StringComparer.OrdinalIgnoreCase)
                         .Where(g => g.Count() > 1).Select(g => g.Key))
                conflicts.Add(new SaleSerialConflictDto(partId, code, dupe,
                    "Entered more than once on this sale.", Releasable: false));

            foreach (var sn in serials.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var key = (partId, sn.ToUpperInvariant());
                if (tracked.TryGetValue(key, out var unit))
                {
                    var where = unit.OwnerType switch
                    {
                        SerialOwnerType.Technician => $"with a technician (status {unit.Status})",
                        SerialOwnerType.Customer => $"at {unit.OwnerRef ?? "a customer"} (status {unit.Status})",
                        _ => $"at the service centre (status {unit.Status})",
                    };
                    conflicts.Add(new SaleSerialConflictDto(partId, code, sn,
                        $"Already tracked — this unit is {where}. It is not warehouse stock, so it "
                        + "cannot be sold here.", Releasable: false));
                    continue;
                }
                if (claimed.TryGetValue(key, out var other))
                    conflicts.Add(new SaleSerialConflictDto(partId, code, sn,
                        $"Already entered on {other.SaleNo}, which has not been marked sold yet.",
                        Releasable: true, other.SaleId, other.SaleNo));
            }
        }

        return conflicts;
    }

    /// <summary>Every serial claim held by a LIVE, unsold sale for one of these parts.
    ///
    /// Separate and internal so the provider can be made to translate it in a test: it is three joins
    /// deep and it runs while a sale form is being typed, so one that cannot be translated would first
    /// be discovered by a counter waiting on it.
    ///
    /// The three conditions on the sale are the whole definition of a claim that is in the way. A
    /// DELETED or CANCELLED sale will never be marked sold, so its serials are nobody's. A SOLD one's
    /// serials are in the serial ledger by now, so they are caught as tracked units instead — with the
    /// state the unit is actually in, which is more use than being told which sale it was on.</summary>
    internal static IQueryable<SaleSerialClaim> ClaimQuery(
        AppDbContext db, IReadOnlyCollection<long> partIds) =>
        from s in db.SpareSaleLineSerials.AsNoTracking()
        join l in db.SpareSaleLines.AsNoTracking() on s.SpareSaleLineId equals l.Id
        join sale in db.SpareSales.AsNoTracking() on l.SpareSaleId equals sale.Id
        where partIds.Contains(s.PartId)
           && !sale.IsDeleted
           && sale.SoldAt == null
           && sale.Status != SpareSaleStatus.Cancelled
        select new SaleSerialClaim(s.PartId, s.SerialNumber, sale.Id, sale.SaleNo);

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
