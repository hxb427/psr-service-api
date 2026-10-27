using FluentAssertions;
using PSR.Service.Api.Common;
using PSR.Service.Api.Data.Entities;
using PSR.Service.Api.Stock;
using Xunit;

namespace PSR.Service.Tests;

/// <summary>What the call register will and will not accept.
///
/// The dates carry most of the weight. A received date is a day somebody wrote down, and the shop is
/// five and a half hours ahead of the container the API runs in — so the arrangement that already
/// booked every inward entry a day early is sitting right here waiting to do it again, and these
/// tests are what pin the repair. See <see cref="ShopClockBusinessDateTests"/> for the same boundary
/// from the other side.</summary>
public class FieldCallRuleTests
{
    public FieldCallRuleTests() => ShopClock.Configure(5.5);

    // ---------------------------------------------------------------- enums

    [Theory]
    [InlineData("SalesCall", FieldCallType.SalesCall)]
    [InlineData("servicecall", FieldCallType.ServiceCall)]
    [InlineData("INSTALLATION", FieldCallType.Installation)]
    [InlineData("  Demo  ", FieldCallType.Demo)]
    [InlineData("dealerVisit", FieldCallType.DealerVisit)]
    public void Call_type_is_parsed_whatever_the_casing(string raw, FieldCallType expected)
    {
        var (value, error) = FieldCallRules.ParseRequired<FieldCallType>(raw, "call type");

        error.Should().BeNull();
        value.Should().Be(expected);
    }

    [Fact]
    public void A_missing_call_type_is_refused()
    {
        // The one enum with no sensible default: every other field on the form describes HOW the call
        // went, but what kind of call it was is the thing the register is grouped by.
        var (_, error) = FieldCallRules.ParseRequired<FieldCallType>(null, "call type");

        error.Should().NotBeNull();
    }

    [Fact]
    public void An_unknown_call_type_is_refused_and_lists_the_real_ones()
    {
        var (_, error) = FieldCallRules.ParseRequired<FieldCallType>("WarrantyClaim", "call type");

        error.Should().NotBeNull();
        // Generated from the enum, so a sixth call type cannot leave this message naming five.
        foreach (var name in Enum.GetNames<FieldCallType>()) error.Should().Contain(name);
    }

    [Fact]
    public void An_omitted_locality_is_local_and_an_omitted_status_is_incomplete()
    {
        FieldCallRules.ParseOptional(null, FieldCallLocality.Local, "locality")
            .Should().Be((FieldCallLocality.Local, (string?)null));

        // Incomplete is the understatement. Defaulting to Complete would have the register claim work
        // was finished because nobody touched a dropdown.
        FieldCallRules.ParseOptional(null, FieldCallStatus.Incomplete, "status")
            .Should().Be((FieldCallStatus.Incomplete, (string?)null));
    }

    [Fact]
    public void A_misspelled_optional_value_is_refused_rather_than_defaulted()
    {
        // The asymmetry that matters: saying nothing takes the default, but saying something wrong is
        // an error. Otherwise "Outstaton" silently becomes Local and the mileage never gets claimed.
        var (value, error) = FieldCallRules.ParseOptional("Outstaton", FieldCallLocality.Local, "locality");

        error.Should().NotBeNull();
        value.Should().Be(FieldCallLocality.Local, "the caller must not act on this, and does not");
    }

    [Fact]
    public void A_filter_that_will_not_parse_is_simply_not_a_filter()
    {
        FieldCallRules.ParseFilter<FieldCallType>("nonsense").Should().BeNull();
        FieldCallRules.ParseFilter<FieldCallType>("").Should().BeNull();
        FieldCallRules.ParseFilter<FieldCallType>("demo").Should().Be(FieldCallType.Demo);
    }

    // ---------------------------------------------------------------- dates

    [Fact]
    public void A_bare_date_is_stored_as_the_day_that_was_picked()
    {
        var picked = new DateTime(2026, 10, 21, 0, 0, 0, DateTimeKind.Unspecified);

        var (received, _, error) = FieldCallRules.Dates(picked, null);

        error.Should().BeNull();
        received.Should().Be(new DateTime(2026, 10, 21));
    }

