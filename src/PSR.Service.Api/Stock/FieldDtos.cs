using System.ComponentModel.DataAnnotations;

namespace PSR.Service.Api.Stock;

// ---------- stock acknowledgement (technician receipt of issued stock) ----------

public record PendingIssueSerialDto(long IssueSerialId, long ComponentSerialId, string SerialNumber);

public record PendingIssueDto(
    long MovementId, long PartId, string ItemCode, string PartName, int Qty,
    string? RequestNo, string? Courier, string? TrackingNo, DateTime IssuedAt,
    List<PendingIssueSerialDto> Serials);

public record AckSerialLine(long IssueSerialId, [Required] string Status);   // Received | Defective | Missing

public record AckIssueRequest(
    [Range(0, 1_000_000)] int QtyReceived,
    [Range(0, 1_000_000)] int QtyDefective = 0,
    [Range(0, 1_000_000)] int QtyMissing = 0,
    [StringLength(500)] string? Remarks = null,
    List<AckSerialLine>? Serials = null);

// ---------- returns with serials + courier ----------

public record StockReturnSerialDto(long ComponentSerialId, string SerialNumber, bool Defective, string Status);

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
    bool Defective = false);

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
    [StringLength(64)] string? TicketNumber = null);

public record FieldServiceLineDto(
    long Id, string Kind, long PartId, string ItemCode, string PartName, int Qty,
    string? SerialNo, bool Defective, decimal? UnitPrice, decimal? Amount);

public record FieldServiceDto(
    long Id, string ServiceNo, long TechnicianId, string? TechnicianName,
    string CustomerName, string? Phone, string? Place, string? MachineSerial,
    string? Complaint, string? WorkDone, string? Remarks, DateTime CreatedAt,
    decimal? Total, List<FieldServiceLineDto> Lines,
    string? TicketId = null, string? TicketNumber = null);

/// <summary>Whether a ticket already has a service recorded against it. The app's OTP step is gated on
/// this, and asking the server rather than trusting its own memory is what makes the gate survive a
/// reinstall, a second device, or the technician killing the app between the form and the OTP.</summary>
public record TicketServiceStatusDto(string TicketId, bool HasService, List<string> ServiceNos);

public record FieldSaleLineRequest(
    [Required] long PartId,
    [Range(1, 1_000_000)] int Qty = 1,
    [StringLength(128)] string? SerialNo = null);

public record CreateFieldSaleRequest(
    [Required, StringLength(200)] string CustomerName,
    [StringLength(50)] string? Phone,
    [StringLength(200)] string? Place,
    [StringLength(500)] string? Remarks,
    [MinLength(1)] List<FieldSaleLineRequest> Lines,
    long? CustomerId = null);

public record FieldSaleLineDto(
    long Id, long PartId, string ItemCode, string PartName, int Qty,
    string? SerialNo, decimal? UnitPrice, decimal? Amount);

public record FieldSaleDto(
    long Id, string SaleNo, long TechnicianId, string? TechnicianName,
    string CustomerName, string? Phone, string? Place, string? Remarks, DateTime CreatedAt,
    decimal? Total, List<FieldSaleLineDto> Lines);

public record TransferTechnicianDto(long Id, string Username, string? FullName, bool IsFieldTechnician);

// ---------- technician's available serials (pick lists) ----------

public record AvailableSerialDto(long Id, long PartId, string ItemCode, string? ItemName, string SerialNumber, string Status);
