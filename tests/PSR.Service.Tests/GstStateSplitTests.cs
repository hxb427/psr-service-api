using FluentAssertions;
using PSR.Service.Api.Documents;
using Xunit;

namespace PSR.Service.Tests;

/// <summary>Which tax heads a document bills. Kerala (32) is the company's own state, so a Kerala party
/// is billed CGST + SGST and everybody else IGST — read off the party's state rather than chosen on the
/// form, which is the point: the person raising a PI picks a state, not a tax treatment.</summary>
public class GstStateSplitTests
{
    private const string Home = "32";          // CompanyInfo.StateCode

    private static bool Inter(string? code, string? name = null, string? gstin = null)
        => GstStates.IsInterState(code, name, gstin, Home);

    [Theory]
    [InlineData("32")]
    [InlineData("32 ")]
    [InlineData("32 - Kerala")]
    public void Kerala_is_billed_intra_state(string code) => Inter(code).Should().BeFalse();

    [Theory]
    [InlineData("33")]      // Tamil Nadu
    [InlineData("29")]      // Karnataka
    [InlineData("07")]      // Delhi
    [InlineData("97")]      // Other Territory — outside every state, so still across a line
    public void Any_other_state_is_billed_inter_state(string code) => Inter(code).Should().BeTrue();

    /// <summary>A single-digit code is the leading-zero one typed without its zero, not a missing state.</summary>
    [Theory]
    [InlineData("7", "07")]
    [InlineData("07", "07")]
    [InlineData("9", "09")]
    public void A_single_digit_code_is_padded(string typed, string expected)
        => GstStates.NormalizeCode(typed).Should().Be(expected);

    [Fact]
    public void A_code_with_no_digits_resolves_to_nothing()
        => GstStates.NormalizeCode("Kerala").Should().BeNull();

    /// <summary>Party masters hold names typed long before any picker existed, so a name alone has to
    /// decide the split — including the spellings nobody standardised.</summary>
    [Theory]
    [InlineData("Kerala", false)]
    [InlineData("kerala", false)]
    [InlineData("Tamil Nadu", true)]
    [InlineData("Tamilnadu", true)]
    [InlineData("Orissa", true)]
    public void A_state_name_alone_decides_the_split(string name, bool expected)
        => Inter(null, name).Should().Be(expected);

    /// <summary>A GSTIN opens with the code of the state that issued it, which beats a typed name: the
    /// registration cannot be in a state other than the one it was granted in.</summary>
    [Fact]
    public void A_gstin_decides_the_split_when_no_code_was_given()
        => Inter(null, null, "33AAAAA0000A1Z5").Should().BeTrue();

    [Fact]
    public void A_gstin_beats_a_state_name_that_disagrees_with_it()
        => Inter(null, "Kerala", "33AAAAA0000A1Z5").Should().BeTrue();

    /// <summary>The code is what the picker fills in, so an explicit one is the last word — including
    /// when it disagrees with the GSTIN, which is a data error for the person on the form to see rather
    /// than something to be overruled while the document is being built.</summary>
    [Fact]
    public void An_explicit_code_beats_the_gstin()
        => Inter("32", null, "33AAAAA0000A1Z5").Should().BeFalse();

    /// <summary>A walk-in with nothing recorded is standing at the counter, in Kerala.</summary>
    [Fact]
    public void A_party_with_no_state_at_all_is_billed_intra_state()
        => Inter(null, null, null).Should().BeFalse();

    /// <summary>Nothing can be said to share a state with a company that has not declared one, so the
    /// safe answer is the one every document gave while the split was switched off.</summary>
    [Fact]
    public void A_company_with_no_state_code_bills_igst()
        => GstStates.IsInterState("32", "Kerala", null, null).Should().BeTrue();

    /// <summary>A code retired years ago still has to resolve: it is sitting in a party master, and it
    /// is not 32, so it bills IGST and prints the name it was typed under.</summary>
    [Fact]
    public void A_retired_code_still_resolves_and_bills_igst()
    {
        Inter("28").Should().BeTrue();
        GstStates.ByCode("28")!.Name.Should().Be("Andhra Pradesh");
    }

    /// <summary>What the picker offers: each code once, none of the retired ones, and the company's own
    /// state among them — a list a Kerala shop cannot pick Kerala from would be no use at all.</summary>
    [Fact]
    public void The_offered_list_is_one_entry_per_live_code()
    {
        GstStates.All.Select(s => s.Code).Should().OnlyHaveUniqueItems();
        GstStates.All.Select(s => s.Code).Should().NotContain("25").And.NotContain("28");
        GstStates.All.Should().Contain(s => s.Code == Home && s.Name == "Kerala");
        GstStates.All.Should().OnlyContain(s => s.Code.Length == 2 && s.Name.Length > 0);
    }

    /// <summary>The name filled in beside the code on the document, from whichever of the three the party
    /// turned up with.</summary>
    [Theory]
    [InlineData("33", null, null, "Tamil Nadu")]
    [InlineData(null, "Tamilnadu", null, "Tamil Nadu")]
    [InlineData(null, null, "33AAAAA0000A1Z5", "Tamil Nadu")]
    public void A_state_resolves_to_its_printed_name(string? code, string? name, string? gstin, string expected)
        => GstStates.Resolve(code, name, gstin)!.Name.Should().Be(expected);

    /// <summary>A code outside the list — 99, or anything else that turns up — bills on the code alone
    /// and prints without a name rather than being quietly reassigned to a state that does exist.</summary>
    [Fact]
    public void An_unlisted_code_bills_on_the_code_alone()
    {
        Inter("99").Should().BeTrue();
        GstStates.Resolve("99", null, null).Should().BeNull();
        GstStates.ResolveCode("99", null, null).Should().Be("99");
    }
}
