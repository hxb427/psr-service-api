using FluentAssertions;
using PSR.Service.Api.Data.Entities;
using PSR.Service.Api.Reports;
using Xunit;

namespace PSR.Service.Tests;

/// <summary>The monthly consumption report's arithmetic. Every figure on that report is this one
/// function read a different way, and all of it is wrong in a way nobody can see if the function is:
/// an under-count looks exactly like a quiet month, and the report's whole job is telling those apart.
///
/// Pure rules, no database — see ConsumptionRules for why they live apart from the endpoint.</summary>
public class ConsumptionRuleTests
{
    private const double Ist = 5.5;

    /// <summary>The report's definition, in one test: stock leaving the warehouse shelf, filed by
    /// where it went. These four types are exactly the four that take the warehouse balance down.</summary>
    // The stream arrives as a string because ConsumptionStream is internal to the API and an internal
    // type cannot be a public test method's parameter — the same reason the stream-parsing test below
    // compares names rather than values.
    [Theory]
    [InlineData(MovementType.Issue, "Technicians", ConsumptionRules.IssuedToTechnician)]
    [InlineData(MovementType.Sale, "Sales", ConsumptionRules.CounterSale)]
    [InlineData(MovementType.Replacement, "Service", ConsumptionRules.UnitReplacement)]
    public void Stock_leaving_the_warehouse_is_counted_where_it_went(
        MovementType type, string stream, string kind)
    {
        var effect = ConsumptionRules.Effect(type, 3);

        effect.Should().NotBeNull();
        effect!.Value.Stream.ToString().Should().Be(stream);
        effect.Value.Kind.Should().Be(kind);
        effect.Value.Consumed.Should().Be(3);
    }

    /// <summary>An issue is the day the stock left the building, and that is this report's whole
    /// question. It used to be excluded on the grounds that a part in a van has not been used yet,
    /// which is true and is the stock analysis's question — Issued against Used, with the gap being
    /// what the technician is carrying. Counting it here is what makes this report's total equal the
    /// stock ledger's Outward column.</summary>
    [Fact]
    public void An_issue_to_a_technician_is_the_bulk_of_the_report()
    {
        var effect = ConsumptionRules.Effect(MovementType.Issue, 6);

        effect!.Value.Stream.Should().Be(ConsumptionStream.Technicians);
        effect.Value.Consumed.Should().Be(6, "all six left the shelf, whatever is fitted later");
    }

    /// <summary>Stock used off a TECHNICIAN'S OWN balance was already counted the day the warehouse
    /// issued it. Counting it again when it is fitted or sold in the field reports the same board
    /// twice — once on its way to the van and once on its way into a machine.</summary>
    [Theory]
    [InlineData(MovementType.Consumption)]
    [InlineData(MovementType.ConsumptionReversal)]
    [InlineData(MovementType.TransferOut)]
    [InlineData(MovementType.TransferIn)]
    [InlineData(MovementType.Transfer)]
    [InlineData(MovementType.IssueReceipt)]
    [InlineData(MovementType.ReturnDispatched)]
    [InlineData(MovementType.DefectiveOnArrival)]
    public void Stock_moving_on_the_technician_side_is_not_warehouse_outflow(MovementType type)
    {
        ConsumptionRules.Effect(type, 5).Should().BeNull();
    }

    /// <summary>A shipment that never arrived moves no warehouse balance: the Issue it belongs to
    /// already took it off the shelf, and this row only records that it was lost on the way. Counting
    /// it would count the same units out twice.</summary>
    [Fact]
    public void A_transit_loss_is_not_counted_because_the_issue_already_was()
    {
        ConsumptionRules.Effect(MovementType.LossInTransit, 2).Should().BeNull();
    }

    /// <summary>The reason this report cannot go negative. Everything that puts stock BACK on the
    /// shelf is simply not counted — not netted off, not subtracted, not carried as a negative into
    /// the month it happened in. Netting is defensible arithmetic and an unreadable report: a sale
    /// returned this month against one made before the window started used to leave a month reading
    /// below zero, with nothing on screen to explain it.</summary>
    [Theory]
    [InlineData(MovementType.Receipt)]
    [InlineData(MovementType.Return)]
    [InlineData(MovementType.SaleReturn)]
    [InlineData(MovementType.SaleUnsold)]
    [InlineData(MovementType.ReplacementReturn)]
    public void Stock_coming_back_is_not_counted_at_all(MovementType type)
    {
        ConsumptionRules.Effect(type, 4).Should().BeNull();
    }

    /// <summary>The one movement type stored already signed. A stock-take that found three fewer took
    /// three off the shelf and consumed them; one that found three MORE put stock back, and is
    /// therefore not outflow at all. The old rule turned that second case into negative consumption,
    /// which is where most of the negative figures on the report came from — manual corrections and
    /// serial-status fixes are written as adjustments and go both ways.</summary>
    [Theory]
    [InlineData(-3, 3)]
    [InlineData(-1, 1)]
    public void A_write_down_is_the_negative_half_of_an_adjustment(int delta, int consumed)
    {
        var effect = ConsumptionRules.Effect(MovementType.Adjustment, delta)!.Value;

        effect.Stream.Should().Be(ConsumptionStream.Shrinkage);
        effect.Kind.Should().Be(ConsumptionRules.StockWriteDown);
        effect.Consumed.Should().Be(consumed);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(1)]
    public void A_correction_that_found_more_stock_is_not_consumption(int delta)
    {
        ConsumptionRules.Effect(MovementType.Adjustment, delta).Should().BeNull(
            "stock that went back on the shelf cannot be stock that left it");
    }

