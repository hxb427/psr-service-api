using FluentAssertions;
using PSR.Service.Api.Data.Entities;
using PSR.Service.Api.Services;
using Xunit;

namespace PSR.Service.Tests;

/// <summary>Every part-priced line on a service job — a component the technician fitted, the replacement
/// unit on a total loss or swap, and a component added on the PI / invoice form — is billed at the dealer
/// rate on a dealer's job and the customer rate on a direct customer's, as the old app's PI page billed
/// them. Each column falls back to the other when it is empty, also as the old app did.</summary>
public class PartyRateTests
{
    private static Part Part(decimal customer, decimal dealer) =>
        new() { Id = 1, ItemCode = "PS-1", Name = "Board", CustomerRate = customer, DealerRate = dealer };

    [Fact]
    public void A_dealers_job_is_billed_the_dealer_rate()
        => PartyRate.For(Part(customer: 1000m, dealer: 800m), isDealer: true).Should().Be(800m);

    [Fact]
    public void A_customers_job_is_billed_the_customer_rate()
        => PartyRate.For(Part(customer: 1000m, dealer: 800m), isDealer: false).Should().Be(1000m);

    [Fact]
    public void An_empty_column_falls_back_to_the_other()
    {
        PartyRate.For(Part(customer: 1000m, dealer: 0m), isDealer: true).Should().Be(1000m);
        PartyRate.For(Part(customer: 0m, dealer: 800m), isDealer: false).Should().Be(800m);
    }

    [Fact]
    public void A_part_with_neither_rate_prices_at_nothing()
        => PartyRate.For(Part(customer: 0m, dealer: 0m), isDealer: true).Should().Be(0m);

    /// <summary>Whose job it is comes off the dealer link — the same test billing uses for whose name goes
    /// on the document.</summary>
    [Fact]
    public void A_job_with_a_dealer_on_it_is_the_dealers()
    {
        PartyRate.IsDealer(new ServiceJob { DealerId = 4 }).Should().BeTrue();
        PartyRate.IsDealer(new ServiceJob { CustomerId = 9 }).Should().BeFalse();
        PartyRate.For(Part(customer: 1000m, dealer: 800m), new ServiceJob { DealerId = 4 }).Should().Be(800m);
    }

    /// <summary>The generate form shows which column a component was taken at, fallbacks included.</summary>
    [Fact]
    public void The_rate_taken_is_described_including_a_fallback()
    {
        PartyRate.Describe(Part(1000m, 800m), isDealer: true).Should().Be("Dealer rate");
        PartyRate.Describe(Part(1000m, 0m), isDealer: true).Should().StartWith("Customer rate");
        PartyRate.Describe(Part(1000m, 800m), isDealer: false).Should().Be("Customer rate");
    }
}
