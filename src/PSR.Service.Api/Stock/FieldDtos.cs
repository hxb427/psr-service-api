using System.ComponentModel.DataAnnotations;

namespace PSR.Service.Api.Stock;

// ---------- stock acknowledgement (technician receipt of issued stock) ----------

public record PendingIssueSerialDto(long IssueSerialId, long ComponentSerialId, string SerialNumber);

/// <summary>One issue waiting to be acknowledged, with the provenance the technician needs to decide
/// whether what is in their hands is what was sent.
///
/// <paramref name="IssuedAt"/> is the movement's own stamp, not the request's <c>IssuedDate</c>:
/// a partially-issued request overwrites that column on every further issue, so it would report the
/// LAST dispatch against the request rather than the date of this consignment. Everything from
/// <paramref name="RequestNo"/> through <paramref name="IssuedByName"/> is null on a direct issue,
/// which has no request behind it.</summary>
public record PendingIssueDto(
    long MovementId, long PartId, string ItemCode, string PartName, int Qty,
    string? RequestNo, DateTime? RequestedAt,
    string? Courier, string? TrackingNo, string? DcNo, string? IssuedByName,
    DateTime IssuedAt,
    List<PendingIssueSerialDto> Serials);

public record AckSerialLine(long IssueSerialId, [Required] string Status);   // Received | Defective | Missing

public record AckIssueRequest(
    [Range(0, 1_000_000)] int QtyReceived,
    [Range(0, 1_000_000)] int QtyDefective = 0,
    [Range(0, 1_000_000)] int QtyMissing = 0,
    [StringLength(500)] string? Remarks = null,
    List<AckSerialLine>? Serials = null);

// ---------- returns with serials + courier ----------

/// <param name="Defective">What the technician said when they shipped it.</param>
/// <param name="Status">The unit's current lifecycle status.</param>
/// <param name="AckStatus">What the service centre found when it opened the box, or null while this
/// unit is still awaiting booking. Per unit, because a box of ten can arrive with eight in it.</param>
public record StockReturnSerialDto(
    long ComponentSerialId, string SerialNumber, bool Defective, string Status, string? AckStatus = null);

/// <summary>One unit's verdict at the receiving end: Received | Defective | Missing.</summary>
public record AckStockReturnSerialLine(long ComponentSerialId, [Required] string Status);

/// <summary>Book a return shipment in, whole or in part.
///
/// An empty body means what this endpoint has always meant - everything arrived exactly as sent -
/// so clients built before partial receipt existed keep working unchanged.
///
/// <para><paramref name="Serials"/> books in only the units it names, on a serial-tracked shipment.
/// The shipment stays Pending until the last unit is resolved, so a box that turns up a day late
/// does not force the nine that arrived to wait.</para>
///
/// <para><paramref name="QtyReceived"/> is the quantity-only equivalent and is refused on a
/// serial-tracked shipment, where the units are the count. It may not exceed what was sent: a
/// receipt is a statement about a shipment, not a way to conjure stock.</para></summary>
public record AckStockReturnRequest(
    [Range(0, 1_000_000)] int? QtyReceived = null,
    List<AckStockReturnSerialLine>? Serials = null,
    [StringLength(500)] string? Remarks = null);

// ---------- technician transfers ----------

public record CreateTransferLine(
    [Required] long PartId,
    [Range(1, 1_000_000)] int Qty,
    List<long>? SerialIds = null);

public record CreateTransferRequest(
    [Required] long ToTechnicianId,
    [StringLength(500)] string? Remarks,
    [MinLength(1)] List<CreateTransferLine> Lines);

public record TransferSerialDto(long TransferSerialId, long ComponentSerialId, string SerialNumber, string? AckStatus);

public record TransferLineDto(
    long Id, long PartId, string ItemCode, string PartName, int Qty,
    int? QtyReceived, int? QtyDefective, int? QtyMissing, List<TransferSerialDto> Serials);

