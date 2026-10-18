using System.Linq.Expressions;
using PSR.Service.Api.Data.Entities;

namespace PSR.Service.Api.Reports;

/// <summary>Where the stock went when it left the warehouse. The split exists because the store's
/// questions — how much are we handing to technicians, how much are we selling over the counter, how
/// much are we writing off — are all answered out of the same ledger and used to be impossible to
/// tell apart on it.</summary>
internal enum ConsumptionStream
{
    /// <summary>Dispatched or handed to a technician. The biggest column by far on most months: it is
    /// how nearly all stock leaves the building, whether it is later fitted, sold in the field or
    /// brought back.</summary>
    Technicians,

    /// <summary>Sold as goods straight off the shelf — a counter or dealer spare sale.</summary>
    Sales,

    /// <summary>A whole replacement unit shipped out against a job.</summary>
    Service,

    /// <summary>Written off the shelf without going anywhere — a stock-take that found fewer than the
    /// book said, or a serial correction that took the count down. Small, and deliberately not folded
    /// into the other columns: a write-down counted as an issue reads as stock a technician is
    /// holding.</summary>
    Shrinkage,
}

/// <summary>What one ledger row means to the consumption report: which way the stock went, what kind
/// of event it was, and how many units left the warehouse.
///
/// <paramref name="Consumed"/> is always strictly positive. The report counts outflow and nothing
/// else, so there is no row anywhere in the ledger that can make one of its figures smaller — see
/// <see cref="ConsumptionRules.Effect"/>.</summary>
internal readonly record struct ConsumptionEffect(ConsumptionStream Stream, string Kind, int Consumed);

/// <summary>The consumption report's arithmetic, kept apart from the endpoint that serves it so the
/// rules can be pinned by tests without a database behind them.
///
/// CONSUMPTION HERE MEANS GROSS WAREHOUSE OUTFLOW: every movement that takes stock off the warehouse
/// shelf, and only those. Stock issued to a technician is consumed on the day it is dispatched, not
/// the day it is fitted — it has left the warehouse, and the warehouse is what this report is about.
///
/// Nothing that puts stock BACK is counted: a return, a sale un-marked, a reverted job, a cancelled
/// replacement, a stock-take that found more. That is the whole reason this report cannot show a
/// negative figure. The older version of this rule netted those reversals off, which is defensible
/// arithmetic and unreadable as a report: a sale returned this month against one made before the
/// window started left a month reading below zero, and a positive stock correction turned up as
/// negative consumption. A month cannot consume minus four boards.
///
/// The cost of counting gross is that outflow and usage are not the same number and this report only
/// answers the first. Six boards issued to a technician who fits two are six here. StockAnalysis is
/// the report for the other question — its Issued and Used columns side by side — and the gap between
/// them is what a technician is carrying.
///
/// This is therefore the same question <see cref="ReportsEndpoints"/>'s WarehouseDelta answers, and
/// deliberately so: this report's total is the stock ledger's Outward column, broken out by month, by
/// part, and down to the document behind every movement. If the two ever disagree, one of them has a
/// movement type the other does not.</summary>
internal static class ConsumptionRules
{
    // The kind labels, as the report prints them. Here rather than inline so the grid, the drill-down
    // and the workbook cannot drift into three spellings of the same event.
    internal const string IssuedToTechnician = "Issued to technician";
    internal const string CounterSale = "Counter sale";
    internal const string UnitReplacement = "Unit replacement";
    internal const string StockWriteDown = "Stock write-down";

    /// <summary>How many months the report covers when the caller names no window. Twelve, so the
    /// page opens on a year of months and a seasonal item's figure has something to be read against;
    /// an unbounded default would list every month the shop has ever traded.</summary>
    internal const int DefaultMonths = 12;

