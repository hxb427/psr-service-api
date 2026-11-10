using FluentAssertions;
using PSR.Service.Api.Data.Entities;
using PSR.Service.Api.Stock;
using Xunit;

namespace PSR.Service.Tests;

/// <summary>
/// A visit may bill several service charges. Clients written before it could — the desktop builds and
/// the phones already in the field — read one charge from four fields, so the response rolls the list
/// up into them.
///
/// These pin the honest part of that roll-up. The AMOUNT must sum, because a client showing one figure
/// has to show the one the customer paid; the ID and the RATE must not, because an id names one
/// catalogue row and a rate is a percentage, and a visit with two charges has no truthful single value
/// for either. Getting that backwards would put a wrong rate on a receipt rather than no rate, which
/// is the failure worth a test.
/// </summary>
public class FieldServiceChargeRollUpTests
{
    private static FieldServiceCharge Charge(long id, string name, decimal amount, decimal taxPercent) =>
        new() { ServiceChargeId = id, Description = name, Amount = amount, TaxPercent = taxPercent };

    [Fact]
    public void A_visit_with_no_charges_rolls_up_to_nothing()
    {
        var (id, description, amount, taxPercent) = FieldOpsEndpoints.ChargeRollUp([]);

        id.Should().BeNull();
        description.Should().BeNull();
        amount.Should().Be(0m);
        taxPercent.Should().BeNull();
    }

    /// <summary>The ordinary case, and it has to come through completely unchanged: this is what every
    /// visit filed before the list existed looks like.</summary>
    [Fact]
    public void One_charge_rolls_up_to_itself()
    {
        var (id, description, amount, taxPercent) =
            FieldOpsEndpoints.ChargeRollUp([Charge(7, "Calibration", 500m, 18m)]);

        id.Should().Be(7);
        description.Should().Be("Calibration");
        amount.Should().Be(500m);
        taxPercent.Should().Be(18m);
    }

    [Fact]
    public void Several_charges_sum_under_a_joined_description()
    {
        var (_, description, amount, _) = FieldOpsEndpoints.ChargeRollUp(
            [Charge(7, "Calibration", 500m, 18m), Charge(9, "Mainboard replacement", 1250m, 18m)]);

        description.Should().Be("Calibration + Mainboard replacement");
        amount.Should().Be(1750m);
    }

    /// <summary>Null, not the first one's. An id that names one of two charges reads as "this visit
    /// billed that charge", which is a different and smaller thing than what happened — and anything
    /// grouping visits by charge would quietly undercount the other.</summary>
    [Fact]
    public void Several_charges_roll_up_to_no_single_id()
    {
        FieldOpsEndpoints.ChargeRollUp(
                [Charge(7, "Calibration", 500m, 18m), Charge(9, "Mainboard", 1250m, 18m)])
            .Id.Should().BeNull();
    }

    /// <summary>Null even when the two charges happen to agree on 18%. The rate stops being a property
    /// of the visit the moment there is more than one charge, and a client that prints it beside a
    /// summed figure would be printing tax on a base that was never taxed as one.</summary>
    [Fact]
    public void Several_charges_roll_up_to_no_single_tax_rate()
    {
        FieldOpsEndpoints.ChargeRollUp(
                [Charge(7, "Calibration", 500m, 18m), Charge(9, "Mainboard", 1250m, 18m)])
            .TaxPercent.Should().BeNull();
    }
}
