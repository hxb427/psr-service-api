using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using PSR.Service.Api.Audit;
using PSR.Service.Api.Common;
using PSR.Service.Api.Data;
using PSR.Service.Api.Stock;
using Xunit;

namespace PSR.Service.Tests;

/// <summary>Logging a call must get as far as the database.
///
/// <see cref="NumberSequenceService"/> will not hand out a document number unless the caller has
/// already opened a transaction — the row lock it relies on for uniqueness lasts no longer than one,
/// so it refuses rather than return a number it cannot promise is unique. The refusal is an
/// <see cref="InvalidOperationException"/>, nothing catches it, and the engineer gets a 500 on every
/// single save.
///
/// Nothing already in this suite could see that. The rule tests stop at the parsed request, the
/// query tests translate SQL, and the routing tests only prove the handler can be bound — binding a
/// handler does not run it. So the one line that was missing was also the one line no test executed,
/// and the register shipped unable to record a call.</summary>
public class FieldCallCreateTransactionTests
{
    public FieldCallCreateTransactionTests() => ShopClock.Configure(5.5);

    private static ClaimsPrincipal Engineer(long userId = 42) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "test"));

    [Fact]
    public async Task Logging_a_call_is_not_refused_its_number_for_want_of_a_transaction()
    {
        // Points at no reachable server, which is the point: the handler is expected to fail here.
        // What matters is where. A valid request must be turned away by the database being absent,
        // never by the sequence declining to number it.
        using var db = new DesignTimeDbContextFactory().CreateDbContext([]);

        var act = async () => await FieldCallsEndpoints.CreateAsync(
            new CreateFieldCallRequest("ServiceCall", "A customer who rang in"),
            Engineer(), db, new NumberSequenceService(db), new AuditService(db),
            new DefaultHttpContext(), CancellationToken.None);

        var thrown = await act.Should().ThrowAsync<Exception>(
            "there is no database behind this context, so the save cannot succeed");

        thrown.And.Message.Should().NotContain(
            "outside a transaction",
            "the handler must open one before drawing the call number, or every POST is a 500");
    }
}
