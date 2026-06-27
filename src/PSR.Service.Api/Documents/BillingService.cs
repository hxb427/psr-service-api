using Microsoft.EntityFrameworkCore;
using PSR.Service.Api.Data;
using PSR.Service.Api.Data.Entities;
using PSR.Service.Api.Settings;
using PSR.Service.Api.Stock;

namespace PSR.Service.Api.Documents;

/// <summary>A computed-but-unsaved document plus the jobs it covers (used by the preview path).</summary>
public sealed record BuiltDocument(ServiceDocument Doc, List<ServiceJob> Jobs);

/// <summary>Builds a PI / Invoice / DC over one or more completed jobs of a single customer (old-app workflow):
/// snapshots the party, prices each unit (warranty-in → free), computes GST (CGST+SGST intra, IGST inter).
/// <see cref="BuildAsync"/> validates + computes WITHOUT touching the DB (preview); <see cref="GenerateAsync"/>
/// then allocates an atomic number, stamps it onto every covered job, and writes an item-history entry.</summary>
public class BillingService(AppDbContext db, NumberSequenceService seq, CompanyInfo company, AppSettingsService settings)
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

        // Party snapshot — request fields win, else fall back to the jobs' party master (dealer carries full
        // billing details: address/GSTIN/state; a direct customer carries name+address).
        var first = jobs[0];
        var partyName = req.PartyName?.Trim();
        var partyAddress = req.PartyAddress?.Trim();
        var partyGstin = req.PartyGstin?.Trim();
        var partyState = req.PartyState?.Trim();
        var partyStateCode = req.PartyStateCode?.Trim();
        if (first.DealerId is { } did)
        {
            var dealer = await db.Dealers.Where(x => x.Id == did)
                .Select(x => new { x.Name, x.Address, x.Gstin, x.State, x.StateCode }).FirstOrDefaultAsync(ct);
            if (string.IsNullOrWhiteSpace(partyName)) partyName = dealer?.Name;
            partyAddress ??= dealer?.Address;
            partyGstin ??= dealer?.Gstin;
            partyState ??= dealer?.State;
            partyStateCode ??= dealer?.StateCode;
        }
        else if (first.CustomerId is { } cid)
        {
            var c = await db.Customers.Where(x => x.Id == cid).Select(x => new { x.Name, x.Address }).FirstOrDefaultAsync(ct);
            if (string.IsNullOrWhiteSpace(partyName)) partyName = c?.Name;
            partyAddress ??= c?.Address;
        }
        if (string.IsNullOrWhiteSpace(partyName)) throw new BillingException("Party name is required.");

        var interState = !string.IsNullOrWhiteSpace(partyStateCode)
            && !string.Equals(partyStateCode, company.StateCode, StringComparison.OrdinalIgnoreCase);

        var overrides = (req.Lines ?? new()).GroupBy(l => l.ServiceId).ToDictionary(g => g.Key, g => g.Last());

        var doc = new ServiceDocument
        {
            DocType = docType,
            DocDate = req.DocDate ?? DateTime.UtcNow,
            PartyName = partyName,
            PartyAddress = partyAddress,
            // Consignee/delivery address — defaults to the billing address when not given separately.
            ConsigneeAddress = string.IsNullOrWhiteSpace(req.ConsigneeAddress) ? partyAddress : req.ConsigneeAddress.Trim(),
            PartyGstin = partyGstin,
            PartyState = partyState,
            PartyStateCode = partyStateCode,
            IsInterState = interState,
            CourierMode = req.CourierMode?.Trim(),
            CourierCharges = req.CourierCharges ?? 0m,
            Remarks = req.Remarks?.Trim(),
            CreatedByUserId = userId,
        };

        decimal taxable = 0, tax = 0;
        foreach (var job in jobs.OrderBy(j => j.Id))
        {
            // Natural per-unit taxable/tax from the job's service lines (+ part/charge GST).
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

            overrides.TryGetValue(job.Id, out var ov);
            var qty = ov?.Qty is { } q && q > 0 ? q : 1;

            // In-warranty units are free unless a manager set an explicit rate.
            decimal unitInclusive;
            if (ov?.Rate is { } r) unitInclusive = r;
            else if (job.WarrantyStatus == WarrantyStatus.InWarranty) unitInclusive = 0m;
            else unitInclusive = naturalTaxable + naturalTax;

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

        doc.TaxableAmount = taxable;
        if (interState) doc.IgstAmount = tax;
        else { doc.CgstAmount = Math.Round(tax / 2m, 2); doc.SgstAmount = tax - doc.CgstAmount; }
        doc.TotalAmount = taxable + tax + doc.CourierCharges;

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
}
