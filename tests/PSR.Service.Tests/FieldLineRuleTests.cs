using FluentAssertions;
using PSR.Service.Api.Data.Entities;
using PSR.Service.Api.Stock;
using Xunit;

namespace PSR.Service.Tests;

/// <summary>
/// The two halves of a field service line are held to deliberately different standards, and the
/// asymmetry is the point of these tests.
///
/// A unit the technician FITS is one they are holding while they type, and which unit it was
/// decides the warranty it carries and the job it comes back on — so its serial is required.
///
/// A unit they COLLECT is one they just pulled off a machine, and the number is frequently
/// unreadable: burnt label, painted over, behind a bracket, unit in pieces. Demanding it there
/// bought nothing and cost a lot — the technician either abandoned the service record or typed a
/// guess, and a guessed serial silently welds one customer's history to another unit.
///
/// These pin that so a later tidy-up cannot quietly make the collected serial mandatory again.
/// </summary>
public class FieldLineRuleTests
{
    private static Part Tracked() => new()
    { Id = 1, ItemCode = "PS-100", Name = "Seal kit", IsSerialTracked = true };

    private static Part Untracked() => new()
    { Id = 2, ItemCode = "PS-200", Name = "Grease", IsSerialTracked = false };

    // ---------------------------------------------------------------- collected

    [Fact]
    public void A_collected_tracked_unit_may_be_recorded_without_its_serial()
    {
        FieldLineRules.Validate(FieldLineKind.Collected, Tracked(), qty: 1, serialNo: null)
            .Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_and_whitespace_serials_count_as_unnamed_rather_than_invalid(string? sn)
    {
        FieldLineRules.Validate(FieldLineKind.Collected, Tracked(), 1, sn).Should().BeNull();
        FieldLineRules.CollectedEntersSerialTracking(Tracked(), sn).Should().BeFalse();
    }

    [Fact]
    public void A_collected_untracked_part_is_a_quantity_and_never_asked_for_a_serial()
    {
        FieldLineRules.Validate(FieldLineKind.Collected, Untracked(), 3, null).Should().BeNull();
        FieldLineRules.CollectedEntersSerialTracking(Untracked(), null).Should().BeFalse();
    }

    /// <summary>A serial typed against a part nobody tracks is a note, not an identity — it must not
    /// drag the part into the serial ledger.</summary>
    [Fact]
    public void A_serial_on_an_untracked_part_does_not_start_tracking_it()
    {
        FieldLineRules.CollectedEntersSerialTracking(Untracked(), "SN-123").Should().BeFalse();
    }

    /// <summary>The whole reason the serial is still worth asking for: when it IS readable, the unit
    /// enters tracking and can later ride a Faulty return shipment into a repair job.</summary>
    [Fact]
    public void A_named_collected_unit_on_a_tracked_part_does_enter_tracking()
    {
        FieldLineRules.CollectedEntersSerialTracking(Tracked(), "SN-123").Should().BeTrue();
    }

    [Fact]
    public void A_collected_line_is_never_held_to_one_unit_per_line()
    {
        FieldLineRules.Validate(FieldLineKind.Collected, Tracked(), qty: 4, serialNo: null)
            .Should().BeNull();
    }

    // ---------------------------------------------------------------- used

    [Fact]
    public void A_fitted_tracked_unit_must_name_its_serial()
    {
        FieldLineRules.Validate(FieldLineKind.Used, Tracked(), 1, null)
            .Should().Be("PS-100 is serial-tracked — name the fitted serial.");
    }

    [Fact]
    public void A_fitted_tracked_unit_goes_up_one_per_line()
    {
        FieldLineRules.Validate(FieldLineKind.Used, Tracked(), qty: 2, serialNo: "SN-1")
            .Should().Be("PS-100 is serial-tracked — one line per unit (qty 1).");
    }

    [Fact]
    public void A_fitted_tracked_unit_with_its_serial_and_qty_one_is_accepted()
    {
        FieldLineRules.Validate(FieldLineKind.Used, Tracked(), 1, "SN-1").Should().BeNull();
    }

    [Fact]
    public void A_fitted_untracked_part_is_just_a_quantity()
    {
        FieldLineRules.Validate(FieldLineKind.Used, Untracked(), 5, null).Should().BeNull();
    }
}