    /// <summary>The invariant the whole report rests on, asserted over every movement type at both
    /// signs: no effect is ever zero or negative. Every figure on the report — each month, each part,
    /// the totals strip, the drill-down — is a sum of these, so this one property is what guarantees
    /// no cell anywhere can read below zero.</summary>
    [Fact]
    public void No_movement_of_any_kind_can_produce_a_negative_figure()
    {
        foreach (var type in Enum.GetValues<MovementType>())
            foreach (var qty in new[] { -7, -1, 0, 1, 7 })
                ConsumptionRules.Effect(type, qty)?.Consumed.Should().BePositive(
                    $"{type} of {qty} would otherwise put a negative on the report");
    }

    /// <summary>Two statements of one fact: the SQL filter that decides which rows are fetched, and
    /// the rule that decides what they mean. A type added to the rule and not to the filter never
    /// reaches the report, and nothing anywhere fails — the column just reads low.
    ///
    /// Asserted against whether a type can EVER count, at either sign, because Adjustment counts at
    /// one sign only and the predicate cannot express that in SQL.</summary>
    [Fact]
    public void The_sql_filter_matches_the_rule_for_every_movement_type()
    {
        var isConsuming = ConsumptionRules.IsConsuming.Compile();

        foreach (var type in Enum.GetValues<MovementType>())
        {
            var fetched = isConsuming(new StockMovement { MovementType = type, Quantity = 1 });
            var counted = ConsumptionRules.Effect(type, 1) is not null
                       || ConsumptionRules.Effect(type, -1) is not null;

            fetched.Should().Be(counted,
                $"{type} is {(counted ? "counted by the rule, so it must be fetched" : "not outflow, so fetching it is waste")}");
        }
    }

    /// <summary>Adjustment is the one type the database predicate deliberately over-fetches: both
    /// halves come back and the sign is filtered in memory. Pinned so that a later attempt to push the
    /// sign into SQL has to deal with this test rather than quietly changing what the column means.</summary>
    [Fact]
    public void A_positive_adjustment_is_fetched_and_then_discarded()
    {
        var isConsuming = ConsumptionRules.IsConsuming.Compile();

        isConsuming(new StockMovement { MovementType = MovementType.Adjustment, Quantity = 3 })
            .Should().BeTrue("the sign is not in the SQL");
        ConsumptionRules.Effect(MovementType.Adjustment, 3).Should().BeNull("...so the rule drops it");
    }

    // ---------------------------------------------------------------- the month axis

    /// <summary>The report's rows are months, so the boundary between two of them is the report. The
    /// shop is five and a half hours ahead of what is stored: stock issued at seven in the evening on
    /// the last of October is stored as the first of November, and bucketing the raw timestamp takes a
    /// day's work out of October and puts it in a month that had not started.</summary>
    [Fact]
    public void An_evening_on_the_last_of_the_month_stays_in_that_month()
    {
        // 19:00 IST on 31 Oct 2026 == 13:30 UTC — same day either way.
        var earlyEvening = new DateTime(2026, 10, 31, 13, 30, 0, DateTimeKind.Utc);
        // 23:00 IST on 31 Oct 2026 == 17:30 UTC on the 31st; still October on both clocks.
        var lateEvening = new DateTime(2026, 10, 31, 17, 30, 0, DateTimeKind.Utc);
        // 00:30 IST on 1 Nov 2026 == 19:00 UTC on 31 Oct — the one the raw timestamp gets wrong.
        var justAfterMidnight = new DateTime(2026, 10, 31, 19, 0, 0, DateTimeKind.Utc);

        ConsumptionRules.MonthOf(earlyEvening, Ist).Should().Be(new DateTime(2026, 10, 1));
        ConsumptionRules.MonthOf(lateEvening, Ist).Should().Be(new DateTime(2026, 10, 1));
        ConsumptionRules.MonthOf(justAfterMidnight, Ist).Should().Be(new DateTime(2026, 11, 1),
            "half past midnight at the shop is the new month, whatever the stored date says");
    }

    /// <summary>A window of shop days has to become a window of stored instants, or the last five and
    /// a half hours of the final day fall outside it — which on this report is a whole evening's work
    /// missing from the month somebody is looking at.</summary>
    [Fact]
    public void The_window_covers_the_shop_days_it_names()
    {
        var (from, toExclusive) = ConsumptionRules.StoredRange(
            new DateTime(2026, 10, 1), new DateTime(2026, 10, 31), Ist);

        // Midnight on 1 Oct at the shop is 18:30 on 30 Sep as stored.
        from.Should().Be(new DateTime(2026, 9, 30, 18, 30, 0));
        // The window ends at midnight on 1 Nov at the shop — 18:30 on 31 Oct stored.
        toExclusive.Should().Be(new DateTime(2026, 10, 31, 18, 30, 0));

        var lastMinuteOfTheMonth = new DateTime(2026, 10, 31, 18, 29, 0);
        lastMinuteOfTheMonth.Should().BeOnOrAfter(from).And.BeBefore(toExclusive);
    }