public record TransferDto(
    long Id, string TransferNo, long FromTechnicianId, string? FromTechnicianName,
    long ToTechnicianId, string? ToTechnicianName, string Status, string? Remarks,
    DateTime CreatedAt, DateTime? AcknowledgedAt, List<TransferLineDto> Lines);

public record AckTransferLine(
    [Required] long LineId,
    [Range(0, 1_000_000)] int QtyReceived,
    [Range(0, 1_000_000)] int QtyDefective = 0,
    [Range(0, 1_000_000)] int QtyMissing = 0,
    List<AckSerialLine>? Serials = null);   // IssueSerialId here = TransferSerialId

public record AckTransferRequest([MinLength(1)] List<AckTransferLine> Lines);

// ---------- field services / field sales ----------

public record FieldServiceLineRequest(
    [Required] string Kind,                 // Used | Collected
    [Required] long PartId,
    [Range(1, 1_000_000)] int Qty = 1,
    [StringLength(128)] string? SerialNo = null,
    bool Defective = false,
    /// <summary>Whether to put this part's money on what the customer owes. The last word is
    /// <see cref="FieldLineRules.Billable"/>'s, not this field's: a collected unit and a serial-tracked
    /// one are never billed whatever is sent here.
    ///
    /// True when absent, which is what a client written before this field existed meant — every fitted
    /// part went on the bill. A build that knows about the checkbox always says, and says false for a
    /// part nobody ticked.</summary>
    bool Billable = true);

/// <summary>Everything about a visit except what it cost. There is deliberately no field on this
/// request for a rate, a tax or a total: the caller names the parts, the quantity, which rate column to
/// bill at and which service charge was agreed, and the server prices all of it from its own catalogue.
/// A client that could send a figure is a client that could send any figure.</summary>
public record CreateFieldServiceRequest(
    [Required, StringLength(200)] string CustomerName,
    [StringLength(50)] string? Phone,
    [StringLength(200)] string? Place,
    [StringLength(100)] string? MachineSerial,
    [StringLength(1000)] string? Complaint,
    [StringLength(1000)] string? WorkDone,
    [StringLength(500)] string? Remarks,
    List<FieldServiceLineRequest>? Lines = null,
    /// <summary>Set when the technician picked an existing customer instead of typing a new name.
    /// Optional: a walk-in who is not on the list still gets served, and CustomerName carries them.</summary>
    long? CustomerId = null,
    /// <summary>The Poornasree ticket this visit closed out, when the job came from one. The app sends
    /// both halves; neither is verified here, because the ticket lives in another system that has
    /// already refused the call if the caller was not its assigned engineer.</summary>
    [StringLength(36)] string? TicketId = null,
    [StringLength(64)] string? TicketNumber = null,
    /// <summary>What the technician found behind the complaint, as opposed to how the customer
    /// described the symptom.</summary>
    [StringLength(1000)] string? Cause = null,
    /// <summary>Customer | Dealer. Absent means Customer.</summary>
    [StringLength(16)] string? RateType = null,
    /// <summary>The agreed service charge, by id. Its description and figure are read from the
    /// catalogue and snapshotted onto the record - the request does not get to say what it cost.
    ///
    /// Superseded by <paramref name="ServiceChargeIds"/> and kept for builds that only ever sent one.
    /// Read only when that list is absent; a client that sends both means the list.</summary>
    long? ServiceChargeId = null,
    /// <summary>Cash | Upi | BankTransfer. Absent for a visit nobody paid for on the spot.</summary>
    [StringLength(20)] string? PaymentMethod = null,
    /// <summary>In | Out | Unknown. Optional - the app offers the factory record's answer as a default
    /// and the technician may leave it alone, change it, or not answer.</summary>
    [StringLength(10)] string? WarrantyStatus = null,
    /// <summary>The place name the technician picked from the reverse-geocoded candidates.</summary>
    [StringLength(200)] string? LocationLabel = null,
    /// <summary>The fix behind that label. Both halves or neither.</summary>
    decimal? Latitude = null,
    decimal? Longitude = null,
    int? StateId = null,
    int? DistrictId = null,
    /// <summary>The call register entry this visit answers, when the technician picked one off their
    /// open calls. Unlike <paramref name="CustomerId"/>, an id that does not resolve is REFUSED
    /// rather than dropped: it was a deliberate pick with a consequence for the register, and losing
    /// it quietly would leave the call open as though nobody had been.</summary>
    long? FieldCallId = null,
    /// <summary>Complete | Incomplete - the engineer's answer to "did I finish the job", applied to
    /// the call named above. Absent means Incomplete, which leaves the call open for the next visit.
    /// Ignored when no call is linked, there being nothing for it to say anything about.
    ///
    /// Only ever completes a call, never reopens one; see
    /// <see cref="PSR.Service.Api.Data.Entities.FieldCallStatus"/>.</summary>
    [StringLength(16)] string? CallStatus = null,
    /// <summary>Every service charge agreed for this visit, by id. One visit routinely does several
    /// priced jobs, and billing one of them meant the rest were given away or buried in the parts.
    ///
    /// Each is priced and snapshotted from the catalogue, exactly as the single charge was. A repeated
    /// id is collapsed rather than billed twice: the form offers each charge once, so a duplicate is a
    /// double-tap or a replayed request, never an instruction to charge for it again.
    ///
    /// Absent (not empty) falls back to <paramref name="ServiceChargeId"/>, which is what an older
    /// build sends. Empty means what it says — no charge on this visit.</summary>
    List<long>? ServiceChargeIds = null,
    /// <summary>Whether to add the office's standing field visit charge to this visit. The figure is
    /// the one in settings; the request does not get to say what it is.
    ///
    /// False when absent, because that is what a client written before the charge existed meant. The
    /// phone defaults the box to ticked, so the technician has to take it off rather than put it on —
    /// but a build that has never heard of it must not start charging for attendance on its own, with
    /// a quote on screen that says otherwise.</summary>
    bool ApplyVisitCharge = false);

