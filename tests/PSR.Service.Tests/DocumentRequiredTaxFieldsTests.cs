using FluentAssertions;
using PSR.Service.Api.Documents;
using Xunit;

namespace PSR.Service.Tests;

/// <summary>The two tax fields a document cannot be raised without, both of which used to be settled by
/// default instead of being read off the record that knows.
///
/// The HSN/SAC printed as a dash on every line nobody had typed one into, which on a delivery challan was
/// every line of every challan — a DC is the one document nobody stops to price, so nobody opened the
/// column either. It now comes off the catalogue item the unit's PS code names, the same match the rest of
/// the app makes. The party's state, when the party master held none, was taken to be the shop's own,
/// which meant a walk-in was billed CGST + SGST because no question had been asked rather than because
/// anyone decided they were local.</summary>
public class DocumentRequiredTaxFieldsTests
{
    // ---- HSN/SAC on a line ----

    private static string Hsn(string? typed, string? catalogue, string? psCode = "PS-900")
        => BillingService.RequireLineHsn(typed, catalogue, "PSR-1043", psCode);

    /// <summary>The ordinary case now: nobody typed anything and the price list already knows.</summary>
    [Fact]
    public void The_catalogue_code_is_used_when_nothing_was_typed()
        => Hsn(null, " 84239090 ").Should().Be("84239090");

    /// <summary>A typed code is a decision, so it beats the catalogue rather than being overwritten by
    /// it — a manager may bill one unit under something else.</summary>
    [Fact]
    public void A_typed_code_beats_the_catalogue()
        => Hsn("  998719 ", "84239090").Should().Be("998719");

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_typed_code_falls_through_to_the_catalogue(string typed)
        => Hsn(typed, "84239090").Should().Be("84239090");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_line_with_no_code_anywhere_is_refused(string? catalogue)
    {
        var act = () => Hsn(null, catalogue);

        act.Should().Throw<BillingException>().WithMessage("*HSN/SAC*");
    }

    /// <summary>Which line to go and fill in, and where. A document covers several units of one customer,
    /// and the two gaps are mended in different places — the job's PS code, or the catalogue item.</summary>
    [Fact]
    public void A_job_with_no_ps_code_is_told_to_put_one_on()
    {
        var act = () => Hsn(null, null, psCode: null);

        act.Should().Throw<BillingException>()
            .WithMessage("PSR-1043 has no PS code*");
    }

    [Fact]
    public void A_ps_code_the_catalogue_has_no_code_for_names_the_code()
    {
        var act = () => Hsn(null, null, psCode: " PS-900 ");

        act.Should().Throw<BillingException>()
            .WithMessage("PSR-1043: no HSN/SAC for PS code PS-900*");
    }

    // ---- the party's state ----

    /// <summary>What the generate form sends once a state is picked: the code is what the split is read
    /// off, and it resolves on its own.</summary>
    [Fact]
    public void A_picked_state_resolves_to_its_code()
        => GstStates.ResolveCode("33", "Tamil Nadu", null).Should().Be("33");

    /// <summary>A party master typed before any picker existed carries a name and no code, and a dealer
    /// may carry only its registration. Both still settle the state, so neither is refused.</summary>
    [Theory]
    [InlineData(null, "Kerala", null, "32")]
    [InlineData(null, "Tamilnadu", null, "33")]
    [InlineData(null, null, "29AAAAA0000A1Z5", "29")]
    public void A_state_still_resolves_from_a_name_or_a_gstin(
        string? code, string? name, string? gstin, string expected)
        => GstStates.ResolveCode(code, name, gstin).Should().Be(expected);

    /// <summary>The case the document is refused on: nothing anywhere says where the party is. It used to
    /// come out of here as null and be billed as local.</summary>
    [Theory]
    [InlineData(null, null, null)]
    [InlineData("", "", "")]
    [InlineData("   ", "   ", "   ")]
    [InlineData(null, "Kerla", null)]          // a misspelling names no state, so nothing is assumed
    public void A_party_with_nothing_recorded_resolves_to_no_state(string? code, string? name, string? gstin)
        => GstStates.ResolveCode(code, name, gstin).Should().BeNull();
}