    [Fact]
    public void A_zoned_midnight_is_not_booked_a_day_early()
    {
        // What a .NET client sends when it serialises DateTime.Today: midnight IST, which a UTC
        // server deserialises as 18:30 on the 20th. Stored raw, that is the previous day.
        var asUtcInstant = new DateTime(2026, 10, 20, 18, 30, 0, DateTimeKind.Utc);

        var (received, _, _) = FieldCallRules.Dates(asUtcInstant, null);

        received.Should().Be(new DateTime(2026, 10, 21));
    }

    [Fact]
    public void A_time_of_day_is_dropped_from_both_dates()
    {
        var received = new DateTime(2026, 10, 19, 9, 15, 0, DateTimeKind.Unspecified);
        var attended = new DateTime(2026, 10, 20, 17, 45, 0, DateTimeKind.Unspecified);

        var result = FieldCallRules.Dates(received, attended);

        result.received.Should().Be(new DateTime(2026, 10, 19));
        result.attended.Should().Be(new DateTime(2026, 10, 20));
    }

    [Fact]
    public void An_absent_received_date_is_today_on_the_shops_clock()
    {
        var (received, attended, error) = FieldCallRules.Dates(null, null);

        error.Should().BeNull();
        received.Should().Be(ShopClock.Today);
        attended.Should().BeNull("a call not yet attended has no attended date, and that is not an error");
    }

    [Fact]
    public void Attending_before_the_call_came_in_is_refused()
    {
        // The shape a mis-keyed year takes.
        var (_, _, error) = FieldCallRules.Dates(
            new DateTime(2026, 10, 21), new DateTime(2025, 10, 21));

        error.Should().NotBeNull();
    }

    [Fact]
    public void Attending_on_the_same_day_is_allowed()
    {
        // The common case by far: the call comes in and somebody goes out the same morning.
        var day = new DateTime(2026, 10, 21);

        var (_, _, error) = FieldCallRules.Dates(day, day);

        error.Should().BeNull();
    }

    [Fact]
    public void The_ordering_check_compares_shop_days_not_instants()
    {
        // Received as a bare date, attended as the zoned midnight of the SAME shop day. Compared as
        // instants the attended value is 18:30 the evening before and would be refused; compared as
        // shop days they are one day and it is allowed. The second is the correct answer.
        var received = new DateTime(2026, 10, 21, 0, 0, 0, DateTimeKind.Unspecified);
        var attendedAsInstant = new DateTime(2026, 10, 20, 18, 30, 0, DateTimeKind.Utc);

        var (_, attended, error) = FieldCallRules.Dates(received, attendedAsInstant);

        error.Should().BeNull();
        attended.Should().Be(new DateTime(2026, 10, 21));
    }

    [Fact]
    public void A_received_date_in_the_future_is_allowed()
    {
        // Deliberate. A handset whose clock runs minutes ahead of the shop's would trip a future-date
        // rule around midnight, and losing a real call to a clock skew is worse than storing a date
        // anyone can see is wrong.
        var (_, _, error) = FieldCallRules.Dates(ShopClock.Today.AddDays(3), null);

        error.Should().BeNull();
    }

    // ---------------------------------------------------------------- lengths

    [Fact]
    public void A_call_that_fits_its_columns_passes()
    {
        FieldCallRules.Lengths(new FieldCall
        {
            CustomerName = new string('a', 200),
            CustomerAddress = new string('b', 500),
            CallReferenceNo = new string('c', 100),
            MachineModel = new string('d', 100),
            MachineSerial = new string('e', 100),
            SoftwareVersion = new string('f', 50),
            Remarks = new string('g', 1000),
        }).Should().BeNull();
    }

