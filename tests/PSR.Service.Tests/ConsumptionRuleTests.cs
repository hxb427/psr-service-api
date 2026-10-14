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

    /// <summary>The distinction the sales/service split stands on, and the reason the rule cannot
    /// switch on the movement type alone: a part fitted on a field visit and a spare sold off the same
    /// technician's van are both Consumption rows with the same balance effect, and only the reference
    /// says which. Reading the type alone files every field sale under service work.</summary>
    [Theory]
    [InlineData("SERVICE", "Service", ConsumptionRules.InHouseJob)]
    [InlineData("FIELD_SERVICE", "Service", ConsumptionRules.FieldVisit)]
    [InlineData("FIELD_SALE", "Sales", ConsumptionRules.FieldSale)]
    public void A_consumption_is_filed_by_its_reference_not_its_type(
        string reference, string stream, string kind)
    {
        var effect = ConsumptionRules.Effect(MovementType.Consumption, reference, 3);

        effect.Should().NotBeNull();
        effect!.Value.Stream.ToString().Should().Be(stream);
        effect.Value.Kind.Should().Be(kind);
        effect.Value.Consumed.Should().Be(3);
    }

    /// <summary>A counter sale leaves the warehouse shelf rather than a technician's stock, so it has
    /// its own movement type — but it is still the sales side consuming stock.</summary>
    [Fact]
    public void A_counter_sale_is_sales_consumption()
    {
        var effect = ConsumptionRules.Effect(MovementType.Sale, "SPARE_SALE", 2);

        effect!.Value.Stream.Should().Be(ConsumptionStream.Sales);
        effect.Value.Consumed.Should().Be(2);
    }

    /// <summary>A whole unit swapped out for a customer's is service work, not a sale: nobody paid for
    /// it as goods, and it is spend on fixing something.</summary>
    [Fact]
    public void A_replacement_unit_is_service_consumption()
    {
        ConsumptionRules.Effect(MovementType.Replacement, "SERVICE", 1)!.Value
            .Stream.Should().Be(ConsumptionStream.Service);
    }

    /// <summary>Stock handed to a technician has been moved, not used. It is consumed on the day it is
    /// fitted — which may be next month, or never — and counting the issue would both bring the usage
    /// forward and then count it again when the part actually went into a machine.</summary>
    [Theory]
    [InlineData(MovementType.Issue)]
    [InlineData(MovementType.IssueReceipt)]
    [InlineData(MovementType.Receipt)]
    [InlineData(MovementType.Return)]
    [InlineData(MovementType.ReturnDispatched)]
    [InlineData(MovementType.TransferOut)]
    [InlineData(MovementType.TransferIn)]
    [InlineData(MovementType.Transfer)]
    [InlineData(MovementType.DefectiveOnArrival)]
    public void Moving_stock_around_the_company_is_not_consumption(MovementType type)
    {
        ConsumptionRules.Effect(type, "STOCK_REQUEST", 5).Should().BeNull();
    }

    /// <summary>The ledger reverses by appending an opposite row, never by deleting the original, so
    /// the pair has to come to nothing. Summing only the positive rows would report usage that never
    /// happened and would keep reporting it for ever.</summary>
    [Theory]
    [InlineData(MovementType.Consumption, MovementType.ConsumptionReversal, "SERVICE")]
    [InlineData(MovementType.Sale, MovementType.SaleUnsold, "SPARE_SALE")]
    [InlineData(MovementType.Sale, MovementType.SaleReturn, "SPARE_SALE")]
    [InlineData(MovementType.Replacement, MovementType.ReplacementReturn, "SERVICE")]
    public void A_reversal_nets_its_original_off(MovementType original, MovementType reversal, string reference)
    {
        var up = ConsumptionRules.Effect(original, reference, 4)!.Value;
        var down = ConsumptionRules.Effect(reversal, reference, 4)!.Value;

        (up.Consumed + down.Consumed).Should().Be(0);
        down.Stream.Should().Be(up.Stream, "a reversal that landed on the other stream would net off "
            + "against work it has nothing to do with");
    }

    /// <summary>A reverted FIELD SALE would be the nasty case: netting it off against service
    /// consumption would leave the sales column overstated and the service column understated, and
    /// the combined total would look perfectly correct.</summary>
    [Fact]
    public void A_reverted_field_sale_nets_off_against_sales_not_service()
    {
        ConsumptionRules.Effect(MovementType.ConsumptionReversal, "FIELD_SALE", 1)!.Value
            .Stream.Should().Be(ConsumptionStream.Sales);
    }

    /// <summary>The one movement type whose quantity is signed rather than a magnitude. A stock-take
    /// that found three fewer consumed three; one that found three more un-consumes them.</summary>
    [Theory]
    [InlineData(-3, 3)]
    [InlineData(3, -3)]
    public void An_adjustment_is_already_signed(int delta, int consumed)
    {
        var effect = ConsumptionRules.Effect(MovementType.Adjustment, "MANUAL", delta)!.Value;

        effect.Stream.Should().Be(ConsumptionStream.Other);
        effect.Consumed.Should().Be(consumed);
    }

    /// <summary>Two statements of one fact: the SQL filter that decides which rows are fetched, and
    /// the rule that decides what they mean. A type added to the rule and not to the filter never
    /// reaches the report, and nothing anywhere fails — the column just reads low.</summary>
    [Fact]
    public void The_sql_filter_matches_the_rule_for_every_movement_type()
    {
        var isConsuming = ConsumptionRules.IsConsuming.Compile();

        foreach (var type in Enum.GetValues<MovementType>())
        {
            var fetched = isConsuming(new StockMovement { MovementType = type, Quantity = 1 });
            var counted = ConsumptionRules.Effect(type, "SERVICE", 1) is not null;

            fetched.Should().Be(counted,
                $"{type} is {(counted ? "counted by the rule, so it must be fetched" : "not consumption, so fetching it is waste")}");
        }
    }

    // ---------------------------------------------------------------- the month axis

    /// <summary>The report's rows are months, so the boundary between two of them is the report. The
    /// shop is five and a half hours ahead of what is stored: a part fitted at seven in the evening on
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
    [InlineData("service", "Service")]
    [InlineData("SALES", "Sales")]
    [InlineData("other", "Other")]
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
}
