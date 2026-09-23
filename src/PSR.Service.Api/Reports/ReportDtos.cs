namespace PSR.Service.Api.Reports;

// ----- technician performance -----
public record TechPerformanceRow(
    long TechnicianId, string TechnicianName, int CompletedJobs, int DistinctWorkDays, int PartsConsumed);

public record TechPerformanceDetail(
    long TechnicianId, string TechnicianName,
    int Issued, int Consumed, int Returned, int Adjusted,
    int CompletedJobs, int DistinctWorkDays,
    List<TechRecentJobRow> RecentJobs);

public record TechRecentJobRow(
    long ServiceId, string ServiceNo, string? CustomerName, string? Description, string SerialNo,
    string ServiceStatus, DateTime? CompletedAt);

// ----- parts used -----
public record PartsUsedReportRow(
    long TechnicianId, string TechnicianName, string ItemCode, string PartName, int Quantity);

// ----- held items (old "missing items ledger": nonzero technician balances) -----
public record HeldItemRow(
    long TechnicianId, string TechnicianName, string ItemCode, string PartName, int OnHand);

// ----- service register (master export / global search) -----
public record ServiceRegisterRow(
    long Id, string ServiceNo, string? ChallanNo, string? InwardDcNo, string? CustomerName, string? CustomerType,
    string SerialNo, string? PsCode, string? ModelName, string? Description, string? ReportedProblem,
    string ServiceStatus, string WarrantyStatus, string PaymentStatus, string Priority, bool IsTotalLoss,
    string? PiNo, DateTime? PiDate, string? InvNo, DateTime? InvDate,
    string? OutwardDcNo, string? OutwardReferenceNo, DateTime? DcDate,
    string? TechnicianName, DateTime DateReceived, string? TechnicianRemarks);

// ----- serial ledger (where each deployed serial-tracked unit is) -----
public record SerialReportRow(
    string SerialNumber, string ItemCode, string PartName, string Status,
    string OwnerType, string? OwnerRef, string? TechnicianName, DateTime? LastUpdatedAt);

// ----- daily summary -----
public record DailySummaryDto(
    DateTime Date, int ReceivedCount,
    int ServicePending, int PiPending, int PaymentPending, int DispatchPending, int DispatchedToday,
    List<DailyTechBreakdownRow> Technicians);

public record DailyTechBreakdownRow(string TechnicianName, int Count, List<DailyTechItemRow> Items);
public record DailyTechItemRow(string Description, int Count);

// ----- TAT analysis -----
// Four legs computed from service_status_history timestamps (first event per status per job):
//   received_to_dispatch, received_to_completion, started_to_completed, completed_to_dispatch.
public record TatLegStat(string Key, string Label, int Count, double AvgHours, double MinHours, double MaxHours);

public record TatJobRow(
    long ServiceId, string ServiceNo, string? Description, string? CustomerName, string? TechnicianName,
    DateTime ReceivedAt, DateTime? StartedAt, DateTime? CompletedAt, DateTime? DispatchedAt,
    double? ReceivedToDispatchHours, double? ReceivedToCompletionHours,
    double? StartedToCompletedHours, double? CompletedToDispatchHours);

public record TatReportDto(List<TatLegStat> Legs, List<TatJobRow> Rows);

// ----- stock ledger -----
/// <summary>One part's warehouse movement over a window: what was on the shelf when it opened, what
/// went on and came off it, and what is left. Every figure is derived from the movement ledger rather
/// than read off the balance table, so the row adds up on its own — opening + input - outward ==
/// closing — and a window ending today closes on the same number the warehouse page shows.
///
/// Input and outward are the two directions and nothing finer. The ledger used to break them into
/// inward, issued, returned, sold and adjusted, which asked the reader to know that a sale un-marked
/// in error lands in "returned" and that "adjusted" is signed before the row could be checked. The
/// store's question is what went on the shelf and what came off it; which door it used is on the
/// movement history for the part.</summary>
public record StockLedgerRow(
    long PartId, string ItemCode, string PartName, string? Unit,
    int Opening, int Input, int Outward, int Closing);

/// <summary>The ledger plus the totals strip above it, so the page does not have to re-add the column
/// it is already showing (and get a different answer once the list is paged or filtered).</summary>
public record StockLedgerDto(
    DateTime? From, DateTime? To,
    int TotalOpening, int TotalInput, int TotalOutward, int TotalClosing,
    List<StockLedgerRow> Rows);

// ----- detailed stock analysis -----
/// <summary>What the warehouse took in over the window, part by part.</summary>
public record StockInwardRow(long PartId, string ItemCode, string PartName, string? Unit, int Received);

/// <summary>One technician's position on one part: what they were given, what they actually used, and
/// what is still on them. Consumed is the net of Consumption and its reversal — a completed job that
/// was reverted puts the parts back on the technician, and counting the original consumption alone
/// would read as usage that never happened. What went back to the warehouse is not a column here: it
/// is already the difference between issued and used + on hand, and the warehouse's own side of it is
/// the ledger's input.</summary>
public record TechStockRow(
    long TechnicianId, string TechnicianName, long PartId, string ItemCode, string PartName,
    int Issued, int Consumed, int OnHand);

public record StockAnalysisDto(List<StockInwardRow> Inward, List<TechStockRow> Technicians);
