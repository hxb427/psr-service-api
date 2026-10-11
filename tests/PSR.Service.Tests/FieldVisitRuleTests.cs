using FluentAssertions;
using PSR.Service.Api.Data.Entities;
using PSR.Service.Api.Stock;
using Xunit;

namespace PSR.Service.Tests;

/// <summary>
/// What a field visit comes to, and what its request is allowed to say.
///
/// These figures end up on a piece of paper a customer keeps, so the arithmetic has to tie: the line
/// amounts and the tax have to reproduce the total rather than miss it by a paisa, and the total has to
/// be the figure the technician actually asks for.
/// </summary>
public class FieldVisitRuleTests
{
    private static Part Priced(decimal dealer, decimal customer, decimal gst = 18m) => new()
    { Id = 1, ItemCode = "PS-100", Name = "Sensor board", DealerRate = dealer, CustomerRate = customer, GstPercent = gst };

    // ---------------------------------------------------------------- which column bills

    [Fact]
    public void A_customer_visit_bills_the_customer_column()
    {
        FieldVisitRules.UnitRate(Priced(dealer: 800m, customer: 1000m), FieldRateType.Customer)
            .Should().Be(1000m);
    }

    [Fact]
    public void A_dealer_visit_bills_the_dealer_column()
    {
        FieldVisitRules.UnitRate(Priced(dealer: 800m, customer: 1000m), FieldRateType.Dealer)
            .Should().Be(800m);
    }

    /// <summary>A counter sale refuses an unpriced part; a field visit does not, and the asymmetry is
    /// deliberate. A sale can wait for someone to price the item. The service has already happened —
    /// the part is fitted, the stock is gone and the serial has moved — so refusing the save would
    /// throw the whole record away over a gap in the parts master.</summary>
    [Fact]
    public void An_unpriced_part_is_recorded_at_zero_rather_than_refusing_the_visit()
    {
        FieldVisitRules.UnitRate(Priced(dealer: 0m, customer: 0m), FieldRateType.Customer)
            .Should().Be(0m);
    }

    // ---------------------------------------------------------------- tax

    [Fact]
    public void Tax_is_rounded_to_the_paise()
    {
        // 1234.57 x 18% = 222.2226
        FieldVisitRules.Tax(1234.57m, 18m).Should().Be(222.22m);
    }

    [Fact]
    public void A_zero_rated_part_carries_no_tax()
    {
        FieldVisitRules.Tax(1000m, 0m).Should().Be(0m);
    }

    // ---------------------------------------------------------------- totals

    [Fact]
    public void A_visit_adds_its_lines_and_its_service_charge_together()
    {
        var totals = FieldVisitRules.Totals(
            [(Amount: 1000m, TaxAmount: 180m), (Amount: 250m, TaxAmount: 45m)],
            chargeAmount: 300m, chargeTaxPercent: 18m, warranty: FieldWarrantyStatus.Out);

        totals.Taxable.Should().Be(1550m);           // 1000 + 250 + 300
        totals.Tax.Should().Be(279m);                // 180 + 45 + 54
        totals.Total.Should().Be(1829m);
        totals.Payable.Should().Be(1829m);
    }

    /// <summary>Up to the next whole rupee, the rule every other payable figure in this system uses.
    /// Nobody at a customer's gate hands back paise.</summary>
    [Fact]
    public void The_total_is_rounded_up_to_the_rupee()
    {
        var totals = FieldVisitRules.Totals(
            [(Amount: 100.40m, TaxAmount: 18.07m)], 0m, 0m, FieldWarrantyStatus.Out);

        totals.Taxable.Should().Be(100.40m);
        totals.Tax.Should().Be(18.07m);
        totals.Total.Should().Be(119m);              // 118.47 -> 119
    }

    [Fact]
    public void A_whole_total_is_not_pushed_up_a_rupee()
    {
        FieldVisitRules.Totals([(Amount: 100m, TaxAmount: 18m)], 0m, 0m, null).Total.Should().Be(118m);
    }

    [Fact]
    public void A_visit_with_nothing_on_it_comes_to_nothing()
    {
        var totals = FieldVisitRules.Totals([], 0m, 0m, null);

        totals.Taxable.Should().Be(0m);
        totals.Tax.Should().Be(0m);
        totals.Total.Should().Be(0m);
        totals.Payable.Should().Be(0m);
    }

    /// <summary>A visit that fitted nothing but took an hour still bills the hour.</summary>
    [Fact]
    public void A_service_charge_alone_is_a_billable_visit()
    {
        var totals = FieldVisitRules.Totals([], chargeAmount: 500m, chargeTaxPercent: 18m,
            warranty: FieldWarrantyStatus.Out);

        totals.Taxable.Should().Be(500m);
        totals.Tax.Should().Be(90m);
        totals.Total.Should().Be(590m);
    }

    // ---------------------------------------------------------------- warranty

    /// <summary>The one rule that makes the fourth figure worth storing. An in-warranty repair still
    /// consumed parts and somebody's day, so the record keeps what it was worth; the customer is asked
    /// for nothing.</summary>
    [Fact]
    public void An_in_warranty_visit_records_its_value_and_collects_nothing()
    {
        var totals = FieldVisitRules.Totals(
            [(Amount: 4000m, TaxAmount: 720m)],
            chargeAmount: 500m, chargeTaxPercent: 18m, warranty: FieldWarrantyStatus.In);

        totals.Taxable.Should().Be(4500m);
        totals.Tax.Should().Be(810m);
        totals.Total.Should().Be(5310m, "the shop absorbed this much, and needs to know it did");
        totals.Payable.Should().Be(0m);
    }

