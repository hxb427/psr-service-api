using Microsoft.EntityFrameworkCore;
using PSR.Service.Api.Data;
using PSR.Service.Api.Data.Entities;
using PSR.Service.Api.Stock;

namespace PSR.Service.Api.Documents;

/// <summary>Builds a PI / Invoice / DC for a service job: snapshots the party, prices every billable line,
/// computes GST (CGST+SGST intra-state, IGST inter-state), and allocates an atomic document number.
/// All GST/rate logic that used to live in the Flutter client now lives here, server-side.</summary>
public class BillingService(AppDbContext db, NumberSequenceService seq, CompanyInfo company)
{
    public async Task<ServiceDocument> GenerateAsync(long serviceId, GenerateDocumentRequest req, long userId, CancellationToken ct)
    {
        if (!Enum.TryParse<DocumentType>(req.DocType, true, out var docType))
            throw new BillingException($"Unknown document type '{req.DocType}'. Use PI, Invoice or DC.");

        var job = await db.Services.FirstOrDefaultAsync(s => s.Id == serviceId && !s.IsDeleted, ct)
            ?? throw new BillingException("Service job not found.");

        // Party snapshot: explicit request fields win, else fall back to the job's party master.
        var partyName = req.PartyName?.Trim();
        var partyAddress = req.PartyAddress?.Trim();
        if (string.IsNullOrWhiteSpace(partyName))
        {
            if (job.DealerId is { } did)
                partyName = await db.Dealers.Where(d => d.Id == did).Select(d => d.Name).FirstOrDefaultAsync(ct);
            else if (job.CustomerId is { } cid)
            {
                var c = await db.Customers.Where(x => x.Id == cid)
                    .Select(x => new { x.Name, x.Address }).FirstOrDefaultAsync(ct);
                partyName = c?.Name;
                partyAddress ??= c?.Address;
            }
        }
        if (string.IsNullOrWhiteSpace(partyName)) throw new BillingException("Party name is required.");

        // Inter-state when the party's state code differs from the company's (Kerala = 32). Blank = treat as intra.
        var interState = !string.IsNullOrWhiteSpace(req.PartyStateCode)
            && !string.Equals(req.PartyStateCode.Trim(), company.StateCode, StringComparison.OrdinalIgnoreCase);

        // Billable lines — HSN + GST% come from the linked part / service charge.
        var rows = await (from l in db.ServiceLines.AsNoTracking()
                          where l.ServiceId == serviceId
                          join p in db.Parts on l.PartId equals p.Id into pg
                          from p in pg.DefaultIfEmpty()
                          join sc in db.ServiceCharges on l.ServiceChargeId equals sc.Id into scg
                          from sc in scg.DefaultIfEmpty()
                          orderby l.Id
                          select new
                          {
                              l.Id, l.Qty, l.UnitPrice, l.Amount, l.Description,
                              PartName = p != null ? p.Name : null,
                              Hsn = p != null ? p.HsnCode : null,
                              ScName = sc != null ? sc.Name : null,
                              Gst = p != null ? p.GstPercent : (sc != null ? sc.TaxPercent : 0m),
                          }).ToListAsync(ct);

        if (req.LineIds is { Count: > 0 })
        {
            var set = req.LineIds.ToHashSet();
            rows = rows.Where(r => set.Contains(r.Id)).ToList();
        }

        var doc = new ServiceDocument
        {
            DocType = docType,
            DocDate = req.DocDate ?? DateTime.UtcNow,
            ServiceId = serviceId,
            PartyName = partyName,
            PartyAddress = partyAddress,
            PartyGstin = req.PartyGstin?.Trim(),
            PartyState = req.PartyState?.Trim(),
            PartyStateCode = req.PartyStateCode?.Trim(),
            IsInterState = interState,
            CourierMode = req.CourierMode?.Trim(),
            CourierCharges = req.CourierCharges ?? 0m,
            Remarks = req.Remarks?.Trim(),
            CreatedByUserId = userId,
        };

        decimal taxable = 0, tax = 0;
        foreach (var r in rows)
        {
            var lineTaxable = r.Amount;                              // = UnitPrice * Qty (tax-exclusive)
            var lineTax = Math.Round(lineTaxable * r.Gst / 100m, 2);
            taxable += lineTaxable;
            tax += lineTax;
            doc.Lines.Add(new ServiceDocumentLine
            {
                Description = r.Description ?? r.PartName ?? r.ScName ?? "Item",
                HsnCode = r.Hsn,
                Qty = r.Qty,
                UnitRate = r.UnitPrice,
                TaxableAmount = lineTaxable,
                GstPercent = r.Gst,
                TaxAmount = lineTax,
                LineTotal = lineTaxable + lineTax,
            });
        }

        doc.TaxableAmount = taxable;
        if (interState)
        {
            doc.IgstAmount = tax;
        }
        else
        {
            doc.CgstAmount = Math.Round(tax / 2m, 2);
            doc.SgstAmount = tax - doc.CgstAmount;                   // keep the halves summing exactly to tax
        }
        doc.TotalAmount = taxable + tax + doc.CourierCharges;

        // Atomic number + insert in one transaction (NextAsync locks the sequence row FOR UPDATE).
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var key = docType switch
        {
            DocumentType.PI => SequenceKeys.ProformaInvoice,
            DocumentType.Invoice => SequenceKeys.Invoice,
            _ => SequenceKeys.DeliveryChallan,
        };
        doc.DocNo = await seq.NextAsync(key, ct);
        db.ServiceDocuments.Add(doc);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return doc;
    }
}