/// <summary>Money is nullable throughout and that is the role projection, not missing data - see
/// <see cref="PSR.Service.Api.Auth.PricingVisibility"/>. <c>Amount</c> is tax-exclusive;
/// <c>LineTotal</c> is what the line contributes to the bill.</summary>
public record FieldServiceLineDto(
    long Id, string Kind, long PartId, string ItemCode, string PartName, int Qty,
    string? SerialNo, bool Defective, decimal? UnitPrice, decimal? Amount,
    decimal? GstPercent = null, decimal? TaxAmount = null, decimal? LineTotal = null,
    string? HsnCode = null,
    /// <summary>Whether this line's money is in the visit's totals. A line that is priced and NOT
    /// billable is a part the shop handed over without charging for; the figures say what it was
    /// worth, and this says the customer was not asked for it.</summary>
    bool Billable = false);

/// <summary>One service charge as it was billed on a visit — the snapshot, not the catalogue row, so
/// it still reads as what the customer was told after the charge is repriced or retired.</summary>
public record FieldServiceChargeDto(
    long ServiceChargeId, string? Description,
    decimal? Amount, decimal? TaxPercent, decimal? TaxAmount);

/// <summary><c>Total</c> is the stored visit total, not a sum taken on read, so it still says what the
/// customer was told after the parts master moves on. <c>AmountPayable</c> is what they owed for it -
/// nil on an in-warranty job, where the work still has a value but the customer does not pay it.</summary>
public record FieldServiceDto(
    long Id, string ServiceNo, long TechnicianId, string? TechnicianName,
    string CustomerName, string? Phone, string? Place, string? MachineSerial,
    string? Complaint, string? WorkDone, string? Remarks, DateTime CreatedAt,
    decimal? Total, List<FieldServiceLineDto> Lines,
    string? TicketId = null, string? TicketNumber = null,
    string? Cause = null,
    string RateType = "Customer",
    /// <summary>The visit's charges rolled up into the single-charge shape older clients read: the id
    /// and tax percent only where there is exactly one charge, the description joined, the amount
    /// summed. Derived from <c>Charges</c>, which is where the charges actually live — a client that
    /// knows about the list should read that instead.</summary>
    long? ServiceChargeId = null, string? ServiceChargeDescription = null,
    decimal? ServiceChargeAmount = null, decimal? ServiceChargeTaxPercent = null,
    string? PaymentMethod = null, string? WarrantyStatus = null,
    decimal? TaxableAmount = null, decimal? TaxAmount = null, decimal? AmountPayable = null,
    string? LocationLabel = null, decimal? Latitude = null, decimal? Longitude = null,
    int? StateId = null, string? StateName = null,
    int? DistrictId = null, string? DistrictName = null,
    /// <summary>The call this visit was filed against, and that call's number and status AFTER the
    /// save. Echoed back rather than left for the client to assume: the status is the server's to
    /// decide (an already-complete call is not reopened), so the only honest way for the app to tell
    /// the technician what became of their call is to be told.</summary>
    long? FieldCallId = null, string? CallNo = null, string? CallStatus = null,
    /// <summary>Every service charge this visit billed. The authoritative list — the four
    /// <c>ServiceCharge*</c> fields above are a roll-up of it for clients that only know about one
    /// charge, and a visit with two charges rolls up to their sum under a joined description.</summary>
    List<FieldServiceChargeDto>? Charges = null,
    /// <summary>The standing visit charge, as applied to this visit. <c>Applied</c> rather than a
    /// non-zero amount is what says the technician charged for attendance: the office may have the
    /// figure set to nil, and a visit charged at nil is a different fact from one where the box was
    /// taken off.</summary>
    bool VisitChargeApplied = false, decimal? VisitChargeAmount = null);

