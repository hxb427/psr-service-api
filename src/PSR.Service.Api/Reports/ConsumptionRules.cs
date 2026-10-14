using System.Linq.Expressions;
using PSR.Service.Api.Data.Entities;

namespace PSR.Service.Api.Reports;

/// <summary>Which side of the business used the stock up. The split exists because the store's two
/// questions — how much are we spending on repairs, and how much are we selling over the counter —
/// are answered out of the same ledger and used to be impossible to tell apart on it.</summary>
internal enum ConsumptionStream
{
    /// <summary>Used fixing something: fitted on an in-house job or a field visit, or shipped out as
    /// a whole replacement unit.</summary>
    Service,

    /// <summary>Sold as goods: a counter/dealer spare sale, or a field technician selling from the
    /// stock they carry.</summary>
    Sales,

    /// <summary>Gone without being used or sold — a stock-take write-down, or a shipment that never
    /// arrived. Small, and deliberately not folded into either of the other two: a shrinkage counted
    /// as service consumption reads as repair spend that never happened.</summary>
    Other,
}

/// <summary>What one ledger row means to the consumption report: which side of the business used the
/// stock, what kind of event it was, and how many units it consumed.
///
/// <paramref name="Consumed"/> is signed, and that is why this carries a quantity rather than being a
/// plain classification. The ledger reverses things by appending an opposite row, never by deleting
/// the original — a completed job that was reverted, a sale un-marked in error, a cancelled
/// replacement — so those rows have to come back NEGATIVE and net the original off. A report that
/// summed only the positive rows would show usage that never happened, and would keep showing it for
/// ever.</summary>
internal readonly record struct ConsumptionEffect(ConsumptionStream Stream, string Kind, int Consumed);

/// <summary>The consumption report's arithmetic, kept apart from the endpoint that serves it so the
/// rules can be pinned by tests without a database behind them.
///
/// This is the second of the two rules that read the stock ledger — <see cref="ReportsEndpoints"/>'s
/// WarehouseDelta is the other — and they answer deliberately different questions. WarehouseDelta is
/// about a shelf: issuing six boards to a technician takes six off it. This is about use: those six
/// boards have not been consumed, they have been moved, and they are consumed on the day they are
/// fitted, which may be next month or never. The two reports therefore will not agree on a total,
/// and should not.</summary>
internal static class ConsumptionRules
{
    // The kind labels, as the report prints them. Here rather than inline so the grid, the drill-down
    // and the workbook cannot drift into three spellings of the same event.
    internal const string InHouseJob = "In-house job";
    internal const string JobReverted = "Job reverted";
    internal const string FieldVisit = "Field visit";
    internal const string FieldSale = "Field sale";
    internal const string CounterSale = "Counter sale";
    internal const string SaleUnmarked = "Sale un-marked";
    internal const string SaleReturned = "Sale returned";
    internal const string UnitReplacement = "Unit replacement";
    internal const string ReplacementCancelled = "Replacement cancelled";
    internal const string StockAdjustment = "Stock adjustment";
    internal const string LostInTransit = "Lost in transit";

    /// <summary>How many months the report covers when the caller names no window. Twelve, so the
    /// page opens on a year of months and a seasonal item's figure has something to be read against;
    /// an unbounded default would list every month the shop has ever traded.</summary>
    internal const int DefaultMonths = 12;

