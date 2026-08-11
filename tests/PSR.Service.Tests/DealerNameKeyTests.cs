using FluentAssertions;
using PSR.Service.Api.Reference;
using Xunit;

namespace PSR.Service.Tests;

/// <summary>The dealer import leans entirely on this key: too loose and two real dealers merge into
/// one, too tight and the same dealer is imported again under a second spelling.</summary>
public class DealerNameKeyTests
{
    [Theory]
    [InlineData("M/s. Sri Ram & Co.", "SRI RAM AND CO")]
    [InlineData("M/S SRI RAM AND CO", "SRI RAM AND CO")]
    [InlineData("  sri   ram  and co ", "SRI RAM AND CO")]
    [InlineData("Sri Ram &Co", "SRI RAM AND CO")]
    [InlineData("MESSRS. Sri Ram and Co", "SRI RAM AND CO")]
    public void Spelling_variants_collapse_to_one_key(string raw, string expected)
        => DealerNameKey.Normalize(raw).Should().Be(expected);

    [Theory]
    [InlineData("Agri Tech Pvt Ltd", "Agro Tech Pvt Ltd")]
    [InlineData("Dealer One", "Dealer Two")]
    public void Genuinely_different_names_keep_different_keys(string a, string b)
        => DealerNameKey.Normalize(a).Should().NotBe(DealerNameKey.Normalize(b));

    [Fact]
    public void Clean_keeps_casing_but_collapses_whitespace()
        => DealerNameKey.Clean("  Sri   Ram  & Co.  ").Should().Be("Sri Ram & Co.");

    [Theory]
    [InlineData("SRI RAM AND CO", "SRI RAM AND COO")]        // one typo
    [InlineData("BALAJI AGENCIES", "BALAJI AGENCIS")]         // dropped letter
    public void Near_matches_are_flagged(string a, string b)
        => DealerNameKey.IsNearMatch(a, b).Should().BeTrue();

    [Theory]
    [InlineData("SRI RAM AND CO", "BALAJI AGENCIES")]
    [InlineData("ABCD", "WXYZ")]                              // too short to judge
    [InlineData("DEALER ONE", "DEALER ONE HYDERABAD BRANCH")] // a real second branch, not a typo
    public void Unrelated_names_are_not_flagged(string a, string b)
        => DealerNameKey.IsNearMatch(a, b).Should().BeFalse();

    [Fact]
    public void Empty_and_punctuation_only_names_normalize_to_empty()
    {
        DealerNameKey.Normalize("   ").Should().BeEmpty();
        DealerNameKey.Normalize("-- .. //").Should().BeEmpty();
    }
}
