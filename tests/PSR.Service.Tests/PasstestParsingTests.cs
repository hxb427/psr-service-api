using FluentAssertions;
using PSR.Service.Api.MachineTests;
using Xunit;

namespace PSR.Service.Tests;

/// <summary>passtestdata.Warranty is a hand-typed VARCHAR and InvDate has held strings, so both are
/// parsed rather than cast. Getting either wrong changes a warranty verdict.</summary>
public class PasstestParsingTests
{
    [Theory]
    [InlineData("24", 24)]
    [InlineData("12 months", 12)]
    [InlineData(" 18 ", 18)]
    [InlineData("15/r", 15)]          // "/r" marks a replacement unit; months are before the slash
    [InlineData("12/R", 12)]
    [InlineData("warranty 6 m", 6)]
    public void Warranty_column_yields_months(string raw, int expected)
        => PasstestRepository.ParseWarrantyMonths(raw).Should().Be(expected);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("NA")]
    [InlineData("-")]
    [InlineData("0")]                 // 0 months is "not known", same convention as Dealer.WarrantyMonths
    [InlineData("/r")]
    public void Unusable_warranty_text_yields_null(string? raw)
        => PasstestRepository.ParseWarrantyMonths(raw).Should().BeNull();

    [Theory]
    [InlineData("2024-01-15", 2024, 1, 15)]
    [InlineData("2024/01/15", 2024, 1, 15)]
    [InlineData("2024-01-15 00:00:00", 2024, 1, 15)]
    [InlineData("15-01-2024", 2024, 1, 15)]   // typed Indian order — day first
    [InlineData("15/01/2024", 2024, 1, 15)]
    [InlineData("01/02/2024", 2024, 2, 1)]    // 1 Feb, not 2 Jan — invariant parsing would flip it
    public void Invoice_date_parses_both_orders(string raw, int y, int m, int d)
        => PasstestRepository.ParseLegacyDate(raw).Should().Be(new DateTime(y, m, d));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a date")]
    [InlineData("32/01/2024")]        // impossible day must not throw
    [InlineData("15/13/2024")]        // impossible month
    public void Unusable_dates_yield_null(string? raw)
        => PasstestRepository.ParseLegacyDate(raw).Should().BeNull();
}