    /// <summary>Only <see cref="FieldWarrantyStatus.In"/> waives the bill. Unknown means the technician
    /// could not establish it, and waiving a bill on a maybe is how the shop ends up giving away
    /// repairs it was owed for — the customer is charged and can produce a warranty card afterwards.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData(FieldWarrantyStatus.Out)]
    [InlineData(FieldWarrantyStatus.Unknown)]
    public void Anything_short_of_in_warranty_is_still_payable(FieldWarrantyStatus? warranty)
    {
        var totals = FieldVisitRules.Totals([(Amount: 1000m, TaxAmount: 180m)], 0m, 0m, warranty);

        totals.Payable.Should().Be(totals.Total).And.Be(1180m);
    }

    // ---------------------------------------------------------------- what a request may say

    [Fact]
    public void An_absent_rate_type_means_customer()
    {
        FieldVisitRules.ParseRateType(null).Should().Be((FieldRateType.Customer, (string?)null));
        FieldVisitRules.ParseRateType("  ").Should().Be((FieldRateType.Customer, (string?)null));
    }

    [Theory]
    [InlineData("dealer", FieldRateType.Dealer)]
    [InlineData("DEALER", FieldRateType.Dealer)]
    [InlineData(" Customer ", FieldRateType.Customer)]
    public void Rate_type_is_read_case_insensitively_and_trimmed(string raw, FieldRateType expected)
    {
        var (value, error) = FieldVisitRules.ParseRateType(raw);

        value.Should().Be(expected);
        error.Should().BeNull();
    }

    /// <summary>A misspelt rate type is refused rather than quietly defaulted. Defaulting it would bill
    /// a dealer at customer rates and nothing on the record would say why.</summary>
    [Fact]
    public void An_unknown_rate_type_is_refused()
    {
        FieldVisitRules.ParseRateType("wholesale").error
            .Should().Be("Unknown rate type 'wholesale'. Use Customer or Dealer.");
    }

    /// <summary>Absent is a real answer here: a visit settled on account, or one that is covered and
    /// owes nothing, has no payment method, and defaulting to Cash would put money in the record that
    /// nobody handed over.</summary>
    [Fact]
    public void An_absent_payment_method_stays_absent()
    {
        FieldVisitRules.ParsePaymentMethod(null).Should().Be(((FieldPaymentMethod?)null, (string?)null));
    }

    [Theory]
    [InlineData("Cash", FieldPaymentMethod.Cash)]
    [InlineData("upi", FieldPaymentMethod.Upi)]
    [InlineData("BankTransfer", FieldPaymentMethod.BankTransfer)]
    public void The_three_payment_methods_are_accepted(string raw, FieldPaymentMethod expected)
    {
        FieldVisitRules.ParsePaymentMethod(raw).value.Should().Be(expected);
    }

    [Fact]
    public void An_unknown_payment_method_is_refused()
    {
        FieldVisitRules.ParsePaymentMethod("Gpay").error
            .Should().Be("Unknown payment method 'Gpay'. Use Cash, Upi or BankTransfer.");
    }

    /// <summary>Null and Unknown are different answers and both are storable — nobody asked, against
    /// the technician saying they could not tell.</summary>
    [Fact]
    public void Absent_warranty_is_not_the_same_as_unknown_warranty()
    {
        FieldVisitRules.ParseWarrantyStatus(null).value.Should().BeNull();
        FieldVisitRules.ParseWarrantyStatus("Unknown").value.Should().Be(FieldWarrantyStatus.Unknown);
    }

    [Fact]
    public void An_unknown_warranty_status_is_refused()
    {
        FieldVisitRules.ParseWarrantyStatus("expired").error
            .Should().Be("Unknown warranty status 'expired'. Use In, Out or Unknown.");
    }

    // ---------------------------------------------------------------- the GPS fix

    /// <summary>No fix at all is fine. The technician may have been in a basement, or declined the
    /// permission, and the visit still happened — this is why the field is not a gate.</summary>
    [Fact]
    public void No_fix_is_acceptable()
    {
        FieldVisitRules.ValidateFix(null, null).Should().BeNull();
    }

    /// <summary>Half a fix is not a weaker claim about where the technician was, it is not a claim at
    /// all — and stored, it would leave a record that looks located and cannot be mapped.</summary>
    [Theory]
    [InlineData(10.5, null)]
    [InlineData(null, 76.2)]
    public void Half_a_fix_is_refused(double? lat, double? lng)
    {
        FieldVisitRules.ValidateFix((decimal?)lat, (decimal?)lng)
            .Should().Be("A location fix needs both latitude and longitude.");
    }

    [Fact]
    public void A_real_fix_is_accepted()
    {
        // Thrissur, give or take.
        FieldVisitRules.ValidateFix(10.527100m, 76.214400m).Should().BeNull();
    }

    [Theory]
    [InlineData(-91, 0)]
    [InlineData(91, 0)]
    public void An_impossible_latitude_is_refused(double lat, double lng)
    {
        FieldVisitRules.ValidateFix((decimal)lat, (decimal)lng)
            .Should().Be("Latitude must be between -90 and 90.");
    }

    [Theory]
    [InlineData(0, -181)]
    [InlineData(0, 181)]
    public void An_impossible_longitude_is_refused(double lat, double lng)
    {
        FieldVisitRules.ValidateFix((decimal)lat, (decimal)lng)
            .Should().Be("Longitude must be between -180 and 180.");
    }

    /// <summary>Null Island is a real coordinate and a common sign of a failed fix, but refusing it
    /// here would mean inventing a rule the receiver of this data has not asked for. It is accepted and
    /// left visible.</summary>
    [Fact]
    public void The_origin_is_a_valid_coordinate()
    {
        FieldVisitRules.ValidateFix(0m, 0m).Should().BeNull();
    }
}