    /// <summary>Quiet months are rows too. A month missing because nothing happened cannot be told
    /// apart from a month missing because something is broken, and the gap in the trend is the point.</summary>
    [Fact]
    public void Every_month_in_the_window_is_a_row_even_an_empty_one()
    {
        var months = ConsumptionRules.MonthsIn(new DateTime(2026, 8, 14), new DateTime(2026, 11, 2));

        months.Should().Equal(
            new DateTime(2026, 8, 1), new DateTime(2026, 9, 1),
            new DateTime(2026, 10, 1), new DateTime(2026, 11, 1));
    }

    /// <summary>With no dates named the report opens on a year of whole months, so a seasonal item's
    /// figure has something to be read against and the oldest bucket is not a fortnight that looks
    /// like a slow month.</summary>
    [Fact]
    public void A_window_nobody_named_is_the_trailing_twelve_whole_months()
    {
        var (from, to) = ConsumptionRules.Window(null, new DateTime(2026, 10, 12), Ist);

        from.Should().Be(new DateTime(2025, 11, 1));
        to.Should().Be(new DateTime(2026, 10, 12));
        ConsumptionRules.MonthsIn(from, to).Should().HaveCount(ConsumptionRules.DefaultMonths);
    }

    /// <summary>A window handed over backwards would otherwise come back as a silent zero, which on
    /// this report is indistinguishable from a month in which the shop did nothing.</summary>
    [Fact]
    public void A_backwards_window_is_turned_round_rather_than_read_as_empty()
    {
        var (from, to) = ConsumptionRules.Window(
            new DateTime(2026, 10, 31), new DateTime(2026, 10, 1), Ist);

        from.Should().Be(new DateTime(2026, 10, 1));
        to.Should().Be(new DateTime(2026, 10, 31));
    }

    /// <summary>An unrecognised stream shows everything rather than nothing. A page of zeroes reads
    /// as a quiet month and gets believed; a combined figure where a filtered one was wanted is
    /// obvious on sight.</summary>
    [Theory]
    [InlineData("technicians", "Technicians")]
    [InlineData("TECHS", "Technicians")]
    [InlineData("issues", "Technicians")]
    [InlineData("sales", "Sales")]
    [InlineData("SERVICE", "Service")]
    [InlineData("shrinkage", "Shrinkage")]
    [InlineData("all", null)]
    [InlineData("combined", null)]
    [InlineData(null, null)]
    [InlineData("nonsense", null)]
    public void A_stream_nobody_recognises_is_everything(string? value, string? expected)
    {
        ConsumptionRules.ParseStream(value)?.ToString().Should().Be(expected);
        ConsumptionRules.StreamName(ConsumptionRules.ParseStream(value))
            .Should().Be(expected?.ToLowerInvariant() ?? "all");
    }

    /// <summary>"other" was this column's name when it also held transit losses. A saved link or an
    /// older desktop build has to land on the column that replaced it rather than on everything, which
    /// is what an unrecognised value would do.</summary>
    [Fact]
    public void The_old_name_for_the_shrinkage_column_still_resolves_to_it()
    {
        ConsumptionRules.ParseStream("other").Should().Be(ConsumptionStream.Shrinkage);
    }

    // ---------------------------------------------------------------- against the stock ledger

    /// <summary>The claim this report's documentation makes, pinned: its total IS the stock ledger's
    /// Outward column, broken out by month and by part. The two reports read the same ledger through
    /// two separate rules — ConsumptionRules.Effect here, ReportsEndpoints.WarehouseDelta there — and
    /// nothing but this test stops them drifting apart on a movement type, which would leave two pages
    /// of the app quietly disagreeing about what left the shelf.
    ///
    /// Quantities are the ones the ledger actually writes: a magnitude for every type, and both signs
    /// for Adjustment, which is the only type stored signed.</summary>
    [Fact]
    public void The_report_counts_exactly_what_the_stock_ledger_calls_outward()
    {
        foreach (var type in Enum.GetValues<MovementType>())
        {
            var quantities = type == MovementType.Adjustment ? new[] { 1, 4, -1, -4 } : new[] { 1, 4 };

            foreach (var qty in quantities)
            {
                var consumed = ConsumptionRules.Effect(type, qty)?.Consumed ?? 0;
                // What the ledger took OFF the shelf for this row, as a positive number; zero when the
                // row put stock on it or never touched it.
                var offTheShelf = Math.Max(0, -ReportsEndpoints.WarehouseDelta(type, qty));

                consumed.Should().Be(offTheShelf,
                    $"{type} of {qty} moves the warehouse by {ReportsEndpoints.WarehouseDelta(type, qty)}, "
                    + "so the consumption report and the stock ledger must agree on it");
            }
        }
    }
}
