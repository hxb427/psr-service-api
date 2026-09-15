using FluentAssertions;
using PSR.Service.Api.Data.Entities;
using PSR.Service.Api.Reference;
using Xunit;

namespace PSR.Service.Tests;

/// <summary>The charges list is ordered by name, except for the handful the bench reaches for on most
/// jobs — those are floated to the top of the technician's add-line dialog. The ranking matches on a
/// squashed, case-folded name, because the catalogue does not agree with itself about spacing or case
/// ("Sensor Board", "sensorboard") and a rule that only matched one spelling would silently do nothing.</summary>
public class ServiceChargeOrderTests
{
    private static ServiceCharge Charge(string name) => new() { Name = name };

    [Theory]
    [InlineData("Mainboard", 0)]
    [InlineData("MAINBOARD REPLACEMENT", 0)]
    [InlineData("Main Board Replacement", 0)]
    [InlineData("Sensorboard", 1)]
    [InlineData("Sensor Board Replacement", 1)]
    [InlineData("Calibration", 2)]
    [InlineData("Re-Calibration charge", 2)]
    public void Leading_charges_rank_ahead_of_the_rest(string name, int expected) =>
        ServiceChargesEndpoints.Rank(Charge(name)).Should().Be(expected);

    [Theory]
    [InlineData("Service charge")]
    [InlineData("Transport")]
    [InlineData("Board")]          // not one of the three on its own
    public void Everything_else_ranks_behind_them(string name) =>
        ServiceChargesEndpoints.Rank(Charge(name))
            .Should().Be(ServiceChargesEndpoints.LeadingCharges.Length);

    /// <summary>OrderBy is stable, so the tail keeps the name order the query returned it in and only
    /// the three named charges move.</summary>
    [Fact]
    public void Ranking_floats_the_three_and_leaves_the_rest_alone()
    {
        var byName = new[] { "Calibration", "Fitting", "Mainboard replacement", "Sensor Board replacement", "Transport" }
            .Select(Charge).ToList();

        var ordered = byName.OrderBy(ServiceChargesEndpoints.Rank).Select(c => c.Name).ToList();

        ordered.Should().Equal(
            "Mainboard replacement", "Sensor Board replacement", "Calibration", "Fitting", "Transport");
    }
}
