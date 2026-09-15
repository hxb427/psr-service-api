using Microsoft.EntityFrameworkCore;
using PSR.Service.Api.Data;
using PSR.Service.Api.Data.Entities;
using PSR.Service.Api.Settings;
using PSR.Service.Api.Stock;

using PSR.Service.Api.Common;

namespace PSR.Service.Api.Documents;

/// <summary>A computed-but-unsaved document plus the jobs it covers (used by the preview path).</summary>
public sealed record BuiltDocument(ServiceDocument Doc, List<ServiceJob> Jobs);

/// <summary>A computed-but-unsaved spare-sale document plus the sale it bills.</summary>
public sealed record BuiltSaleDocument(ServiceDocument Doc, SpareSale Sale);

/// <summary>The billing party, after the request's own fields have been layered over the party master.</summary>
internal sealed record PartySnapshot(string Name, string? Address, string? Gstin, string? State, string? StateCode);

/// <summary>What a job costs before anyone edits it: the tax-INCLUSIVE per-unit price its own service
/// lines add up to (zero while under warranty) and the blended GST rate those lines imply. One place
/// computes it, so the figure the generate form shows is the figure the server would have used.</summary>
internal readonly record struct JobPrice(decimal BlendedGst, decimal UnitInclusive);

/// <summary>Builds a PI / Invoice / DC over one or more completed jobs of a single customer (old-app workflow):
/// snapshots the party, prices each unit (warranty-in → free), computes GST (CGST+SGST intra, IGST inter).
/// <see cref="BuildAsync"/> validates + computes WITHOUT touching the DB (preview); <see cref="GenerateAsync"/>
/// then allocates an atomic number, stamps it onto every covered job, and writes an item-history entry.</summary>
public class BillingService(AppDbContext db, NumberSequenceService seq, CompanyInfo company,
    AppSettingsService settings)
{
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

        decimal taxable = 0, tax = 0;
        foreach (var job in jobs.OrderBy(j => j.Id))
        {
            var price = await PriceJobAsync(job, ct);
            var blendedGst = price.BlendedGst;

            overrides.TryGetValue(job.Id, out var ov);
            var qty = ov?.Qty is { } q && q > 0 ? q : 1;
            // In-warranty units are free unless a manager set an explicit rate.
            var unitInclusive = ov?.Rate ?? price.UnitInclusive;

            if (!lineEditAllowed && (unitInclusive != price.UnitInclusive || qty != 1))
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
                HsnCode = null,
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
        return new BuiltDocument(doc, jobs);
    }

    /// <summary>Persist a computed document: allocate the atomic number, stamp every covered job, and log the
    /// event to each job's status history. Returns the new document id.</summary>
    public async Task<long> GenerateAsync(GenerateDocumentRequest req, long userId, CancellationToken ct)
    {
        var (doc, jobs) = await BuildAsync(req, userId, ct);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var key = doc.DocType switch
        {
            DocumentType.PI => SequenceKeys.ProformaInvoice,
            DocumentType.Invoice => SequenceKeys.Invoice,
            _ => SequenceKeys.DeliveryChallan,
        };
        doc.DocNo = await seq.NextAsync(key, ct);
        db.ServiceDocuments.Add(doc);

        foreach (var job in jobs)
        {
            // Stamp the document number onto the job (the gated chain reads these back).
            switch (doc.DocType)
            {
                case DocumentType.PI: job.PiNo = doc.DocNo; job.PiDate = doc.DocDate; break;
                case DocumentType.Invoice: job.InvNo = doc.DocNo; job.InvDate = doc.DocDate; break;
                case DocumentType.DC: job.OutwardDcNo = doc.DocNo; job.DcDate = doc.DocDate; break;
            }
            job.RowVersion++;

            // Log the generation to the job's item history (shows in the detail pane).
            db.ServiceStatusHistory.Add(new ServiceStatusHistory
            {
                ServiceId = job.Id,
                FromStatus = job.ServiceStatus.ToString(),
                ToStatus = doc.DocType.ToString(),          // "PI" / "Invoice" / "DC" — not a workflow status, ignored by metrics
                ChangedByUserId = userId,
                Note = $"{doc.DocNo} generated",
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
        // off the sale with no PI at all. Payment is still the gate on the invoice.
        if (docType == DocumentType.PI)
        {
            if (!string.IsNullOrWhiteSpace(sale.PiNo))
                throw new BillingException($"A PI ({sale.PiNo}) has already been generated for this sale.");
        }
        else
        {
            if (sale.PaymentStatus != PaymentStatus.Paid)
                throw new BillingException("Mark payment received before generating the invoice.");
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

    // ================================================================ quoting

    /// <summary>What one job's own service lines price it at, before any manager edit: the tax-inclusive
    /// per-unit figure and the blended GST rate behind it. In-warranty units come back at zero — the
    /// customer is not billed for work under cover.
    ///
    /// The blended rate exists because a job mixes parts and service charges that can carry different GST
    /// percentages while the document bills the unit as one item. It is the rate that reproduces the
    /// line's own tax, not a rate read off any master.</summary>
    private async Task<JobPrice> PriceJobAsync(ServiceJob job, CancellationToken ct)
    {
        var lines = await (from l in db.ServiceLines.AsNoTracking()
                           where l.ServiceId == job.Id
                           join p in db.Parts on l.PartId equals p.Id into pg
                           from p in pg.DefaultIfEmpty()
                           join sc in db.ServiceCharges on l.ServiceChargeId equals sc.Id into scg
                           from sc in scg.DefaultIfEmpty()
                           select new { l.Amount, Gst = p != null ? p.GstPercent : (sc != null ? sc.TaxPercent : 0m) })
            .ToListAsync(ct);

        var naturalTaxable = lines.Sum(x => x.Amount);
        var naturalTax = lines.Sum(x => Math.Round(x.Amount * x.Gst / 100m, 2));
        var blendedGst = naturalTaxable > 0 ? Math.Round(naturalTax / naturalTaxable * 100m, 2) : 18m;
        var unitInclusive = job.WarrantyStatus == WarrantyStatus.InWarranty ? 0m : naturalTaxable + naturalTax;
        return new JobPrice(blendedGst, unitInclusive);
    }

    /// <summary>Price the selected jobs without building anything, so the generate form can show what each
    /// unit is about to be billed. Same code path as <see cref="BuildAsync"/>, which is the point: the form
    /// used to open with an empty rate box on every line, and the only way to learn what a unit would cost
    /// was to generate the document and read the PDF.
    ///
    /// Jobs are fetched one id at a time rather than with a Contains filter — see BuildAsync for why —
    /// but per id, not by pulling the table, since this runs on every visit to the form.</summary>
    public async Task<List<DocumentQuoteLineDto>> QuoteAsync(IReadOnlyCollection<long> serviceIds, CancellationToken ct)
    {
        var rows = new List<DocumentQuoteLineDto>();
        foreach (var id in serviceIds.Distinct().OrderBy(x => x))
        {
            var job = await db.Services.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id && !s.IsDeleted, ct);
            if (job is null) continue;      // generation re-checks and refuses the whole document

            var price = await PriceJobAsync(job, ct);
            var taxable = price.BlendedGst > 0
                ? Math.Round(price.UnitInclusive / (1 + price.BlendedGst / 100m), 2)
                : price.UnitInclusive;

            rows.Add(new DocumentQuoteLineDto(
                job.Id, job.ServiceNo, job.SerialNo,
                string.IsNullOrWhiteSpace(job.Description) ? $"{job.SerialNo} {job.ModelName}".Trim() : job.Description,
                job.WarrantyStatus.ToString(), price.BlendedGst,
                price.UnitInclusive, taxable, price.UnitInclusive - taxable));
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
            // Out-of-state parties are billed IGST; anyone in the company's own state gets CGST + SGST.
            IsInterState = !string.IsNullOrWhiteSpace(party.StateCode)
                && !string.Equals(party.StateCode, company.StateCode, StringComparison.OrdinalIgnoreCase),
            CourierMode = courierMode?.Trim(),
            CourierCharges = courierCharges ?? 0m,
            Remarks = remarks?.Trim(),
            CreatedByUserId = userId,
        };

    private static void ApplyTotals(ServiceDocument doc, decimal taxable, decimal tax)
    {
        doc.TaxableAmount = taxable;
        if (doc.IsInterState) doc.IgstAmount = tax;
        else { doc.CgstAmount = Math.Round(tax / 2m, 2); doc.SgstAmount = tax - doc.CgstAmount; }
        doc.TotalAmount = taxable + tax + doc.CourierCharges;
    }
}