    /// <summary>How a ledger row lands on the consumption report, or null when it is not consumption.
    ///
    /// The movement type alone is not enough, and that is the point of this function. A Consumption
    /// row is a part FITTED by a technician when it carries a SERVICE or FIELD_SERVICE reference and
    /// a spare SOLD by one when it carries FIELD_SALE — same type, same balance effect, opposite
    /// sides of the business. Switching on the type alone files every field sale under service work,
    /// which is exactly the mistake the sales/service split on this report exists to stop.
    ///
    /// What is deliberately NOT consumption: an issue to a technician, a return, a receipt and a peer
    /// transfer all move stock from one shelf to another inside the company. Counting an issue would
    /// say the stock was used up on the day it was handed over rather than the day it was fitted, and
    /// would then count it a second time when it actually was. A defective arrival moves no balance
    /// at all — the unit is with the technician, faulty — so it is nobody's consumption either.</summary>
    internal static ConsumptionEffect? Effect(MovementType type, string? referenceType, int qty)
    {
        var reference = referenceType?.Trim().ToUpperInvariant();
        return type switch
        {
            MovementType.Consumption => Used(reference, qty),
            MovementType.ConsumptionReversal => Reverted(reference, qty),

            MovementType.Sale => new ConsumptionEffect(ConsumptionStream.Sales, CounterSale, qty),
            MovementType.SaleUnsold => new ConsumptionEffect(ConsumptionStream.Sales, SaleUnmarked, -qty),
            MovementType.SaleReturn => new ConsumptionEffect(ConsumptionStream.Sales, SaleReturned, -qty),

            MovementType.Replacement => new ConsumptionEffect(ConsumptionStream.Service, UnitReplacement, qty),
            MovementType.ReplacementReturn => new ConsumptionEffect(ConsumptionStream.Service, ReplacementCancelled, -qty),

            // Stored already signed, where a shrinkage is negative: a stock-take that found three
            // fewer consumed three, and one that found three more un-consumes them. The sign flip is
            // the whole conversion — this is the one movement type whose quantity is not a magnitude.
            MovementType.Adjustment => new ConsumptionEffect(ConsumptionStream.Other, StockAdjustment, -qty),
            MovementType.LossInTransit => new ConsumptionEffect(ConsumptionStream.Other, LostInTransit, qty),

            _ => null,
        };
    }

    /// <summary>The movement types <see cref="Effect"/> can return something for, as a predicate the
    /// database applies before any of this runs. Issues, receipts, returns and peer transfers are the
    /// bulk of the ledger and none of them is consumption, so leaving them in the database rather than
    /// fetching a year of them to discard is most of this report's cost.
    ///
    /// Two statements of one fact, which is a drift waiting to happen: a type added to
    /// <see cref="Effect"/> and not here would simply never appear on the report, and nothing would
    /// fail. ConsumptionRuleTests walks every <see cref="MovementType"/> and asserts the two agree,
    /// so the drift is a red test rather than a column that quietly reads low.</summary>
    internal static readonly Expression<Func<StockMovement, bool>> IsConsuming = m =>
        m.MovementType == MovementType.Consumption
        || m.MovementType == MovementType.ConsumptionReversal
        || m.MovementType == MovementType.Sale
        || m.MovementType == MovementType.SaleUnsold
        || m.MovementType == MovementType.SaleReturn
        || m.MovementType == MovementType.Replacement
        || m.MovementType == MovementType.ReplacementReturn
        || m.MovementType == MovementType.Adjustment
        || m.MovementType == MovementType.LossInTransit;

    /// <summary>A part fitted or sold off a technician's own stock. Which of the two it was is on the
    /// reference, never on the type.</summary>
    private static ConsumptionEffect Used(string? reference, int qty) => reference switch
    {
        "FIELD_SALE" => new ConsumptionEffect(ConsumptionStream.Sales, FieldSale, qty),
        "FIELD_SERVICE" => new ConsumptionEffect(ConsumptionStream.Service, FieldVisit, qty),
        // "SERVICE", and anything written before the field references existed.
        _ => new ConsumptionEffect(ConsumptionStream.Service, InHouseJob, qty),
    };

    /// <summary>Parts handed back when a completed job was reverted. Classified through the same
    /// reference mapping as the consumption it undoes, so a reversal always lands on the stream that
    /// carried the original and can never net off against the other one.</summary>
    private static ConsumptionEffect Reverted(string? reference, int qty) =>
        new(Used(reference, qty).Stream, JobReverted, -qty);

    /// <summary>The stream the caller asked for, or null for all of them combined.</summary>
    internal static ConsumptionStream? ParseStream(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "service" => ConsumptionStream.Service,
        "sales" => ConsumptionStream.Sales,
        "other" => ConsumptionStream.Other,
        // "all", "combined", missing, or anything unrecognised. An unknown value showing everything is
        // the safe way to be wrong: the alternative is a page of zeroes that reads like a quiet month.
        _ => null,
    };

    /// <summary>How a stream is named back to the caller, so the page and the workbook label the
    /// window with the server's own word for it rather than echoing what was typed.</summary>
    internal static string StreamName(ConsumptionStream? stream) => stream switch
    {
        ConsumptionStream.Service => "service",
        ConsumptionStream.Sales => "sales",
        ConsumptionStream.Other => "other",
        _ => "all",
    };

    /// <summary>The shop-calendar month a stored instant falls in, as that month's first day.
    ///
    /// On the shop's clock, not the server's, and this is the one piece of arithmetic the whole report
    /// stands on. Movements are stored UTC; the shop is five and a half hours ahead. A part fitted at
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
