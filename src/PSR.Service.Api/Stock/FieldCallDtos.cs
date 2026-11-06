using System.ComponentModel.DataAnnotations;

namespace PSR.Service.Api.Stock;

/// <summary>What a client sends to log a call.
///
/// Four things it deliberately cannot send: the call number (the server's sequence owns it), the
/// engineer (the signed-in account IS the engineer, by design — see
/// <see cref="Data.Entities.FieldCall.EngineerId"/>), the created-at stamp, and any figure at all,
/// because a call carries no money.
///
/// The length attributes are for the generated OpenAPI document and for the reader. Nothing in this
/// API registers a validation filter, so the handler checks them itself rather than trusting them —
/// an unchecked over-length string reaches MySQL and comes back as a 500 instead of a 400.</summary>
/// <param name="CallReceivedDate">Absent means today on the shop's clock. Send a bare date
/// ("2026-10-21"): a value carrying a zone is repaired on arrival by
/// <see cref="Common.ShopClock.BusinessDate(DateTime)"/>, but sending one is asking to be repaired.</param>
/// <param name="AttendedDate">Null while the call has not been attended. Must not fall before
/// <paramref name="CallReceivedDate"/>, which is the one ordering a mis-keyed year shows up in.</param>
/// <param name="CustomerId">Set only when the engineer picked an existing customer off the list. An
/// id that no longer resolves is dropped rather than refused — the typed name still says who it was.</param>
/// <param name="ContactPhone">The number the call came in on. Held on the call rather than looked up
/// from the customer, for the same reason the address is.</param>
/// <param name="ReportedComplaint">What the caller said was wrong. The field a service entry filed
/// against this call prefills its own complaint box from, which is why it is as wide as that one.</param>
public record CreateFieldCallRequest(
    [Required, StringLength(20)] string CallType,
    [Required, StringLength(200)] string CustomerName,
    [StringLength(16)] string? Locality = null,
    long? CustomerId = null,
    [StringLength(500)] string? CustomerAddress = null,
    DateTime? CallReceivedDate = null,
    [StringLength(100)] string? CallReferenceNo = null,
    [StringLength(50)] string? ContactPhone = null,
    [StringLength(1000)] string? ReportedComplaint = null,
    [StringLength(100)] string? MachineModel = null,
    [StringLength(100)] string? MachineSerial = null,
    [StringLength(50)] string? SoftwareVersion = null,
    DateTime? AttendedDate = null,
    [StringLength(16)] string? CallStatus = null,
    [StringLength(1000)] string? Remarks = null);

/// <summary>What a client sends to correct a call already in the register.
///
/// The same shape as <see cref="CreateFieldCallRequest"/>, because the phone shows one form for both
/// and a second shape would be a second set of fields to keep in step. Two things are missing rather
/// than optional: the call number, which the sequence owns and which every reference to the call is
/// written in, and the engineer, because whose day a call was is not a typo to fix — a call attended
/// by somebody else is their record to log, not this one to reassign.
///
/// A PUT, so what arrives is the whole call: a null means that field is now empty, not that the
/// client had nothing to say about it. The two exceptions are the dates and the status, where the
/// form always sends what it is showing and so an omission can only come from a hand-made request —
/// those keep what the call already holds rather than clearing a received date to today or resetting
/// a status nobody touched.</summary>
/// <param name="CallStatus">Forward only, like every other way this status moves. Complete finishes
/// the call; Incomplete against a call that is already Complete is REFUSED rather than quietly
/// ignored. The asymmetry with a visit's own toggle is deliberate — see
/// <see cref="FieldCallRules.Reopening"/>.</param>
public record UpdateFieldCallRequest(
    [Required, StringLength(20)] string CallType,
    [Required, StringLength(200)] string CustomerName,
    [StringLength(16)] string? Locality = null,
    long? CustomerId = null,
    [StringLength(500)] string? CustomerAddress = null,
    DateTime? CallReceivedDate = null,
    [StringLength(100)] string? CallReferenceNo = null,
    [StringLength(50)] string? ContactPhone = null,
    [StringLength(1000)] string? ReportedComplaint = null,
    [StringLength(100)] string? MachineModel = null,
    [StringLength(100)] string? MachineSerial = null,
    [StringLength(50)] string? SoftwareVersion = null,
    DateTime? AttendedDate = null,
    [StringLength(16)] string? CallStatus = null,
    [StringLength(1000)] string? Remarks = null);

/// <summary>What a client sends when marking a day attended. The whole body is optional — a tap with
/// nothing to say sends no body at all, which is the common case and must stay a single tap.</summary>
/// <param name="Remarks">What happened on that day. Sending it again for a day already marked
/// REPLACES that day's note rather than adding a second row: the engineer is correcting what they
/// wrote, because a day can only be attended once. Omitting it on a repeat leaves the existing note
/// alone, so a retry on a dropped connection cannot blank one.</param>
public record MarkAttendedRequest([StringLength(1000)] string? Remarks = null);

/// <summary>One day an engineer was at the call, as the clients read it.</summary>
/// <param name="MarkedByName">Resolved on read, like <see cref="FieldCallDto.EngineerName"/>.</param>
public record FieldCallAttendanceDto(
    long Id,
    DateTime AttendedDate,
    string? Remarks,
    long MarkedByUserId,
    string? MarkedByName,
    DateTime CreatedAt);

