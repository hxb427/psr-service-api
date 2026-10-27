namespace PSR.Service.Api.Data.Entities;

/// <summary>Which rate column a field visit bills at. Deliberately NOT <see cref="SaleRateType"/>,
/// which also carries Manual: on a field visit the server prices every line from the parts master and
/// a technician never sends a figure, so admitting Manual here would name a value nothing honours.</summary>
public enum FieldRateType { Customer, Dealer }

/// <summary>Whether the serviced machine was still under warranty.
///
/// Nullable on the record, and <see cref="Unknown"/> is a different thing from null: Unknown is the
/// technician saying they could not establish it, null is nobody having been asked. The app fetches a
/// default from the factory-test record and leaves it editable, because the machine in front of the
/// technician is the authority on what it is, not a row in another company's database.</summary>
public enum FieldWarrantyStatus { In, Out, Unknown }

/// <summary>How the customer settled. Cash and UPI are collected on the spot; a bank transfer is
/// usually promised rather than seen, which is why the record keeps the method the technician was told
/// instead of claiming the money arrived.</summary>
public enum FieldPaymentMethod { Cash, Upi, BankTransfer }

/// <summary>On-site field service performed by a field technician (legacy service_records).
/// Separate from the shop-job workflow (services): no state machine — a completed fact.
/// Creating one consumes the technician's stock and drives serial transitions.</summary>
public class FieldService
{
    public long Id { get; set; }
    public string ServiceNo { get; set; } = string.Empty;   // unique (FSVnnnnn)
    public long TechnicianId { get; set; }
    /// <summary>The Poornasree ticket this visit was booked against, when the job came from one.
    /// Null for a walk-in or a call taken directly — the ledger movement stands on its own either way.
    /// Both halves are kept because the two systems quote different ones: the ticket API is keyed by
    /// the UUID, while every human names the ticket by its TKT-yyyymmdd-xxxxxxxx number.</summary>
    public string? TicketId { get; set; }
    public string? TicketNumber { get; set; }

    /// <summary>The call register entry this visit answers, when the technician picked one.
    ///
    /// A different thing from <see cref="TicketId"/> and both can be set at once: a ticket is the
    /// customer's own request raised in Poornasree, while a <see cref="FieldCall"/> is our record
    /// that an engineer was sent out. The office logs calls that never had a ticket (a dealer rings
    /// the branch) and tickets arrive that nobody logged a call for, so neither can stand in for the
    /// other.
    ///
    /// Null for a visit filed on its own, which stays the ordinary case. Where it is set, saving the
    /// visit may complete the call — see <see cref="FieldCallStatus"/> for why that only ever moves
    /// one way.
    ///
    /// Not unique, deliberately: a call left Incomplete is picked again for the next visit, and the
    /// two visits are both filed against the one call. That is the whole point of leaving it
    /// incomplete.</summary>
    public long? FieldCallId { get; set; }

    public string CustomerName { get; set; } = string.Empty;
    /// <summary>Set when the technician picked an existing customer rather than typing a new name.
    /// Free text stays authoritative for display; this is what carries ownership onto the serial.</summary>
    public long? CustomerId { get; set; }
    public string? Phone { get; set; }
    public string? Place { get; set; }
    public string? MachineSerial { get; set; }              // serviced unit's serial (free text)
    public string? Complaint { get; set; }
    /// <summary>What the technician found to be behind the complaint. The complaint is the customer's
    /// account of the symptom; this is the diagnosis, and they are routinely different things - "no
    /// output" against "sensor board corroded". Kept apart so neither overwrites the other.</summary>
    public string? Cause { get; set; }
    public string? WorkDone { get; set; }
    public string? Remarks { get; set; }

    // ---------------------------------------------------------------- commercial

    /// <summary>Which rate column every line on this visit bills at. One choice for the visit, not per
    /// line: a visit is billed to one party, and a dealer who is charged dealer rates for the board is
    /// not charged customer rates for the seal in the same breath.</summary>
    public FieldRateType RateType { get; set; } = FieldRateType.Customer;

    // The chosen service charge, snapshotted. The id stays live so the catalogue can still be ranked by
    // what technicians actually pick, but the description and the figure are copies - repricing a
    // charge next month must not rewrite what a customer was told last month.
    public long? ServiceChargeId { get; set; }
    public string? ServiceChargeDescription { get; set; }
    /// <summary>Tax-exclusive, like every other rate in this system.</summary>
    public decimal ServiceChargeAmount { get; set; }
    public decimal ServiceChargeTaxPercent { get; set; }

    public FieldPaymentMethod? PaymentMethod { get; set; }
    public FieldWarrantyStatus? WarrantyStatus { get; set; }

