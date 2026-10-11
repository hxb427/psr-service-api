using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using PSR.Service.Api.Data;
using PSR.Service.Api.Reference;
using Xunit;

namespace PSR.Service.Tests;

/// <summary>The complaint/cause suggestion ranking groups in SQL, so an untranslatable GroupBy would
/// only blow up when a technician opened the field form against the real database. ToQueryString forces
/// the provider to translate without a live server, which is exactly the failure worth catching early —
/// the same reason <see cref="TopUsedQueryTests"/> exists for the part ranking.</summary>
public class FieldPhraseQueryTests
{
    private static AppDbContext NewContext() => new DesignTimeDbContextFactory().CreateDbContext([]);

    [Fact]
    public void The_complaint_ranking_translates_to_sql()
    {
        using var db = NewContext();

        var sql = FieldPhrasesEndpoints.TopComplaintsQuery(db, technicianId: 42).Take(10).ToQueryString();

        sql.Should().Contain("GROUP BY");
        sql.Should().Contain("COUNT(");
        // The ranking has to be applied by the database, not after Take has truncated an arbitrary ten.
        sql.Should().Contain("ORDER BY");
        sql.Should().Contain("DESC");
        sql.Should().Contain("LIMIT");
    }

    [Fact]
    public void The_cause_ranking_translates_to_sql()
    {
        using var db = NewContext();

        var sql = FieldPhrasesEndpoints.TopCausesQuery(db, technicianId: 42).Take(10).ToQueryString();

        sql.Should().Contain("GROUP BY");
        sql.Should().Contain("COUNT(");
        sql.Should().Contain("ORDER BY");
        sql.Should().Contain("LIMIT");
    }

    /// <summary>Each query groups on its OWN column. They are near-identical and sit next to each other,
    /// which is how a copy-paste ends up ranking complaints twice and never ranking causes at all —
    /// silently, because both halves of the response would still come back full.</summary>
    [Fact]
    public void Each_ranking_groups_on_its_own_column()
    {
        using var db = NewContext();

        FieldPhrasesEndpoints.TopComplaintsQuery(db, 42).ToQueryString()
            .Should().Contain("complaint").And.NotContain("cause");
        FieldPhrasesEndpoints.TopCausesQuery(db, 42).ToQueryString()
            .Should().Contain("cause").And.NotContain("complaint");
    }

    /// <summary>A technician's phrasing is their own habit. Offering one technician the sentences
    /// another writes would spread a diagnosis nobody made, so the scoping is not optional.</summary>
    [Fact]
    public void Both_rankings_are_scoped_to_the_caller()
    {
        using var db = NewContext();

        foreach (var sql in new[]
                 {
                     FieldPhrasesEndpoints.TopComplaintsQuery(db, 42).ToQueryString(),
                     FieldPhrasesEndpoints.TopCausesQuery(db, 42).ToQueryString(),
                 })
            sql.Should().Contain("technician_id");
    }

    /// <summary>An empty complaint is the absence of a complaint, not a popular way of describing one —
    /// without this the top suggestion on a new technician's form is a blank line.</summary>
    [Fact]
    public void Blank_text_is_excluded_from_both_rankings()
    {
        using var db = NewContext();

        foreach (var sql in new[]
                 {
                     FieldPhrasesEndpoints.TopComplaintsQuery(db, 42).ToQueryString(),
                     FieldPhrasesEndpoints.TopCausesQuery(db, 42).ToQueryString(),
                 })
            sql.Should().Contain("IS NOT NULL");
    }
}