/// <summary>One register entry as the clients read it.</summary>
/// <param name="AttendedDate">The FIRST day attended, kept as it always was so that everything built
/// on it still reads the same. <paramref name="Attendances"/> is the full account.</param>
/// <param name="Attendances">Every day the engineer was there, oldest first. Empty on a call logged
/// before the per-day log existed that carries only an <paramref name="AttendedDate"/> — those are
/// not backfilled, because one column cannot say how many visits it stood for.</param>
/// <param name="EngineerName">Resolved on read rather than snapshotted: unlike a rate or a charge
/// description, a person's name is not restated to mean someone else, so the id still points at the
/// right human after a correction to their spelling.</param>
/// <param name="UpdatedAt">Null while the call still says what it said when it was logged. See
/// <see cref="Data.Entities.FieldCall.UpdatedAt"/> for why this is not the attended date.</param>
/// <param name="CompletedAt">When the call closed, or null while it is open. NOT the same as
/// <paramref name="UpdatedAt"/>, and the difference is the point - see
/// <see cref="Data.Entities.FieldCall.CompletedAt"/>.</param>
public record FieldCallDto(
    long Id,
    string CallNo,
    string CallType,
    long EngineerId,
    string? EngineerName,
    DateTime CallReceivedDate,
    string Locality,
    string CustomerName,
    long? CustomerId,
    string? CustomerAddress,
    string? CallReferenceNo,
    string? ContactPhone,
    string? ReportedComplaint,
    string? MachineModel,
    string? MachineSerial,
    string? SoftwareVersion,
    DateTime? AttendedDate,
    string CallStatus,
    string? Remarks,
    DateTime CreatedAt,
    DateTime? UpdatedAt,
    DateTime? CompletedAt,
    List<FieldCallAttendanceDto>? Attendances = null);

// ---------------------------------------------------------------- turnaround

/// <summary>Average, fastest and slowest turnaround over one set of closed calls.
///
/// Hours rather than days, like <see cref="Reports.TatLegStat"/> on the shop-job side: a figure
/// stated in one unit by every endpoint can be formatted into whatever the reader wants, and a
/// same-day call is 4.5 hours rather than 0.2 days. Rounded to a tenth, which is six minutes - any
/// finer is a precision the dates behind it do not have.
///
/// A set with nothing measurable in it comes back as zeros with no call numbers, not as null: the
/// section still has tiles to draw, and "0 calls" is a true and readable answer to a filter that
/// matched nothing.</summary>
/// <param name="FastestCallNo">The call the <paramref name="MinHours"/> figure came from, so the
/// extreme is a record somebody can open rather than a number to go looking for.</param>
public record FieldCallTatStat(
    int CompletedCount,
    double AvgHours,
    double MinHours,
    double MaxHours,
    string? FastestCallNo,
    string? SlowestCallNo);

/// <summary>One engineer's turnaround, and what they still have open.
///
/// Both halves together on purpose: an engineer with the best average on the page may simply be
/// closing the easy calls and leaving the rest, and the open and critical counts beside the average
/// are what make that visible.</summary>
public record FieldCallTatEngineerRow(
    long EngineerId,
    string EngineerName,
    FieldCallTatStat Tat,
    int OpenCount,
    int CriticalCount);

/// <summary>One call that has been open too long, with the server's own reading of how long.
///
/// <paramref name="DaysOpen"/> is stated rather than left for the client to subtract, because the
/// server is what decided the threshold: a reader whose clock runs a few hours behind would
/// otherwise see "6 days" sitting under a seven-day alert and have every reason to think the page
/// was wrong.</summary>
public record FieldCallCriticalRow(int DaysOpen, FieldCallDto Call);

/// <summary>The turnaround section: how long calls take, who takes it, and which ones have not been
/// finished.
///
/// Turnaround is measured from the moment a call was logged to the moment it was marked complete -
/// created_at to completed_at - and so it covers only calls that HAVE closed. The open calls are
/// counted separately and never averaged in: a call still sitting open has no turnaround yet, and
/// folding its age into the average would make a backlog look like fast work the longer it was
/// ignored.</summary>
/// <param name="CriticalDays">The threshold actually applied, which may not be the one asked for -
/// see <see cref="FieldCallTat.CriticalDays"/>.</param>
/// <param name="OpenCount">Every call still Incomplete in the window, critical or not.</param>
/// <param name="CriticalCount">How many of those have been open longer than
/// <paramref name="CriticalDays"/>. Exact, and so not always the length of
/// <paramref name="Critical"/>, which is capped for display.</param>
/// <param name="WindowFull">True when a projection behind these figures came back full, meaning
/// every count and average here is a floor rather than a total. Said out loud for the same reason
/// the register's own lists say it: a number that is quietly partial is worse than one that admits
/// it.</param>
public record FieldCallTatDto(
    int CriticalDays,
    FieldCallTatStat Overall,
    int OpenCount,
    int CriticalCount,
    List<FieldCallTatEngineerRow> Engineers,
    List<FieldCallCriticalRow> Critical,
    bool WindowFull);
