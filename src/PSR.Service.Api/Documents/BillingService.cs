using Microsoft.EntityFrameworkCore;
using PSR.Service.Api.Data;
using PSR.Service.Api.Data.Entities;
using PSR.Service.Api.Services;
using PSR.Service.Api.Settings;
using PSR.Service.Api.Stock;

using PSR.Service.Api.Common;

namespace PSR.Service.Api.Documents;

/// <summary>A computed-but-unsaved document plus the jobs it covers (used by the preview path); per job,
/// whether its component prices went into the figure; and the charges added on the form. Generation
/// writes the last two back onto the jobs, so the invoice after a PI bills on the same basis, and bills
/// the added charges again, without anybody re-entering either.</summary>
public sealed record BuiltDocument(ServiceDocument Doc, List<ServiceJob> Jobs,
    IReadOnlyDictionary<long, bool> ComponentPricing, IReadOnlyList<PendingAddedLine> AddedLines);

/// <summary>A charge added on the generate form, built and priced but not yet written: the service line it
/// becomes, the GST its charge or part carries, its name, and which rate it was taken at. <see cref="Index"/>
/// is its position in the request, so the quote can hand each figure back against the row it came from.</summary>
public sealed record PendingAddedLine(int Index, ServiceLine Line, decimal GstPercent, string Name, string RateBasis);

/// <summary>A computed-but-unsaved spare-sale document plus the sale it bills.</summary>
public sealed record BuiltSaleDocument(ServiceDocument Doc, SpareSale Sale);

/// <summary>The billing party, after the request's own fields have been layered over the party master.</summary>
internal sealed record PartySnapshot(string Name, string? Address, string? Gstin, string? State, string? StateCode);

/// <summary>What a job costs before anyone edits it: the tax-INCLUSIVE per-unit price its own service
/// lines add up to (zero while under warranty) and the blended GST rate those lines imply. One place
/// computes it, so the figure the generate form shows is the figure the server would have used.</summary>
internal readonly record struct JobPrice(decimal BlendedGst, decimal UnitInclusive);

/// <summary>One service line as pricing sees it: what kind it is, what it adds up to tax-exclusive, the
/// GST rate its part or charge carries, and whether it was added on a PI or invoice rather than on the
/// bench (<see cref="ServiceLine.AddedOnDocNo"/>).</summary>
internal readonly record struct PricedLine(ServiceLineType Type, decimal Amount, decimal GstPercent,
    bool AddedAtBilling = false);

/// <summary>Both ways a job can be priced — on its service charges alone, and with its component prices
/// added — so the generate form can flip a unit between the two without asking again. Replacement lines
/// are in both: they bill the unit the customer leaves with, which is not a component. So is anything
/// added on the generate form, components included: the switch is about what the technician fitted, and
/// a charge somebody added at billing was added to be billed.</summary>
internal readonly record struct JobPricing(JobPrice ChargesOnly, JobPrice WithComponents, bool HasComponents)
{
    public JobPrice For(bool includeComponents) => includeComponents ? WithComponents : ChargesOnly;
}