    /// <summary>How a ledger row lands on the consumption report, or null when it is not stock leaving
    /// the warehouse.
    ///
    /// Four types do, and they are exactly the four that take the warehouse balance down: an Issue to
    /// a technician, a Sale off the shelf, a Replacement unit shipped, and the negative half of an
    /// Adjustment.
    ///
    /// What is deliberately NOT here, and why each one would be wrong:
    ///
    /// Consumption and ConsumptionReversal are the technician's stock being used up, not the
    /// warehouse's. Those units were already counted the day they were issued, so counting them again
    /// when they are fitted reports the same board twice.
    ///
    /// LossInTransit moves no warehouse balance at all — the Issue it belongs to already debited the
    /// shelf, and this row only records that the shipment never arrived. Counting it would
    /// double-count the loss. It is still the right figure for "what did we lose", which is the stock
    /// analysis's question, not this one.
    ///
    /// Receipt, Return, SaleReturn, SaleUnsold and ReplacementReturn all put stock back on the shelf.
    /// They are the ledger's Input, and subtracting them here is what used to make this report go
    /// negative.
    ///
    /// IssueReceipt, ReturnDispatched, DefectiveOnArrival and the three Transfer types never touch the
    /// warehouse: they move stock between technicians, or between a technician and a courier. ShelfIn
    /// is shelf-side, and so is any part of a row covered by <paramref name="shelfQty"/>.
    ///
    /// ShelfTransfer is the one deliberate hole in "this report equals the ledger's Outward column".
    /// It takes quantity off the warehouse without anything leaving the building: units already
    /// sitting on the shelf stopped being counted as warehouse stock when the two pools were
    /// separated. Nobody received them and nothing was used, so counting it would put a one-off spike
    /// of phantom consumption in the month the change was deployed and leave it on the report for
    /// ever. ConsumptionRuleTests names this exemption so it stays a decision rather than a gap.
    ///
    /// <paramref name="shelfQty"/> is how much of the row came off the SHELF rather than the
    /// warehouse, and it is subtracted before anything else happens. A machine the shop kept off a
    /// finished job was never counted as warehouse stock, so issuing it to a technician or handing it
    /// over as a replacement takes nothing off the warehouse and belongs nowhere on this report. An
    /// issue made up of two shelf units and one new one consumes one. That keeps this report equal to
    /// the stock ledger's Outward column, which is the property the whole thing is defined by - and
    /// it does mean the Technicians figure here reads lower than what technicians actually received,
    /// which is StockAnalysis's question rather than this one.</summary>
    internal static ConsumptionEffect? Effect(MovementType type, int qty, int shelfQty = 0)
    {
        var warehouseQty = qty - Math.Clamp(shelfQty, 0, Math.Max(qty, 0));
        ConsumptionEffect? effect = type switch
        {
            MovementType.Issue => new ConsumptionEffect(ConsumptionStream.Technicians, IssuedToTechnician, warehouseQty),
            MovementType.Sale => new ConsumptionEffect(ConsumptionStream.Sales, CounterSale, warehouseQty),
            MovementType.Replacement => new ConsumptionEffect(ConsumptionStream.Service, UnitReplacement, warehouseQty),

            // The one movement type stored already signed, and the only one whose quantity is not a
            // magnitude. A stock-take that found three FEWER wrote -3 and took three off the shelf, so
            // it consumed three. One that found three more put stock back, and falls out below.
            MovementType.Adjustment => new ConsumptionEffect(ConsumptionStream.Shrinkage, StockWriteDown, -qty),

            _ => null,
        };

        // The report's whole invariant, in one place. Every figure on it — each month, each part, the
        // totals strip, the drill-down — is a sum of these, so if no single effect can be zero or
        // negative then no cell anywhere on the report can be either. This is also what filters the
        // positive half of Adjustment out, which is why the database predicate below fetches more rows
        // than this function keeps.
        return effect is { Consumed: > 0 } ? effect : null;
    }

    /// <summary>The movement types <see cref="Effect"/> can return something for, as a predicate the
    /// database applies before any of this runs. Issues aside, the bulk of the ledger is receipts,
    /// receipts-of-issue, returns and consumption rows, and none of those is warehouse outflow, so
    /// leaving them in the database rather than fetching a year of them to discard is most of this
    /// report's cost.
    ///
    /// Adjustment is the one type this over-fetches: the sign decides whether it counts and SQL cannot
    /// be asked for "the negative ones" without the rule and the predicate disagreeing on what
    /// negative means. Manual corrections are rare enough that fetching both halves is cheaper than
    /// the drift would be.
    ///
    /// Two statements of one fact, which is a drift waiting to happen: a type added to
    /// <see cref="Effect"/> and not here would simply never appear on the report, and nothing would
    /// fail. ConsumptionRuleTests walks every <see cref="MovementType"/> and asserts the two agree,
    /// so the drift is a red test rather than a column that quietly reads low.
    ///
    /// The <c>Quantity > ShelfQty</c> clause is this predicate's one piece of arithmetic, and it is
    /// here rather than left to <see cref="Effect"/> because a row made up entirely of shelf units is
    /// common once the shelf is in use - every issue filled off it - and fetching a year of them to
    /// discard would undo the point of having a predicate at all. A mixed row still comes back, and
    /// Effect takes the shelf part off it.</summary>
    internal static readonly Expression<Func<StockMovement, bool>> IsConsuming = m =>
        ((m.MovementType == MovementType.Issue
          || m.MovementType == MovementType.Sale
          || m.MovementType == MovementType.Replacement)
         && m.Quantity > m.ShelfQty)
        || m.MovementType == MovementType.Adjustment;

