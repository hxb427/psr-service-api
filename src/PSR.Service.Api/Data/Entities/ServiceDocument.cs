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
    /// <summary>The SAC a courier charge is billed under. Courier is a support service, not goods, so it
    /// carries its own code rather than the HSN of anything in the table — and it is fixed by the tax
    /// schedule, not a choice, so it is applied rather than asked for on the generate form.</summary>
    public const string CourierSacCode = "9985";

    public long Id { get; set; }
    public DocumentType DocType { get; set; }
    public string DocNo { get; set; } = string.Empty;   // unique per type (PI-2026-0001, INV-2026-0001, DC-2026-0001)
    public DateTime DocDate { get; set; } = Common.ShopClock.Today;

    // A document covers one OR MORE service jobs of a single customer (old app: one PI lists many units).
    // The covered jobs are the distinct ServiceJobId values across Lines; ServiceId stays null for multi-job docs.
    public long? ServiceId { get; set; }                 // legacy single-job link (kept nullable; unused for multi-job)
    /// <summary>Set instead of the service links when this document bills a direct spare sale
    /// (counter sale of warehouse stock). A document is one or the other, never both.</summary>
    public long? SpareSaleId { get; set; }

    // ----- party snapshot (frozen at generation; entered on the generate form, like the old app) -----
    public string PartyName { get; set; } = string.Empty;
    public string? PartyAddress { get; set; }              // billing address
    public string? ConsigneeAddress { get; set; }          // delivery / consignee address (old app: separate block)
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
    /// <summary>The SAC the courier charge went out under — <see cref="CourierSacCode"/>, stamped
    /// whenever a courier is put on the document. Snapshotted like every other printed figure rather
    /// than filled in at print time, so a document reprinted after the code ever changes still shows the
    /// one the customer was billed under.</summary>
    public string? CourierHsnCode { get; set; }
    public string? Remarks { get; set; }

    public long CreatedByUserId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public List<ServiceDocumentLine> Lines { get; set; } = new();
}

/// <summary>One line on a <see cref="ServiceDocument"/> = one serviced unit (service job), matching the old app's
/// PI/Invoice table (a row per unit). Snapshot — not a live FK chain. The covered job is <see cref="ServiceJobId"/>.</summary>
public class ServiceDocumentLine
{
    public long Id { get; set; }
    public long DocumentId { get; set; }
    public long? ServiceJobId { get; set; }              // the serviced unit this line bills (null on spare-sale lines)
    public long? PartId { get; set; }                    // the catalogue item sold (spare-sale lines only)
    public string Description { get; set; } = string.Empty;
    /// <summary>Serial number of the serviced unit this line bills, snapshotted at generation. What
    /// identifies the machine to the customer, so it is printed in its own column rather than left to
    /// whatever the description happens to say — a manager who retypes the description must not be able
    /// to take the serial off the invoice with it. Null on spare-sale lines (catalogue goods).</summary>
    public string? SerialNo { get; set; }
    public string? Warranty { get; set; }                // snapshot of the unit's warranty status (Active units bill at 0)
    public string? ServiceChallan { get; set; }          // the unit's inward challan no
    public string? HsnCode { get; set; }
    /// <summary>The unit the quantity is counted in (Nos, Set, Pcs...). Typed on the generate form for a
    /// serviced unit, which has no catalogue row to read one off, and carried over from the sale line for
    /// spare-sale goods. A quantity column with no unit beside it says how many but not of what.</summary>
    public string? Unit { get; set; }
    public int Qty { get; set; } = 1;
    public decimal UnitRate { get; set; }                // tax-inclusive rate for the unit (manager-editable)
    public decimal TaxableAmount { get; set; }
    public decimal GstPercent { get; set; }
    public decimal TaxAmount { get; set; }
    public decimal LineTotal { get; set; }               // taxable + tax
    public string? Remarks { get; set; }

    public ServiceDocument Document { get; set; } = null!;
}
