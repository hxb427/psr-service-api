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
/// <param name="CompletedAt">When the technician's work finished — the job's first Completed event.</param>
/// <param name="DispatchedAt">When the unit went back to the customer.</param>
/// <param name="StockedAt">When the unit was taken onto the shop's own shelf instead.</param>
/// <param name="ReplacedAt">When the unit was written off against a whole-unit replacement.</param>
/// <param name="WrittenOffAt">When the unit was declared a total loss.</param>
/// <remarks>The five dates above are read from service_status_history, not from the job: a job stores
/// only the status it is in NOW, so the day it reached any earlier one is recorded nowhere else. Each
/// is the FIRST time the job entered that status. Exactly one of the last four is normally set — the
/// one matching <paramref name="ServiceStatus"/> — and all of them are null on a job still in service.</remarks>
public record ServiceRegisterRow(
    long Id, string ServiceNo, string? ChallanNo, string? InwardDcNo, string? CustomerName, string? CustomerType,
    string SerialNo, string? PsCode, string? ModelName, string? Description, string? ReportedProblem,
    string ServiceStatus, string WarrantyStatus, string PaymentStatus, string Priority, bool IsTotalLoss,
    string? PiNo, DateTime? PiDate, string? InvNo, DateTime? InvDate,
    string? OutwardDcNo, string? OutwardReferenceNo, DateTime? DcDate,
    string? TechnicianName, DateTime DateReceived,
    DateTime? CompletedAt, DateTime? DispatchedAt, DateTime? StockedAt, DateTime? ReplacedAt, DateTime? WrittenOffAt,
    string? TechnicianRemarks);

/// <summary>One status event read back for the register: the job, the status it moved INTO, and when.
/// Internal plumbing for <see cref="ServiceRegisterRow"/>'s dates, never serialized.</summary>
internal record StatusDateRow(long ServiceId, string ToStatus, DateTime ChangedAt);

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

// ----- monthly consumption -----
//
// What the business actually used up, month by month, and who used it. The two existing stock reports
// answer neighbouring questions and neither answers this one: the ledger is a shelf's arithmetic, and
// treats a part issued to a technician as gone the day it was handed over; the analysis splits that
// same movement by technician. Consumption is the other half - stock is used on the day it is fitted
// or sold, by whoever fitted or sold it, and that is the figure a reorder is decided from.

/// <summary>One shop-calendar month's consumption. Months with nothing in them are returned too, as
/// zero rows: a gap in a trend is the thing worth seeing, and a missing month is indistinguishable
/// from a broken report.</summary>
/// <param name="Items">How many distinct parts were consumed that month. "40 units" and "40 units
/// across 31 different parts" are different months and only one of them is a bulk job.</param>
public record ConsumptionMonthRow(
    DateTime Month, string Label, int Service, int Sales, int Other, int Total, int Items);

/// <summary>One part's consumption - either within one month, or across the whole window when
/// <paramref name="Month"/> is null. Both shapes are the same record because the grid that shows them
/// is the same grid: picking a month on the report swaps one list for the other and nothing else.</summary>
/// <param name="OnHand">What the warehouse holds NOW, not at the end of the window. Consumption and
/// the shelf are two different facts and the pair is the useful one: forty used last month with three
/// left is a reorder, forty used with ninety left is not.</param>
public record ConsumptionItemRow(
    DateTime? Month, string? MonthLabel,
    long PartId, string ItemCode, string PartName, string? Unit,
    int Service, int Sales, int Other, int Total, int OnHand);

/// <summary>The monthly consumption report: the window it actually covers, the totals strip, the
/// month rows, and the item rows both per-month and summed over the window.
///
/// <paramref name="From"/> and <paramref name="To"/> are echoed because the caller may have named
/// neither - the default is the trailing twelve months - and a page showing a year of figures over
/// two empty date boxes cannot say what it is showing.
///
/// <paramref name="Items"/> and <paramref name="ItemTotals"/> are both here, and <paramref
/// name="ItemTotals"/> is deliberately not derivable from the other by the client: both are summed on
/// the server out of the same movement set, so they agree by construction. A page that re-added the
/// month rows to get a window total would get a second answer the moment it was filtered or paged.</summary>
public record ConsumptionReportDto(
    DateTime From, DateTime To, string Stream,
    int TotalService, int TotalSales, int TotalOther, int Total,
    List<ConsumptionMonthRow> Months,
    List<ConsumptionItemRow> Items,
    List<ConsumptionItemRow> ItemTotals);

/// <summary>Who consumed one part, and in what capacity. <paramref name="Who"/> is the technician for
/// anything fitted or sold off a technician's own stock, and the user who moved it for anything that
/// went straight off the warehouse shelf - a counter sale, a replacement unit, a write-down.</summary>
public record ConsumptionConsumerRow(string Who, string Kind, string Stream, int Qty);

/// <summary>One consuming movement, as the drill-down lists it: the exact detail behind a figure.</summary>
/// <param name="Qty">Signed, so a reversal row shows as the negative it is rather than looking like a
/// second consumption of the same part.</param>
/// <param name="Reference">The document the movement happened on - job, field visit, field sale or
/// counter sale number - so the row can be traced back to the paperwork. Null for a stock adjustment,
/// which has remarks instead.</param>
/// <param name="Party">The customer or dealer the stock went to, where the document names one. For a
/// sale this is the truest answer to "who consumed it"; for a job it says whose machine it went into.</param>
public record ConsumptionEventRow(
    DateTime At, string Kind, string Stream, int Qty,
    string? Who, string? Reference, string? Party, string? Remarks);

/// <summary>Everything about one part's consumption over the window: the totals, its month-by-month
/// trend, who consumed it, and the movements behind those figures. Fetched per part rather than
/// bundled into the report, because the movement-level detail for every part in a year would be most
/// of the ledger for the one part someone clicked on.</summary>
/// <param name="MovementCount">How many consuming movements there actually were, against the
/// <see cref="Events"/> list which is capped. The figures above the list are summed from all of them,
/// so a reader who could not see that the list was shortened would try to add it up and fail.</param>
public record ConsumptionItemDetailDto(
    long PartId, string ItemCode, string PartName, string? Unit, int WarehouseOnHand,
    DateTime From, DateTime To, string Stream,
    int TotalService, int TotalSales, int TotalOther, int Total,
    List<ConsumptionMonthRow> Months,
    List<ConsumptionConsumerRow> Consumers,
    List<ConsumptionEventRow> Events,
    int MovementCount);