    /// <summary>The stream the caller asked for, or null for all of them combined.</summary>
    internal static ConsumptionStream? ParseStream(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "technicians" or "techs" or "issues" => ConsumptionStream.Technicians,
        "sales" => ConsumptionStream.Sales,
        "service" => ConsumptionStream.Service,
        // "other" is what this column was called when it also held transit losses. Accepted so a saved
        // link or an older desktop build lands on the column that replaced it rather than on everything.
        "shrinkage" or "other" => ConsumptionStream.Shrinkage,
        // "all", "combined", missing, or anything unrecognised. An unknown value showing everything is
        // the safe way to be wrong: the alternative is a page of zeroes that reads like a quiet month.
        _ => null,
    };

    /// <summary>How a stream is named back to the caller, so the page and the workbook label the
    /// window with the server's own word for it rather than echoing what was typed.</summary>
    internal static string StreamName(ConsumptionStream? stream) => stream switch
    {
        ConsumptionStream.Technicians => "technicians",
        ConsumptionStream.Sales => "sales",
        ConsumptionStream.Service => "service",
        ConsumptionStream.Shrinkage => "shrinkage",
        _ => "all",
    };

    /// <summary>The shop-calendar month a stored instant falls in, as that month's first day.
    ///
    /// On the shop's clock, not the server's, and this is the one piece of arithmetic the whole report
    /// stands on. Movements are stored UTC; the shop is five and a half hours ahead. Stock issued at
    /// half past seven on the evening of the 31st is stored as the 1st, so bucketing the raw timestamp
    /// files it under the following month — October would be short of a day it worked and November
    /// would carry a day it had not started. Every movement after 18:30 local is affected, which is
    /// most of an evening shift.</summary>
    internal static DateTime MonthOf(DateTime stored, double shopOffsetHours)
    {
        var local = stored.AddHours(shopOffsetHours);
        return new DateTime(local.Year, local.Month, 1);
    }

    /// <summary>"Oct 2026". Built here so the grid, the drill-down and the workbook share one spelling
    /// of a month, and so a client in another culture cannot render the shop's months three ways.</summary>
    internal static string MonthLabel(DateTime month) =>
        month.ToString("MMM yyyy", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The shop-local days the report covers, filling in whichever end the caller left out:
    /// the trailing <see cref="DefaultMonths"/> months ending today, aligned to the first of a month
    /// so the oldest bucket is a whole month rather than a fortnight that reads like a slow one.</summary>
    internal static (DateTime From, DateTime To) Window(DateTime? from, DateTime? to, double shopOffsetHours)
    {
        var today = DateTime.UtcNow.AddHours(shopOffsetHours).Date;
        var end = to?.Date ?? today;
        var start = from?.Date ?? new DateTime(end.Year, end.Month, 1).AddMonths(-(DefaultMonths - 1));
        // A window handed over backwards would otherwise come back as a silent zero.
        return start <= end ? (start, end) : (end, start);
    }

    /// <summary>The half-open range of STORED instants covering a run of shop-local days.
    ///
    /// The other stock reports compare a shop-local date straight against a UTC column, which quietly
    /// lets the last five and a half hours of the window's final day fall outside it. That is a
    /// rounding error on a report whose window is a date range; on one whose rows ARE months it is the
    /// report, so this one converts properly. The cost is that the two can disagree by an evening's
    /// movements at a window edge, which is the right way round — this one is correct.</summary>
    internal static (DateTime From, DateTime ToExclusive) StoredRange(
        DateTime from, DateTime to, double shopOffsetHours) =>
        (from.AddHours(-shopOffsetHours), to.AddDays(1).AddHours(-shopOffsetHours));

    /// <summary>Every shop month the window touches, oldest first — including the ones in which
    /// nothing moved. A month missing from the report because it was quiet is indistinguishable from
    /// a month missing because something is broken, and the gap in a trend is worth seeing.</summary>
    internal static List<DateTime> MonthsIn(DateTime from, DateTime to)
    {
        var months = new List<DateTime>();
        for (var m = new DateTime(from.Year, from.Month, 1); m <= to; m = m.AddMonths(1))
            months.Add(m);
        return months;
    }
}