    // Visit totals, computed and persisted server-side. A client never sends a figure, and these are
    // stored rather than summed on read so the record still reads as what the customer was told after
    // the parts master moves on.
    public decimal TaxableAmount { get; set; }
    public decimal TaxAmount { get; set; }
    /// <summary>Taxable + tax, rounded up to the rupee (<see cref="Common.BillMoney"/>). The VALUE of
    /// the work, whoever ends up paying for it.</summary>
    public decimal TotalAmount { get; set; }
    /// <summary>What the customer actually owes - <see cref="TotalAmount"/>, or nil when the machine
    /// was in warranty. Separate from the total on purpose: a warranty job still has a value, and the
    /// shop needs to know what it absorbed. Collapsing the two would make a covered repair look like
    /// it cost nothing to carry out.</summary>
    public decimal AmountPayable { get; set; }

    // ---------------------------------------------------------------- where it happened

    /// <summary>The place the technician picked out of the names a GPS fix reverse-geocoded to.
    /// <see cref="Place"/> stays what it was - free text, often copied off the ticket.</summary>
    public string? LocationLabel { get; set; }
    /// <summary>The fix itself. hsrtech stored only the picked label, which made its "location" worth
    /// exactly as much as whichever name the technician tapped; attendance is a claim about a point on
    /// the ground, so the point is what gets stored. Null when the handset could not get a fix - that
    /// does not block the record (a workshop basement has no sky).</summary>
    public decimal? Latitude { get; set; }
    public decimal? Longitude { get; set; }

    public int? StateId { get; set; }
    public int? DistrictId { get; set; }

    public long CreatedByUserId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public List<FieldServiceLine> Lines { get; set; } = new();
}

public enum FieldLineKind
{
    Used,       // part fitted/consumed from technician stock
    Collected,  // faulty unit collected from the customer (no stock consumption)
}

public class FieldServiceLine
{
    public long Id { get; set; }
    public long FieldServiceId { get; set; }
    public FieldLineKind Kind { get; set; } = FieldLineKind.Used;
    public long PartId { get; set; }
    public int Qty { get; set; } = 1;
    /// <summary>Tax-exclusive, server-set from the rate column the visit's
    /// <see cref="FieldService.RateType"/> names. A client never sends it.</summary>
    public decimal UnitPrice { get; set; }
    /// <summary>Taxable amount: unit price x qty, tax excluded.</summary>
    public decimal Amount { get; set; }
    /// <summary>Snapshotted off the part, so the tax a customer was charged is reproducible after the
    /// master rate changes.</summary>
    public decimal GstPercent { get; set; }
    public decimal TaxAmount { get; set; }
    /// <summary>Used: serial fitted (serial-tracked parts, one unit per line).
    /// Collected: serial of the faulty unit taken from the customer.</summary>
    public string? SerialNo { get; set; }
    /// <summary>Collected lines: technician declared the collected unit faulty.</summary>
    public bool Defective { get; set; }

    public FieldService FieldService { get; set; } = null!;
}

/// <summary>Direct field sale by a field technician (legacy sales_transactions).</summary>
public class FieldSale
{
    public long Id { get; set; }
    public string SaleNo { get; set; } = string.Empty;      // unique (FSLnnnnn)
    public long TechnicianId { get; set; }
    public string CustomerName { get; set; } = string.Empty;
    /// <summary>Set when the technician picked an existing customer rather than typing a new name.
    /// Free text stays authoritative for display; this is what carries ownership onto the serial.</summary>
    public long? CustomerId { get; set; }
    public string? Phone { get; set; }
    public string? Place { get; set; }
    public string? Remarks { get; set; }

    /// <summary>Which rate column the sale bills at - see <see cref="FieldService.RateType"/>.</summary>
    public FieldRateType RateType { get; set; } = FieldRateType.Customer;
    public FieldPaymentMethod? PaymentMethod { get; set; }

    public decimal TaxableAmount { get; set; }
    public decimal TaxAmount { get; set; }
    /// <summary>Taxable + tax, rounded up to the rupee. A sale carries no warranty, so unlike a
    /// service visit the total IS the payable figure and there is no second column for it.</summary>
    public decimal TotalAmount { get; set; }

    public string? LocationLabel { get; set; }
    public decimal? Latitude { get; set; }
    public decimal? Longitude { get; set; }
    public int? StateId { get; set; }
    public int? DistrictId { get; set; }

    public long CreatedByUserId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public List<FieldSaleLine> Lines { get; set; } = new();
}

public class FieldSaleLine
{
    public long Id { get; set; }
    public long FieldSaleId { get; set; }
    public long PartId { get; set; }
    public int Qty { get; set; } = 1;
    /// <summary>Tax-exclusive, server-set from the sale's rate column.</summary>
    public decimal UnitPrice { get; set; }
    /// <summary>Taxable amount: unit price x qty, tax excluded.</summary>
    public decimal Amount { get; set; }
    public decimal GstPercent { get; set; }
    public decimal TaxAmount { get; set; }
    public string? SerialNo { get; set; }                   // serial sold (tracked parts, one unit per line)

    public FieldSale FieldSale { get; set; } = null!;
}