/// <summary>Whether a ticket already has a service recorded against it. The app's OTP step is gated on
/// this, and asking the server rather than trusting its own memory is what makes the gate survive a
/// reinstall, a second device, or the technician killing the app between the form and the OTP.</summary>
public record TicketServiceStatusDto(string TicketId, bool HasService, List<string> ServiceNos);

public record FieldSaleLineRequest(
    [Required] long PartId,
    [Range(1, 1_000_000)] int Qty = 1,
    [StringLength(128)] string? SerialNo = null);

/// <summary>As with a service visit, the request carries no figure of its own - see
/// <see cref="CreateFieldServiceRequest"/>. A sale has no complaint, cause, warranty or service charge:
/// goods changed hands, and that is all there is to say about it.</summary>
public record CreateFieldSaleRequest(
    [Required, StringLength(200)] string CustomerName,
    [StringLength(50)] string? Phone,
    [StringLength(200)] string? Place,
    [StringLength(500)] string? Remarks,
    [MinLength(1)] List<FieldSaleLineRequest> Lines,
    long? CustomerId = null,
    [StringLength(16)] string? RateType = null,
    [StringLength(20)] string? PaymentMethod = null,
    [StringLength(200)] string? LocationLabel = null,
    decimal? Latitude = null,
    decimal? Longitude = null,
    int? StateId = null,
    int? DistrictId = null);

public record FieldSaleLineDto(
    long Id, long PartId, string ItemCode, string PartName, int Qty,
    string? SerialNo, decimal? UnitPrice, decimal? Amount,
    decimal? GstPercent = null, decimal? TaxAmount = null, decimal? LineTotal = null,
    string? HsnCode = null);

public record FieldSaleDto(
    long Id, string SaleNo, long TechnicianId, string? TechnicianName,
    string CustomerName, string? Phone, string? Place, string? Remarks, DateTime CreatedAt,
    decimal? Total, List<FieldSaleLineDto> Lines,
    string RateType = "Customer", string? PaymentMethod = null,
    decimal? TaxableAmount = null, decimal? TaxAmount = null,
    string? LocationLabel = null, decimal? Latitude = null, decimal? Longitude = null,
    int? StateId = null, string? StateName = null,
    int? DistrictId = null, string? DistrictName = null);

public record TransferTechnicianDto(long Id, string Username, string? FullName, bool IsFieldTechnician);

// ---------- technician's available serials (pick lists) ----------

public record AvailableSerialDto(long Id, long PartId, string ItemCode, string? ItemName, string SerialNumber, string Status);
