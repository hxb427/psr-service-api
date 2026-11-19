using FluentAssertions;
using PSR.Service.Api.Data.Entities;
using PSR.Service.Api.Documents;
using Xunit;

namespace PSR.Service.Tests;

/// <summary>A PI / invoice bills a job's service charges, and its component prices only when somebody has
/// ticked "add component prices" — off by default. These pin which lines each figure is built from, and
/// that the choice is fixed once a PI or an invoice has priced the job.</summary>
public class ComponentPricingTests
{
    private static readonly PricedLine Charge = new(ServiceLineType.ServiceCharge, 500m, 18m);      // 590 incl.
    private static readonly PricedLine Component = new(ServiceLineType.Component, 1000m, 12m);      // 1120 incl.
    private static readonly PricedLine Replacement = new(ServiceLineType.Replacement, 4000m, 18m);  // 4720 incl.

    [Fact]
    public void Charges_only_leaves_the_components_out()
    {
        var pricing = BillingService.PriceLines([Charge, Component], inWarranty: false);

        pricing.ChargesOnly.UnitInclusive.Should().Be(590m);
        pricing.ChargesOnly.BlendedGst.Should().Be(18m);
    }

    [Fact]
    public void With_components_adds_them_at_their_own_gst()
    {
        var pricing = BillingService.PriceLines([Charge, Component], inWarranty: false);

        // 500 + 1000 taxable, 90 + 120 tax — the blended rate is the one that reproduces that tax.
        pricing.WithComponents.UnitInclusive.Should().Be(1710m);
        pricing.WithComponents.BlendedGst.Should().Be(14m);
    }

    [Fact]
    public void The_default_setting_bills_charges_only()
    {
        var job = new ServiceJob();
        var pricing = BillingService.PriceLines([Charge, Component], inWarranty: false);

        job.IncludeComponentPrices.Should().BeFalse("a component is billed because somebody ticked it");
        pricing.For(job.IncludeComponentPrices).Should().Be(pricing.ChargesOnly);
    }

    /// <summary>A replacement line is the unit the customer leaves with, not a component. Taking it out with
    /// the components is how an out-of-warranty replacement went out billing nothing.</summary>
    [Fact]
    public void A_replacement_unit_is_billed_either_way()
    {
        var pricing = BillingService.PriceLines([Replacement], inWarranty: false);

        pricing.ChargesOnly.UnitInclusive.Should().Be(4720m);
        pricing.WithComponents.UnitInclusive.Should().Be(4720m);
        pricing.HasComponents.Should().BeFalse();
    }

    [Fact]
    public void In_warranty_prices_at_nothing_either_way()
    {
        var pricing = BillingService.PriceLines([Charge, Component], inWarranty: true);

        pricing.ChargesOnly.UnitInclusive.Should().Be(0m);
        pricing.WithComponents.UnitInclusive.Should().Be(0m);
    }

    [Fact]
    public void A_job_with_only_components_bills_nothing_until_ticked()
    {
        var pricing = BillingService.PriceLines([Component], inWarranty: false);

        pricing.HasComponents.Should().BeTrue();
        pricing.ChargesOnly.UnitInclusive.Should().Be(0m);
        pricing.WithComponents.UnitInclusive.Should().Be(1120m);
    }

    /// <summary>A component added on the PI / invoice form was added to be billed, so it is in the
    /// charges-only figure too — the tick is about what the technician fitted.</summary>
    [Fact]
    public void A_component_added_at_billing_is_billed_with_the_tick_off()
    {
        var addedComponent = Component with { AddedAtBilling = true };
        var pricing = BillingService.PriceLines([Charge, addedComponent], inWarranty: false);

        pricing.ChargesOnly.UnitInclusive.Should().Be(1710m);
        pricing.WithComponents.UnitInclusive.Should().Be(1710m);
    }

    /// <summary>The tick has nothing to add on a job whose only components came in at billing.</summary>
    [Fact]
    public void Components_added_at_billing_give_the_tick_nothing_to_add()
        => BillingService.PriceLines([Charge, Component with { AddedAtBilling = true }], inWarranty: false)
            .HasComponents.Should().BeFalse();

    /// <summary>The total-loss / swap case the form exists for: the job reaches billing with the replacement
    /// line only, and the service charge added on the form goes on top of it.</summary>
    [Fact]
    public void A_charge_added_at_billing_goes_on_top_of_a_replacement()
    {
        var addedCharge = Charge with { AddedAtBilling = true };
        var pricing = BillingService.PriceLines([Replacement, addedCharge], inWarranty: false);

        pricing.ChargesOnly.UnitInclusive.Should().Be(4720m + 590m);
    }

    /// <summary>Added or not, nothing is billed under warranty — which is why the form refuses to add there.</summary>
    [Fact]
    public void Lines_added_at_billing_still_price_at_nothing_in_warranty()
        => BillingService.PriceLines([Charge with { AddedAtBilling = true }], inWarranty: true)
            .ChargesOnly.UnitInclusive.Should().Be(0m);

    [Fact]
    public void The_setting_is_open_until_a_pi_or_invoice_prices_the_job()
    {
        BillingService.ComponentPricingFixedBy(new ServiceJob()).Should().BeNull();
        // A delivery challan does not fix it — it covers in-warranty units, which price at nothing anyway.
        BillingService.ComponentPricingFixedBy(new ServiceJob { OutwardDcNo = "DC0001" }).Should().BeNull();
        BillingService.ComponentPricingFixedBy(new ServiceJob { PiNo = " " }).Should().BeNull();
    }

    [Fact]
    public void A_pi_or_invoice_fixes_the_setting_and_names_itself()
    {
        BillingService.ComponentPricingFixedBy(new ServiceJob { PiNo = "PI0042" }).Should().Be("PI0042");
        // The invoice is the later of the two, so it is the one named.
        BillingService.ComponentPricingFixedBy(new ServiceJob { PiNo = "PI0042", InvNo = "INV0007" })
            .Should().Be("INV0007");
    }
}