    [Theory]
    [InlineData(201, 500, 100, 100, 100, 50, 1000, 50, 1000, "Customer name")]
    [InlineData(200, 501, 100, 100, 100, 50, 1000, 50, 1000, "Customer address")]
    [InlineData(200, 500, 101, 100, 100, 50, 1000, 50, 1000, "Call reference number")]
    [InlineData(200, 500, 100, 101, 100, 50, 1000, 50, 1000, "Machine model")]
    [InlineData(200, 500, 100, 100, 101, 50, 1000, 50, 1000, "Machine/part serial")]
    [InlineData(200, 500, 100, 100, 100, 51, 1000, 50, 1000, "Software version")]
    [InlineData(200, 500, 100, 100, 100, 50, 1001, 50, 1000, "Remarks")]
    // The two fields a service entry prefills from. Their widths match the equivalents on
    // field_services, so a complaint that fits the visit fits the call it came from.
    [InlineData(200, 500, 100, 100, 100, 50, 1000, 51, 1000, "Phone")]
    [InlineData(200, 500, 100, 100, 100, 50, 1000, 50, 1001, "Reported complaint")]
    public void One_character_over_is_named_in_the_error(
        int name, int address, int reference, int model, int serial, int version, int remarks,
        int phone, int complaint, string expected)
    {
        // Without this check the string reaches MySQL, which either throws (a 500 for what is plainly
        // a bad request) or truncates it silently.
        var error = FieldCallRules.Lengths(new FieldCall
        {
            CustomerName = new string('a', name),
            CustomerAddress = new string('b', address),
            CallReferenceNo = new string('c', reference),
            MachineModel = new string('d', model),
            MachineSerial = new string('e', serial),
            SoftwareVersion = new string('f', version),
            Remarks = new string('g', remarks),
            ContactPhone = new string('1', phone),
            ReportedComplaint = new string('h', complaint),
        });

        error.Should().NotBeNull();
        error.Should().Contain(expected);
    }

    [Fact]
    public void Length_is_measured_on_the_trimmed_value()
    {
        // Trimming is what gets stored, so padding must not be what refuses the save.
        var padded = "  " + new string('a', 200) + "  ";

        FieldCallRules.TooLong(padded, 200, "Customer name").Should().BeNull();
    }

    // ---------------------------------------------------------------- after a visit

    [Fact]
    public void A_visit_that_finished_the_job_completes_the_call()
    {
        // The whole point of the toggle: the call drops off the engineer's open list, so nobody is
        // sent back out to a machine that is already working.
        FieldCallRules.AfterVisit(FieldCallStatus.Incomplete, FieldCallStatus.Complete)
            .Should().Be(FieldCallStatus.Complete);
    }

    [Fact]
    public void A_visit_that_did_not_finish_leaves_the_call_open_for_the_next_one()
    {
        FieldCallRules.AfterVisit(FieldCallStatus.Incomplete, FieldCallStatus.Incomplete)
            .Should().Be(FieldCallStatus.Incomplete);
    }

    [Fact]
    public void A_completed_call_is_not_reopened_by_a_later_visit_that_ran_out_of_time()
    {
        // One engineer's unfinished afternoon must not retract the finished work another already
        // recorded - and nothing in this register reopens anything, so there would be no way back.
        FieldCallRules.AfterVisit(FieldCallStatus.Complete, FieldCallStatus.Incomplete)
            .Should().Be(FieldCallStatus.Complete);
    }

    [Fact]
    public void A_completed_call_stays_completed()
    {
        FieldCallRules.AfterVisit(FieldCallStatus.Complete, FieldCallStatus.Complete)
            .Should().Be(FieldCallStatus.Complete);
    }

    // ---------------------------------------------------------------- correcting a call

    [Fact]
    public void A_correction_cannot_reopen_a_completed_call()
    {
        // The same forward-only rule AfterVisit holds, which is the point - an edit is not a way
        // round it.
        FieldCallRules.Reopening(FieldCallStatus.Complete, FieldCallStatus.Incomplete)
            .Should().NotBeNull();
    }

    [Fact]
    public void A_correction_that_reopens_a_call_says_what_to_do_instead()
    {
        // The engineer standing at a machine that is not fixed needs somewhere to put that, so the
        // refusal names it rather than only saying no.
        FieldCallRules.Reopening(FieldCallStatus.Complete, FieldCallStatus.Incomplete)
            .Should().Contain("new call");
    }

    [Fact]
    public void A_correction_may_close_a_call_that_is_still_open()
    {
        // The gap this endpoint fills: a demo or a dealer visit never gets a field service, so
        // nothing else would ever take it off the engineer's open list.
        FieldCallRules.Reopening(FieldCallStatus.Incomplete, FieldCallStatus.Complete)
            .Should().BeNull();
    }

    [Theory]
    [InlineData(FieldCallStatus.Incomplete)]
    [InlineData(FieldCallStatus.Complete)]
    public void A_correction_that_leaves_the_status_alone_is_never_refused(FieldCallStatus standing)
    {
        // Every save from the form posts the status it is showing, so a correction to the remarks of
        // a closed call arrives as Complete against Complete. Refusing that would make a finished
        // call uneditable.
        FieldCallRules.Reopening(standing, standing).Should().BeNull();
    }
}
