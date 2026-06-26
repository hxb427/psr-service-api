namespace PSR.Service.Api.Data.Entities;

/// <summary>PI (proforma invoice), tax Invoice, or DC (delivery challan).</summary>
public enum DocumentType
{
    PI,        // proforma invoice
    Invoice,   // tax invoice
    DC,        // delivery challan
}

/// <summary>A generated billing/dispatch document for a service job (replaces the legacy `pi` table and the
/// pi_no / inv_no fields scattered on service_table). Everything billed is snapshotted at generation time so
/// the printed figures never drift if the underlying job/lines change later.</summary>
public class ServiceDocument
{
    public long Id { get; set; }
    public DocumentType DocType { get; set; }
    public string DocNo { get; set; } = string.Empty;   // unique per type (PI-2026-0001, INV-2026-0001, DC-2026-0001)
    public DateTime DocDate { get; set; } = DateTime.UtcNow;

    public long? ServiceId { get; set; }                 // the service job billed
    public long? SpareSaleId { get; set; }               // reserved for spare-sale documents (Phase 5.2)

    // ----- party snapshot (frozen at generation; entered on the generate form, like the old app) -----
    public string PartyName { get; set; } = string.Empty;
    public string? PartyAddress { get; set; }
    public string? PartyGstin { get; set; }
    public string? PartyState { get; set; }
    public string? PartyStateCode { get; set; }
    public bool IsInterState { get; set; }               // true => IGST; false => CGST + SGST

    // ----- money snapshot -----
    public decimal TaxableAmount { get; set; }
    public decimal CgstAmount { get; set; }
    public decimal SgstAmount { get; set; }
    public decimal IgstAmount { get; set; }
    public decimal CourierCharges { get; set; }
    public decimal TotalAmount { get; set; }
    public string? CourierMode { get; set; }
    public string? Remarks { get; set; }

    public long CreatedByUserId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public List<ServiceDocumentLine> Lines { get; set; } = new();
}

/// <summary>A single billed line on a <see cref="ServiceDocument"/> (snapshot — not a live FK to service_lines).</summary>
public class ServiceDocumentLine
{
    public long Id { get; set; }
    public long DocumentId { get; set; }
    public string Description { get; set; } = string.Empty;
    public string? HsnCode { get; set; }
    public int Qty { get; set; } = 1;
    public decimal UnitRate { get; set; }
    public decimal TaxableAmount { get; set; }
    public decimal GstPercent { get; set; }
    public decimal TaxAmount { get; set; }
    public decimal LineTotal { get; set; }               // taxable + tax

    public ServiceDocument Document { get; set; } = null!;
}