/// <summary>Builds a PI / Invoice / DC over one or more completed jobs of a single customer (old-app workflow):
/// snapshots the party, prices each unit (warranty-in → free), computes GST (CGST+SGST intra, IGST inter) on
/// the two documents that bill — a delivery challan carries none, see <see cref="ServiceDocument.CarriesTax"/>.
/// <see cref="BuildAsync"/> validates + computes WITHOUT touching the DB (preview); <see cref="GenerateAsync"/>
/// then allocates an atomic number, stamps it onto every covered job, and writes an item-history entry.</summary>
// CompanyInfo is injected to compare the party's state against the company's own — see GstStates. The
// PDF takes it as well, for the letterhead.
public class BillingService(AppDbContext db, NumberSequenceService seq, AppSettingsService settings,
    CompanyInfo company)
{
    /// <summary>Caps on what the generate form may add, so a malformed request cannot write a thousand
    /// lines onto a job. Far above anything a real document needs.</summary>
    internal const int MaxAddedLines = 50;
    internal const int MaxAddedQty = 1000;

    /// <summary>Validate + compute the document in memory. No number allocated, nothing persisted, jobs unchanged.</summary>
    public async Task<BuiltDocument> BuildAsync(GenerateDocumentRequest req, long userId, CancellationToken ct)
    {
        if (!Enum.TryParse<DocumentType>(req.DocType, true, out var docType))
            throw new BillingException($"Unknown document type '{req.DocType}'. Use PI, Invoice or DC.");

        // Admin kill-switch for invoice generation (enforced server-side, not just greyed out in the UI).
        if (docType == DocumentType.Invoice && !await settings.InvoiceGenerationEnabledAsync(ct))
            throw new BillingException("Invoice generation is currently disabled by an administrator.");

        var ids = req.ServiceIds.Distinct().ToList();
        if (ids.Count == 0) throw new BillingException("Select at least one completed job.");

        // Load the jobs (avoid List.Contains in EF — funcletizer bug — materialize then filter in memory).
        var all = await db.Services.Where(s => !s.IsDeleted).ToListAsync(ct);
        var jobs = all.Where(j => ids.Contains(j.Id)).ToList();
        if (jobs.Count != ids.Count) throw new BillingException("One or more selected jobs were not found.");

        // Single-customer rule (old app: PI/Invoice/DC only across the same customer/dealer).
        if (jobs.Select(j => (j.CustomerId, j.DealerId)).Distinct().Count() != 1)
            throw new BillingException("All selected jobs must belong to the same customer.");

        // None may be awaiting replacement approval; all must be completed (the pending-dispatch bucket).
        if (jobs.Any(j => j.ServiceStatus == ServiceStatus.ReplacementApprovalPending))
            throw new BillingException("A selected job is awaiting replacement approval and cannot be billed yet.");
        // A retained job holds the service centre's own unit — the customer was billed on the job the
        // replacement went out on, and there is nobody to bill for the shop repairing its own stock.
        if (jobs.FirstOrDefault(j => j.JobKind == JobKind.SwapRetained) is { } retained)
            throw new BillingException(
                $"{retained.ServiceNo} carries the service centre's own unit (kept against a replacement) "
                + "and cannot be billed.");
        if (jobs.Any(j => j.ServiceStatus is ServiceStatus.Inward or ServiceStatus.Assigned
                or ServiceStatus.Acknowledged or ServiceStatus.InService))
            throw new BillingException("Only completed jobs can be put on a document.");

        // Per-type gating — mirrors the old Pending-Dispatch button rules exactly.
        switch (docType)
        {
            case DocumentType.PI:
                if (jobs.Any(j => !string.IsNullOrWhiteSpace(j.PiNo)))
                    throw new BillingException("A selected job already has a PI.");
                if (jobs.Any(j => !string.IsNullOrWhiteSpace(j.OutwardDcNo)))
                    throw new BillingException("A selected job already has a delivery challan.");
                break;
            case DocumentType.Invoice:
                if (jobs.Any(j => string.IsNullOrWhiteSpace(j.PiNo)))
                    throw new BillingException("Generate the PI before the invoice.");
                if (jobs.Select(j => j.PiNo).Distinct().Count() != 1)
                    throw new BillingException("All selected jobs must share the same PI number.");
                if (jobs.Any(j => j.PaymentStatus != PaymentStatus.Paid))
                    throw new BillingException("Mark payment done before generating the invoice.");
                if (jobs.Any(j => !string.IsNullOrWhiteSpace(j.InvNo)))
                    throw new BillingException("A selected job already has an invoice.");
                break;
            case DocumentType.DC:
                if (jobs.Any(j => j.WarrantyStatus != WarrantyStatus.InWarranty))
                    throw new BillingException("A delivery challan is only for in-warranty units.");
                if (jobs.Any(j => !string.IsNullOrWhiteSpace(j.OutwardDcNo)))
                    throw new BillingException("A selected job already has a delivery challan.");
                break;
        }

        // Party snapshot — request fields win, else fall back to the jobs' party master.
        var first = jobs[0];
        var party = await ResolvePartyAsync(first.DealerId, first.CustomerId, req.PartyName, req.PartyAddress,
            req.PartyGstin, req.PartyState, req.PartyStateCode, ct);

        var overrides = (req.Lines ?? new()).GroupBy(l => l.ServiceId).ToDictionary(g => g.Key, g => g.Last());
        // Admin switch for hand-pricing a line. Read once, and compared against what the job prices to
        // rather than refused on presence: the generate form now pre-fills every rate with the server's
        // own figure, so a document sent back unchanged must not read as an attempt to edit it.
        var lineEditAllowed = await settings.DocumentLineEditEnabledAsync(ct);

        var doc = NewDocument(docType, party, req.DocDate, req.ConsigneeAddress,
            req.CourierMode, req.CourierCharges, req.Remarks, userId);

        // Charges added on the form, validated and priced now so they are inside every figure below —
        // including the check against hand-pricing, which compares the typed rate with the job's own
        // lines PLUS these. Written onto the jobs only by GenerateAsync.
        var added = await BuildAddedLinesAsync(jobs, req.AddedLines, docType, ct);

        // The HSN/SAC of each unit, read off the catalogue row its PS code names. Cached per code because
        // one document commonly covers several units of the same model.
        var catalogueHsn = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        decimal taxable = 0, tax = 0;
        var componentPricing = new Dictionary<long, bool>();
        foreach (var job in jobs.OrderBy(j => j.Id))
        {
            overrides.TryGetValue(job.Id, out var ov);

            // Whether this unit bills its components: what the form ticked, else what the job carries.
            // A request that says nothing (an older client) gets the job's own setting, which is off
            // unless somebody turned it on.
            var includeComponents = ov?.IncludeComponentPrices ?? job.IncludeComponentPrices;
            // Once a PI or an invoice has priced the job the setting is fixed, so the invoice bills on the
            // basis the PI quoted. The form shows it greyed out there; this is the same rule for a request
            // that did not come from the form.
            if (includeComponents != job.IncludeComponentPrices && ComponentPricingFixedBy(job) is { } fixedBy)
                throw new BillingException(
                    $"{job.ServiceNo}: {fixedBy} was raised {(job.IncludeComponentPrices ? "with" : "without")} "
                    + "the component prices, so this document has to bill on the same basis.");
            componentPricing[job.Id] = includeComponents;

            var price = (await PriceJobAsync(job, added.GetValueOrDefault(job.Id), ct)).For(includeComponents);
            // Zero on a delivery challan, which carries no GST — see ServiceDocument.CarriesTax. The rate
            // typed on a challan is the declared value of the unit, so nothing is split off it: the line
            // goes out with no GST rate, no tax amount and no head, and what was declared is what prints.
            var blendedGst = ServiceDocument.CarriesTax(docType) ? price.BlendedGst : 0m;

            var hsn = RequireLineHsn(ov?.HsnCode, await CatalogueHsnAsync(job.PsCode, catalogueHsn, ct),
                job.ServiceNo, job.PsCode);
            var qty = ov?.Qty is { } q && q > 0 ? q : 1;
            // In-warranty units are free unless a manager set an explicit rate.
            var unitInclusive = ov?.Rate ?? price.UnitInclusive;

            // The switch guards a document against being priced at something other than what the job's own
            // service lines add up to. A delivery challan covers in-warranty units, which add up to nothing,
            // so there is no server figure for a typed one to depart from — the value on a DC is a declared
            // one for transit and has to be entered by hand or the challan goes out blank. Gating it would
            // make the pricing a DC now carries unusable.
            if (!lineEditAllowed && docType != DocumentType.DC
                && (unitInclusive != price.UnitInclusive || qty != 1))
                throw new BillingException(
                    $"{job.ServiceNo}: editing the rate or quantity on a document is currently disabled by an "
                    + "administrator, so the document has to bill what the job's own service lines add up to.");

            var lineTotal = unitInclusive * qty;
            var lineTaxable = blendedGst > 0 ? Math.Round(lineTotal / (1 + blendedGst / 100m), 2) : lineTotal;
            var lineTax = lineTotal - lineTaxable;
            taxable += lineTaxable;
            tax += lineTax;

            doc.Lines.Add(new ServiceDocumentLine
            {
                ServiceJobId = job.Id,
                Description = ov?.Description?.Trim()
                    ?? (string.IsNullOrWhiteSpace(job.Description) ? $"{job.SerialNo} {job.ModelName}".Trim() : job.Description),
                // The unit's serial, kept out of the description so a retyped one cannot drop it.
                SerialNo = job.SerialNo,
                Warranty = ov?.Warranty?.Trim() ?? job.WarrantyStatus.ToString(),
                ServiceChallan = job.ChallanNo,
                // The catalogue's code for this item unless somebody typed over it — see RequireLineHsn.
                HsnCode = hsn,
                // Typed on the generate form, and left blank when it was left blank: a quantity with no
                // unit beside it is untidy, which is not the same as a line with no tax code on it.
                Unit = ov?.Unit?.Trim(),
                Qty = qty,
                UnitRate = unitInclusive,
                TaxableAmount = lineTaxable,
                GstPercent = blendedGst,
                TaxAmount = lineTax,
                LineTotal = lineTotal,
                Remarks = ov?.Remarks?.Trim(),
            });
        }

        ApplyTotals(doc, taxable, tax);
        return new BuiltDocument(doc, jobs, componentPricing,
            added.Values.SelectMany(x => x).OrderBy(x => x.Index).ToList());
    }

    /// <summary>Validate and price the charges added on the generate form, grouped by the job each one is
    /// for. Nothing is attached to the context — <see cref="GenerateAsync"/> writes them, a preview or a
    /// quote never does.
    ///
    /// A service charge is billed at its own rate, as on the bench. A component is billed at the rate
    /// <see cref="PartyRate"/> picks for whose job it is, the same rule the technician's lines and the
    /// replacement unit follow — the three end up on one bill, so they cannot be priced three ways.
    ///
    /// Refused, with the reason, rather than quietly dropped: on a delivery challan and on an in-warranty
    /// unit, which bill nothing either way, so a charge added there would never reach the paper; on a
    /// charge or part that is no longer offered; and on a part with no rate in either column, which would
    /// otherwise go out as a line billing nothing.</summary>
    private async Task<Dictionary<long, List<PendingAddedLine>>> BuildAddedLinesAsync(
        IReadOnlyCollection<ServiceJob> jobs, IReadOnlyList<DocAddedLine>? requested, DocumentType? docType,
        CancellationToken ct)
    {
        var result = new Dictionary<long, List<PendingAddedLine>>();
        if (requested is not { Count: > 0 }) return result;

        if (docType == DocumentType.DC)
            throw new BillingException("A delivery challan bills nothing, so no charge can be added to it. "
                + "Add it on the PI or the invoice.");
        if (requested.Count > MaxAddedLines)
            throw new BillingException($"At most {MaxAddedLines} charges can be added to one document.");

        var byId = jobs.ToDictionary(j => j.Id);
        for (var i = 0; i < requested.Count; i++)
        {
            var r = requested[i];
            if (!byId.TryGetValue(r.ServiceId, out var job))
                throw new BillingException("A charge was added to a unit that is not on this document.");
            if (job.WarrantyStatus == WarrantyStatus.InWarranty)
                throw new BillingException($"{job.ServiceNo} is in warranty, so it is billed nothing and a charge "
                    + "added to it would not reach the document.");
            if (r.Qty is < 1 or > MaxAddedQty)
                throw new BillingException($"{job.ServiceNo}: the quantity of an added charge must be between 1 "
                    + $"and {MaxAddedQty}.");
            if (!Enum.TryParse<ServiceLineType>(r.LineType, true, out var type) || type == ServiceLineType.Replacement)
                throw new BillingException($"Unknown charge type '{r.LineType}'. Add a ServiceCharge or a Component.");

            var line = new ServiceLine { ServiceId = job.Id, LineType = type, Qty = r.Qty };
            decimal gst;
            string name, basis;
            if (type == ServiceLineType.ServiceCharge)
            {
                if (r.ServiceChargeId is not { } scId)
                    throw new BillingException($"{job.ServiceNo}: pick the service charge to add.");
                var sc = await db.ServiceCharges.AsNoTracking().FirstOrDefaultAsync(s => s.Id == scId, ct)
                         ?? throw new BillingException("That service charge was not found.");
                if (!sc.IsActive)
                    throw new BillingException($"'{sc.Name}' is no longer offered — pick a current service charge.");
                line.ServiceChargeId = sc.Id;
                line.UnitPrice = sc.Charge;
                line.Description = sc.Name;
                gst = sc.TaxPercent;
                name = sc.Name;
                basis = "Service charge";
            }
            else
            {
                if (r.PartId is not { } pid)
                    throw new BillingException($"{job.ServiceNo}: pick the component to add.");
                var part = await db.Parts.AsNoTracking().FirstOrDefaultAsync(p => p.Id == pid, ct)
                           ?? throw new BillingException("That part was not found.");
                if (!part.IsActive)
                    throw new BillingException($"{part.ItemCode} is no longer active in the price list.");
                var isDealer = PartyRate.IsDealer(job);
                line.PartId = part.Id;
                line.UnitPrice = PartyRate.For(part, isDealer);
                if (line.UnitPrice <= 0)
                    throw new BillingException($"{part.ItemCode} has no dealer or customer rate in the price list, "
                        + "so it would be billed at nothing. Set a rate under Parts first.");
                gst = part.GstPercent;
                name = $"{part.Name} ({part.ItemCode})";
                basis = PartyRate.Describe(part, isDealer);
            }
            line.Amount = line.UnitPrice * line.Qty;

            if (!result.TryGetValue(job.Id, out var forJob)) result[job.Id] = forJob = new List<PendingAddedLine>();
            forJob.Add(new PendingAddedLine(i, line, gst, name, basis));
        }
        return result;
    }

    /// <summary>The document that fixed whether this job bills its component prices — its invoice, else its
    /// PI — or null while the setting is still open. A PI quotes the customer a figure and the invoice has to
    /// bill on the same basis, so once either exists the setting is no longer anybody's to change. A
    /// delivery challan does not fix it: it covers in-warranty units, which price at nothing either way.</summary>
    public static string? ComponentPricingFixedBy(ServiceJob job) =>
        !string.IsNullOrWhiteSpace(job.InvNo) ? job.InvNo.Trim()
        : !string.IsNullOrWhiteSpace(job.PiNo) ? job.PiNo.Trim()
        : null;

    /// <summary>The HSN/SAC one serviced unit is billed under: what somebody typed on the generate form,
    /// else the code on the catalogue row the unit's PS code names, else a refusal naming the job.
    ///
    /// The catalogue is the authority, because a serviced machine IS a catalogue item — the PS code booked
    /// in with the job is the same ItemCode the rest of the app matches a unit on — so the code a document
    /// bills it under is the one the price list already holds, exactly as a spare-sale line takes it. It
    /// was being asked for on the form instead, which meant every line went out as a dash unless somebody
    /// typed into a column they had no reason to open; on a delivery challan that was every line of every
    /// challan, a DC being the one document nobody stops to price.
    ///
    /// A typed code still wins. The form shows what the catalogue says and a manager may bill a unit under
    /// something else, which is a decision, not a gap.
    ///
    /// Nothing is invented when both are empty. Every plausible fallback is wrong somewhere — a repair SAC
    /// on a challan that is moving goods, a goods HSN on an invoice billing labour — and a code the
    /// software made up is worse on a tax document than one it refused to guess.</summary>
    internal static string RequireLineHsn(string? typed, string? fromCatalogue, string serviceNo, string? psCode)
    {
        if (!string.IsNullOrWhiteSpace(typed)) return typed.Trim();
        if (!string.IsNullOrWhiteSpace(fromCatalogue)) return fromCatalogue.Trim();

        // Which of the two gaps it is, because they are fixed in different places: the job is missing its
        // PS code, or the catalogue row that code names has no HSN on it (or no row exists at all).
        throw new BillingException(string.IsNullOrWhiteSpace(psCode)
            ? $"{serviceNo} has no PS code, so there is no catalogue item to read an HSN/SAC off. Put the "
              + "PS code on the job, or type the code on the generate form."
            : $"{serviceNo}: no HSN/SAC for PS code {psCode.Trim()} — either no catalogue item has that "
              + "code or the one that does has no HSN on it. Fill it in under Parts, or type the code on "
              + "the generate form.");
    }

    /// <summary>The HSN/SAC the catalogue holds for a PS code, or null when the code names no item or the
    /// item carries no code. Matched on ItemCode exactly, which is the match the rest of the app makes
    /// between a serviced unit and its catalogue row.
    ///
    /// Fetched a code at a time rather than with one Contains filter over the lot — see BuildAsync for why
    /// — and memoised in <paramref name="cache"/>, so six units of one model cost one query.</summary>
    private async Task<string?> CatalogueHsnAsync(string? psCode, Dictionary<string, string?> cache,
        CancellationToken ct)
    {
        var code = psCode?.Trim();
        if (string.IsNullOrWhiteSpace(code)) return null;
        if (cache.TryGetValue(code, out var known)) return known;

        var hsn = await db.Parts.AsNoTracking().Where(p => p.ItemCode == code)
            .Select(p => p.HsnCode).FirstOrDefaultAsync(ct);
        return cache[code] = string.IsNullOrWhiteSpace(hsn) ? null : hsn.Trim();
    }

    /// <summary>Persist a computed document: allocate the atomic number, stamp every covered job, and log the
    /// event to each job's status history. Returns the new document id.</summary>
    public async Task<long> GenerateAsync(GenerateDocumentRequest req, long userId, CancellationToken ct)
    {
        var (doc, jobs, componentPricing, added) = await BuildAsync(req, userId, ct);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var key = doc.DocType switch
        {
            DocumentType.PI => SequenceKeys.ProformaInvoice,
            DocumentType.Invoice => SequenceKeys.Invoice,
            _ => SequenceKeys.DeliveryChallan,
        };
        doc.DocNo = await seq.NextAsync(key, ct);
        db.ServiceDocuments.Add(doc);

        // The charges added on the form become lines on their jobs, stamped with this document's number.
        // That is what carries them forward: the invoice after a PI prices the job off its own lines, so
        // it bills them again without anybody re-adding them, and the job pane lists them with the rest.
        // Billing-only — no stock moves for any of them (see ServiceLine.AddedOnDocNo).
        foreach (var a in added)
        {
            a.Line.AddedOnDocNo = doc.DocNo;
            db.ServiceLines.Add(a.Line);
        }

        foreach (var job in jobs)
        {
            // Stamp the document number onto the job (the gated chain reads these back).
            switch (doc.DocType)
            {
                case DocumentType.PI: job.PiNo = doc.DocNo; job.PiDate = doc.DocDate; break;
                case DocumentType.Invoice: job.InvNo = doc.DocNo; job.InvDate = doc.DocDate; break;
                case DocumentType.DC: job.OutwardDcNo = doc.DocNo; job.DcDate = doc.DocDate; break;
            }
            // What this document billed, kept on the job: the pane shows it, and the invoice that follows
            // a PI is held to it (see ComponentPricingFixedBy).
            var billedComponents = componentPricing[job.Id];
            job.IncludeComponentPrices = billedComponents;
            job.RowVersion++;

            // Log the generation to the job's item history (shows in the detail pane). A document that
            // bills names which way the components went, and anything added to the job on the form, since
            // the figure alone says neither.
            var note = doc.DocType == DocumentType.DC
                ? $"{doc.DocNo} generated"
                : $"{doc.DocNo} generated — component prices {(billedComponents ? "billed" : "not billed")}";
            var addedHere = added.Where(a => a.Line.ServiceId == job.Id).Select(a => $"{a.Name} ×{a.Line.Qty}").ToList();
            if (addedHere.Count > 0) note += "; added " + string.Join(", ", addedHere);
            db.ServiceStatusHistory.Add(new ServiceStatusHistory
            {
                ServiceId = job.Id,
                FromStatus = job.ServiceStatus.ToString(),
                ToStatus = doc.DocType.ToString(),          // "PI" / "Invoice" / "DC" — not a workflow status, ignored by metrics
                ChangedByUserId = userId,
                // The history column is 500 wide; a long list of added charges is cut rather than refused.
                Note = note.Length > 500 ? note[..497] + "..." : note,
            });
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return doc.Id;
    }

    // ================================================================ spare sales

    /// <summary>Validate + compute the PI or tax invoice for a direct spare sale. Nothing is persisted and
    /// no number is allocated; <see cref="GenerateSaleAsync"/> does that. Neither of them moves stock —
    /// the sale's Mark as sold action is the only thing that does.</summary>
    public async Task<BuiltSaleDocument> BuildSaleAsync(GenerateSaleDocumentRequest req, long userId, CancellationToken ct)
    {
        if (!Enum.TryParse<DocumentType>(req.DocType, true, out var docType))
            throw new BillingException($"Unknown document type '{req.DocType}'. Use PI or Invoice.");
        if (docType == DocumentType.DC)
            throw new BillingException("A delivery challan is not issued for a spare sale — generate the PI or the tax invoice.");
        // The SALE switch, not the service one — the two books are gated independently.
        if (docType == DocumentType.Invoice && !await settings.SaleInvoiceGenerationEnabledAsync(ct))
            throw new BillingException("Sale invoice generation is currently disabled by an administrator.");

        // Tracked, not AsNoTracking — GenerateSaleAsync stamps the document number back onto this row.
        var sale = await db.SpareSales.Include(s => s.Lines).FirstOrDefaultAsync(s => s.Id == req.SaleId && !s.IsDeleted, ct)
            ?? throw new BillingException("That sale was not found.");

        if (sale.Status == SpareSaleStatus.Cancelled) throw new BillingException("This sale is cancelled.");
        if (sale.Lines.Count == 0) throw new BillingException("This sale has no items on it.");

        // A PI is OPTIONAL for a counter sale — it is the quote you send when the customer asks for one.
        // An invoice may therefore be raised either against a PI already issued for this sale, or straight
        // off the sale with no PI at all.
        //
        // Payment is no longer a gate either. A counter sale is settled in cash as the goods are handed
        // over, so marking it sold is what records the payment (see SpareSalesEndpoints.MarkSoldAsync),
        // and the separate "mark payment received" step the gate was waiting on no longer exists. An
        // invoice asked for before the goods go out is a bill being written in advance, which is a normal
        // thing to do and was only ever blocked because the two axes used to be welded together.
        if (docType == DocumentType.PI)
        {
            if (!string.IsNullOrWhiteSpace(sale.PiNo))
                throw new BillingException($"A PI ({sale.PiNo}) has already been generated for this sale.");
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(sale.InvNo))
                throw new BillingException($"Invoice {sale.InvNo} has already been generated for this sale.");

            // No stock check here. Invoicing bills the customer; it does not move goods — Mark as sold
            // does, and that is where availability is enforced. A sale can therefore be invoiced before
            // the goods are picked, or after they were handed over, without either order being wrong.
        }

        var party = await ResolvePartyAsync(sale.DealerId, sale.CustomerId, req.PartyName, req.PartyAddress,
            req.PartyGstin, req.PartyState, req.PartyStateCode, ct);

        var doc = NewDocument(docType, party, req.DocDate, req.ConsigneeAddress,
            req.CourierMode, req.CourierCharges, req.Remarks, userId);
        doc.SpareSaleId = sale.Id;

        foreach (var l in sale.Lines.OrderBy(l => l.Id))
        {
            doc.Lines.Add(new ServiceDocumentLine
            {
                ServiceJobId = null,
                PartId = l.PartId,
                Description = $"{l.Description} ({l.ItemCode})",
                Warranty = null,                 // meaningless on a spare sale — the PDF drops the column
                ServiceChallan = null,
                HsnCode = l.HsnCode,
                Unit = l.Unit,
                Qty = l.Qty,
                // The tax-EXCLUSIVE rate the line was priced at, carried over unchanged. Deriving an
                // inclusive rate here instead (LineTotal / Qty) lost paise to rounding, so the printed
                // Rate × Qty stopped matching the Amount beside it — and a spare-sale line prints a GST %
                // column, which only says something against a rate the tax has not been added to yet.
                UnitRate = l.UnitRate,
                TaxableAmount = l.TaxableAmount,
                GstPercent = l.GstPercent,
                TaxAmount = l.TaxAmount,
                LineTotal = l.LineTotal,
                Remarks = null,
            });
        }

        ApplyTotals(doc, sale.Lines.Sum(l => l.TaxableAmount), sale.Lines.Sum(l => l.TaxAmount));
        return new BuiltSaleDocument(doc, sale);
    }

    /// <summary>Persist a sale document: allocate the number and stamp it onto the sale. Nothing here
    /// touches stock — the warehouse moves only when the sale is marked sold, which is a separate action
    /// on the sale and the single place that draws goods out.</summary>
    public async Task<long> GenerateSaleAsync(GenerateSaleDocumentRequest req, long userId, CancellationToken ct)
    {
        var (doc, sale) = await BuildSaleAsync(req, userId, ct);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        doc.DocNo = await seq.NextAsync(
            doc.DocType == DocumentType.PI ? SequenceKeys.ProformaInvoice : SequenceKeys.Invoice, ct);
        db.ServiceDocuments.Add(doc);

        if (doc.DocType == DocumentType.PI)
        {
            sale.PiNo = doc.DocNo;
            sale.PiDate = doc.DocDate;
        }
        else
        {
            sale.InvNo = doc.DocNo;
            sale.InvDate = doc.DocDate;
            sale.Status = SpareSaleStatus.Invoiced;
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return doc.Id;
    }

    // ================================================================ parts challans

    /// <summary>Validate + compute a parts delivery challan. Nothing is persisted and no number is
    /// allocated; <see cref="GeneratePartsChallanAsync"/> does that.
    ///
    /// This is the one document path with no record underneath it. A service document covers jobs and a
    /// sale document bills a sale; a parts challan is paperwork for spares leaving the counter — to a
    /// dealer, or to a field technician — typed on the form and priced off the parts master. Which
    /// also means it has nothing to stamp a number back onto, and nothing to move: the register, the
    /// ledger and the stock balances are untouched by this and by the generate call below.</summary>
    public async Task<ServiceDocument> BuildPartsChallanAsync(GeneratePartsChallanRequest req, long userId,
        CancellationToken ct)
    {
        var lines = req.Lines ?? new List<PartsChallanLineRequest>();
        if (lines.Count == 0) throw new BillingException("Add at least one item to the challan.");
        // One parcel, one recipient. Both filled in is a form that cannot say who signs for the goods.
        if (req.DealerId is not null && req.TechnicianUserId is not null)
            throw new BillingException("A challan goes to one party — pick either a dealer or a technician.");

        // A technician is a user account, not a party master: there is no address or GSTIN to fall back
        // on, so the name is taken off the record and everything else is whatever the form carried.
        var partyName = req.PartyName;
        if (req.TechnicianUserId is { } tid)
        {
            var tech = await db.Users.AsNoTracking().Where(u => u.Id == tid)
                           .Select(u => new { u.Username, u.FullName }).FirstOrDefaultAsync(ct)
                       ?? throw new BillingException("That technician was not found.");
            if (string.IsNullOrWhiteSpace(partyName))
                partyName = string.IsNullOrWhiteSpace(tech.FullName) ? tech.Username : tech.FullName;
        }

        var party = await ResolvePartyAsync(req.DealerId, null, partyName, req.PartyAddress,
            req.PartyGstin, req.PartyState, req.PartyStateCode, ct);

        // Which rate column the master is read off, the same way a counter sale decides it.
        var isDealer = req.DealerId is not null;

        var doc = NewDocument(DocumentType.DC, party, req.DocDate, req.ConsigneeAddress,
            req.CourierMode, req.CourierCharges, req.Remarks, userId);

        decimal taxable = 0;
        foreach (var l in lines)
        {
            if (l.Qty < 1) throw new BillingException("Quantity must be at least 1.");

            // One at a time rather than one Contains filter over the lot — see BuildAsync for why.
            var part = await db.Parts.AsNoTracking().FirstOrDefaultAsync(p => p.Id == l.PartId, ct)
                       ?? throw new BillingException($"Part {l.PartId} was not found.");

            // The master's own rates, which are tax-exclusive — and a challan adds no tax to them, so the
            // rate IS the value declared per unit. A typed rate is honoured as it stands: a dispatch note
            // DECLARES a value for transit rather than billing one, so there is no master figure for it
            // to be wrong against, which is also why the hand-pricing switch does not reach here (see
            // the DC exemption in BuildAsync).
            var unitRate = l.UnitRate ?? (isDealer ? part.DealerRate : part.CustomerRate);
            if (unitRate < 0) throw new BillingException($"{part.ItemCode}: the rate cannot be negative.");

            // The declared value of this item, and no tax on it: a challan carries none — see
            // ServiceDocument.CarriesTax. The master's GST percentage is read past rather than applied.
            var lineValue = Math.Round(unitRate * l.Qty, 2);
            taxable += lineValue;

            doc.Lines.Add(new ServiceDocumentLine
            {
                // No job behind the line, and a catalogue item in front of it — which is what the PDF
                // reads to print the goods columns rather than the serial/warranty/challan trio.
                ServiceJobId = null,
                PartId = part.Id,
                Description = string.IsNullOrWhiteSpace(l.Description)
                    ? $"{part.Name} ({part.ItemCode})"
                    : l.Description.Trim(),
                SerialNo = null,
                Warranty = null,                 // meaningless on parts going out, as on a spare sale
                ServiceChallan = null,
                // The catalogue's code unless somebody typed over it. Not refused when both are empty:
                // a spare-sale PI — an actual billing document — prints what the master has and no
                // more, and a challan has no business being stricter than the invoice beside it.
                HsnCode = string.IsNullOrWhiteSpace(l.HsnCode) ? part.HsnCode : l.HsnCode.Trim(),
                Unit = string.IsNullOrWhiteSpace(l.Unit) ? part.Unit : l.Unit.Trim(),
                Qty = l.Qty,
                UnitRate = unitRate,
                TaxableAmount = lineValue,
                GstPercent = 0m,
                TaxAmount = 0m,
                LineTotal = lineValue,
                Remarks = null,
            });
        }

        ApplyTotals(doc, taxable, tax: 0m);
        return doc;
    }

    /// <summary>Persist a parts challan: allocate the number and write the document. That is the whole
    /// of it — there is no job to stamp, no sale to mark, and deliberately no stock movement. The
    /// number comes out of its own book (<see cref="SequenceKeys.PartsChallan"/>, PDC), so neither the
    /// customer challan register nor the store's issue dockets gain a number with nothing behind
    /// it.</summary>
    public async Task<long> GeneratePartsChallanAsync(GeneratePartsChallanRequest req, long userId,
        CancellationToken ct)
    {
        var doc = await BuildPartsChallanAsync(req, userId, ct);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        doc.DocNo = await seq.NextAsync(SequenceKeys.PartsChallan, ct);
        db.ServiceDocuments.Add(doc);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return doc.Id;
    }

    // ================================================================ quoting

    /// <summary>What one job's own service lines price it at, before any manager edit, both ways: on its
    /// service charges alone and with its component prices added. Which one a document bills is the job's
    /// <see cref="ServiceJob.IncludeComponentPrices"/>, or what the generate form ticked.
    ///
    /// <paramref name="pending"/> are the charges the generate form has added and not yet written; they are
    /// priced in exactly as the lines already on the job are.</summary>
    private async Task<JobPricing> PriceJobAsync(ServiceJob job, IReadOnlyCollection<PendingAddedLine>? pending,
        CancellationToken ct)
    {
        var lines = await (from l in db.ServiceLines.AsNoTracking()
                           where l.ServiceId == job.Id
                           join p in db.Parts on l.PartId equals p.Id into pg
                           from p in pg.DefaultIfEmpty()
                           join sc in db.ServiceCharges on l.ServiceChargeId equals sc.Id into scg
                           from sc in scg.DefaultIfEmpty()
                           select new
                           {
                               l.LineType, l.Amount, AddedAtBilling = l.AddedOnDocNo != null,
                               Gst = p != null ? p.GstPercent : (sc != null ? sc.TaxPercent : 0m),
                           })
            .ToListAsync(ct);

        var priced = lines.Select(x => new PricedLine(x.LineType, x.Amount, x.Gst, x.AddedAtBilling)).ToList();
        if (pending is not null)
            priced.AddRange(pending.Select(a =>
                new PricedLine(a.Line.LineType, a.Line.Amount, a.GstPercent, AddedAtBilling: true)));

        return PriceLines(priced, job.WarrantyStatus == WarrantyStatus.InWarranty);
    }

    /// <summary>Price a job's lines both ways. The technician's Component lines are the only ones the switch
    /// takes out: service charges are what the job bills by default; a Replacement line is the unit the
    /// customer leaves with — it is in both figures, or an out-of-warranty replacement goes back to billing
    /// nothing; and anything added on a PI or invoice, a component included, was added to be billed.
    /// In-warranty units come back at zero either way — the customer is not billed for work under cover.
    ///
    /// HasComponents counts only what the switch acts on, so a job whose only components were added at
    /// billing has nothing for the tick box to add.</summary>
    internal static JobPricing PriceLines(IReadOnlyCollection<PricedLine> lines, bool inWarranty) => new(
        Blend(lines.Where(l => l.Type != ServiceLineType.Component || l.AddedAtBilling), inWarranty),
        Blend(lines, inWarranty),
        lines.Any(l => l.Type == ServiceLineType.Component && !l.AddedAtBilling));

    /// <summary>The tax-inclusive per-unit figure a set of lines adds up to, and the blended GST rate behind
    /// it. The blended rate exists because a job mixes parts and service charges that can carry different
    /// GST percentages while the document bills the unit as one item. It is the rate that reproduces the
    /// lines' own tax, not a rate read off any master.</summary>
    private static JobPrice Blend(IEnumerable<PricedLine> lines, bool inWarranty)
    {
        var list = lines.ToList();
        var naturalTaxable = list.Sum(x => x.Amount);
        var naturalTax = list.Sum(x => Math.Round(x.Amount * x.GstPercent / 100m, 2));
        var blendedGst = naturalTaxable > 0 ? Math.Round(naturalTax / naturalTaxable * 100m, 2) : 18m;
        var unitInclusive = inWarranty ? 0m : naturalTaxable + naturalTax;
        return new JobPrice(blendedGst, unitInclusive);
    }

    /// <summary>Price the selected jobs without building anything, so the generate form can show what each
    /// unit is about to be billed. Same code path as <see cref="BuildAsync"/>, which is the point: the form
    /// used to open with an empty rate box on every line, and the only way to learn what a unit would cost
    /// was to generate the document and read the PDF.
    ///
    /// Jobs are fetched one id at a time rather than with a Contains filter — see BuildAsync for why —
    /// but per id, not by pulling the table, since this runs on every visit to the form.
    ///
    /// It also hands back each unit's HSN/SAC from the catalogue, which is not pricing but is the other
    /// thing the form would otherwise have to ask a human for.
    ///
    /// <paramref name="addedLines"/> are the charges the form has added so far. They are validated the
    /// way generation will validate them — a refusal comes back as a <see cref="BillingException"/> the
    /// moment the charge is added, rather than at the preview — and priced into every figure. The type of
    /// document is not known here, so the one rule this cannot apply is the delivery-challan refusal;
    /// generation applies it, and the form offers no adding on a challan.</summary>
    public async Task<List<DocumentQuoteLineDto>> QuoteAsync(IReadOnlyCollection<long> serviceIds,
        IReadOnlyList<DocAddedLine>? addedLines, CancellationToken ct)
    {
        var jobs = new List<ServiceJob>();
        foreach (var id in serviceIds.Distinct().OrderBy(x => x))
        {
            var job = await db.Services.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id && !s.IsDeleted, ct);
            if (job is not null) jobs.Add(job);     // a missing one: generation re-checks and refuses the whole document
        }
        var added = await BuildAddedLinesAsync(jobs, addedLines, docType: null, ct);

        var rows = new List<DocumentQuoteLineDto>();
        var catalogueHsn = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var job in jobs)
        {
            var addedHere = added.GetValueOrDefault(job.Id);
            var pricing = await PriceJobAsync(job, addedHere, ct);
            // The headline figure is the one the job's own setting gives, which is what a document would
            // bill if nobody touched the tick box; both alternatives ride along below it.
            var price = pricing.For(job.IncludeComponentPrices);
            var taxable = price.BlendedGst > 0
                ? Math.Round(price.UnitInclusive / (1 + price.BlendedGst / 100m), 2)
                : price.UnitInclusive;

            rows.Add(new DocumentQuoteLineDto(
                job.Id, job.ServiceNo, job.SerialNo,
                string.IsNullOrWhiteSpace(job.Description) ? $"{job.SerialNo} {job.ModelName}".Trim() : job.Description,
                job.WarrantyStatus.ToString(), price.BlendedGst,
                price.UnitInclusive, taxable, price.UnitInclusive - taxable,
                // So the form opens with the tax code already in the column, read off the same catalogue
                // row the document itself will fall back to.
                await CatalogueHsnAsync(job.PsCode, catalogueHsn, ct),
                job.IncludeComponentPrices,
                ComponentPricingFixedBy(job),
                pricing.HasComponents,
                pricing.ChargesOnly.UnitInclusive, pricing.ChargesOnly.BlendedGst,
                pricing.WithComponents.UnitInclusive, pricing.WithComponents.BlendedGst,
                addedHere?.Select(a => new QuotedAddedLineDto(
                    a.Index, job.Id, a.Line.LineType.ToString(), a.Line.ServiceChargeId, a.Line.PartId, a.Name,
                    a.Line.Qty, a.Line.UnitPrice, a.Line.Amount, a.GstPercent, a.RateBasis)).ToList(),
                PartyRate.IsDealer(job)));
        }
        return rows;
    }

    // ================================================================ shared

    /// <summary>Layer the request's own party fields over the party master. A dealer carries a full billing
    /// block (address/GSTIN/state); a direct customer only carries name and address, so GST details for a
    /// walk-in are typed on the generate form.</summary>
    private async Task<PartySnapshot> ResolvePartyAsync(
        long? dealerId, long? customerId, string? name, string? address,
        string? gstin, string? state, string? stateCode, CancellationToken ct)
    {
        var partyName = name?.Trim();
        var partyAddress = address?.Trim();
        var partyGstin = gstin?.Trim();
        var partyState = state?.Trim();
        var partyStateCode = stateCode?.Trim();

        if (dealerId is { } did)
        {
            var dealer = await db.Dealers.Where(x => x.Id == did)
                .Select(x => new { x.Name, x.Address, x.Gstin, x.State, x.StateCode }).FirstOrDefaultAsync(ct);
            if (string.IsNullOrWhiteSpace(partyName)) partyName = dealer?.Name;
            partyAddress ??= dealer?.Address;
            partyGstin ??= dealer?.Gstin;
            partyState ??= dealer?.State;
            partyStateCode ??= dealer?.StateCode;
        }
        else if (customerId is { } cid)
        {
            var c = await db.Customers.Where(x => x.Id == cid)
                .Select(x => new { x.Name, x.Address }).FirstOrDefaultAsync(ct);
            if (string.IsNullOrWhiteSpace(partyName)) partyName = c?.Name;
            partyAddress ??= c?.Address;
        }

        if (string.IsNullOrWhiteSpace(partyName)) throw new BillingException("Party name is required.");

        // Settle the state once, here, so the document carries the code its tax was decided on and the PDF
        // has a name to print beside it. A party can arrive with a code and no name (an API caller), a name
        // and no code (typed before the picker existed), or neither and a GSTIN that opens with one. A name
        // already filled in is left alone — what somebody typed is not ours to rewrite.
        //
        // A party that carries none of the three is refused. On a PI or an invoice the code is what decides
        // whether the document bills CGST + SGST or IGST, and the old answer — treat a party with no state
        // as local — made that decision by itself for every walk-in and every dealer master nobody had
        // filled in, which is a tax treatment arrived at by omission. The generate form now opens with no
        // state picked, so this is the same question asked in the one place that cannot be skipped. A
        // delivery challan carries no tax for the code to decide (see ServiceDocument.CarriesTax) and is
        // held to the same rule anyway: the state is on the party block the challan prints, and a dispatch
        // note that cannot say where a parcel is going is worse paperwork than one that asks.
        var resolvedCode = GstStates.ResolveCode(partyStateCode, partyState, partyGstin)
            ?? throw new BillingException(
                "Pick the party's state on the generate form. It is printed on the document, and on a PI "
                + "or an invoice its GST code is what decides whether the tax goes out as CGST + SGST or "
                + "IGST.");
        partyStateCode = resolvedCode;
        if (string.IsNullOrWhiteSpace(partyState)) partyState = GstStates.ByCode(resolvedCode)?.Name;

        return new PartySnapshot(partyName, partyAddress, partyGstin, partyState, partyStateCode);
    }

    private ServiceDocument NewDocument(DocumentType docType, PartySnapshot party, DateTime? docDate,
        string? consigneeAddress, string? courierMode, decimal? courierCharges, string? remarks, long userId) =>
        new()
        {
            DocType = docType,
            DocDate = ShopClock.BusinessDate(docDate) ?? ShopClock.Today,
            PartyName = party.Name,
            PartyAddress = party.Address,
            // Consignee/delivery address — defaults to the billing address when not given separately.
            ConsigneeAddress = string.IsNullOrWhiteSpace(consigneeAddress) ? party.Address : consigneeAddress.Trim(),
            PartyGstin = party.Gstin,
            PartyState = party.State,
            PartyStateCode = party.StateCode,
            // CGST + SGST inside the company's state, IGST across a state line — read off the party's own
            // state code (see GstStates). It is stamped onto the record rather than worked out at print
            // time, so a reprint years later shows the heads the customer was actually billed under,
            // including the stretch when every document went out as IGST whatever the state code said.
            IsInterState = GstStates.IsInterState(party.StateCode, party.State, party.Gstin, company.StateCode),
            CourierMode = courierMode?.Trim(),
            CourierCharges = courierCharges ?? 0m,
            // Fixed by the tax schedule, so it is applied here rather than asked for on the form — and
            // stamped on the record, like the tax heads above, so a reprint shows what was billed.
            CourierHsnCode = courierCharges is > 0m || !string.IsNullOrWhiteSpace(courierMode)
                ? ServiceDocument.CourierSacCode
                : null,
            Remarks = remarks?.Trim(),
            CreatedByUserId = userId,
        };

    private static void ApplyTotals(ServiceDocument doc, decimal taxable, decimal tax)
    {
        doc.TaxableAmount = taxable;
        if (doc.IsInterState) doc.IgstAmount = tax;
        else { doc.CgstAmount = Math.Round(tax / 2m, 2); doc.SgstAmount = tax - doc.CgstAmount; }
        // Rounded up to the next whole rupee — the figure the customer actually pays. The parts above keep
        // their paise, so the gap is a real number the PDF prints as a Round off line rather than a penny
        // the document loses track of.
        doc.TotalAmount = BillMoney.RoundUp(taxable + tax + doc.CourierCharges);
    }
}
