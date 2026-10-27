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

/// <summary>One register entry as the clients read it.</summary>
/// <param name="EngineerName">Resolved on read rather than snapshotted: unlike a rate or a charge
/// description, a person's name is not restated to mean someone else, so the id still points at the
/// right human after a correction to their spelling.</param>
/// <param name="UpdatedAt">Null while the call still says what it said when it was logged. See
/// <see cref="Data.Entities.FieldCall.UpdatedAt"/> for why this is not the attended date.</param>
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
    DateTime? UpdatedAt);
