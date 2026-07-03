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
