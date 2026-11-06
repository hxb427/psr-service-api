using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using PSR.Service.Api.Data;
using PSR.Service.Api.Data.Entities;
using Xunit;

namespace PSR.Service.Tests;

/// <summary>/stock-acks/pending builds one row from four tables, three of them left-joined, and two
/// of those joins hang off a nullable key — <c>StockMovement.ReferenceId</c> to the request, then the
/// request's own nullable <c>IssuedByUserId</c> to the user who dispatched it. A left join chained off
/// an already-left-joined entity is the shape EF can decline to translate, and when it does it throws
/// while *compiling* the query: the ack screen returns 500 with no bad row involved and nothing in the
/// data to point at. Technicians would see an empty incoming-stock list and no reason for it.
///
/// Same approach as <see cref="AvailableSerialsQueryTests"/>: ToQueryString compiles and renders the
/// SQL, so a translation failure lands here instead of on a phone.</summary>
public class PendingAcksQueryTests
{
    private static AppDbContext NewContext()
    {
        // Never opened. Pomelo only needs a declared server version to build its SQL generator,
        // which is all query compilation touches.
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseMySql("Server=localhost;Database=none;Uid=none;Pwd=none;",
                      ServerVersion.Parse("8.0.36-mysql"))
            .Options;
        return new AppDbContext(options);
    }

    /// <summary>Mirrors the query in StockAcksEndpoints.PendingAsync. Kept in step by hand — it is
    /// here to prove the SHAPE translates, so it has to stay the same shape.</summary>
    private static IQueryable<object> PendingQuery(AppDbContext db, long uid) =>
        from m in db.StockMovements.AsNoTracking()
        where m.TechnicianId == uid && m.MovementType == MovementType.Issue
        join a in db.StockIssueAcks on m.Id equals a.StockMovementId into ag
        from a in ag.DefaultIfEmpty()
        where a == null
        join p in db.Parts on m.PartId equals p.Id
        join r in db.StockRequests on m.ReferenceId equals (long?)r.Id into rg
        from r in rg.DefaultIfEmpty()
        join iu in db.Users on r.IssuedByUserId equals (long?)iu.Id into iug
        from iu in iug.DefaultIfEmpty()
        orderby m.Id descending
        select new
        {
            m.Id, m.PartId, p.ItemCode, p.Name, m.Quantity, m.CreatedAt,
            RequestNo = r != null ? r.RequestNo : null,
            RequestedAt = r != null ? (DateTime?)r.RequestDate : null,
            Courier = r != null ? r.Courier : null,
            TrackingNo = r != null ? r.TrackingNo : null,
            DcNo = r != null ? r.DcNo : null,
            IssuedByName = iu != null ? (iu.FullName ?? iu.Username) : null,
        };

    [Fact]
    public void Pending_acks_query_translates_to_sql()
    {
        using var db = NewContext();
        var sql = PendingQuery(db, 42).ToQueryString();

        sql.Should().Contain("stock_movements");
    }

    [Fact]
    public void Every_joined_table_reaches_the_sql()
    {
        // Named individually so a join silently dropped during a refactor fails here rather than
        // quietly returning rows with null provenance, which reads on the phone as "no courier info"
        // and is indistinguishable from a direct issue.
        using var db = NewContext();
        var sql = PendingQuery(db, 42).ToQueryString();

        sql.Should().Contain("stock_requests");
        sql.Should().Contain("users");
        sql.Should().Contain("parts");
        sql.Should().Contain("stock_issue_acks");
    }

    [Fact]
    public void Provenance_columns_are_all_selected()
    {
        using var db = NewContext();
        var sql = PendingQuery(db, 42).ToQueryString();

        // The four that were added for the acknowledge card. A left join that translates but drops
        // a column still leaves the technician without the dispatch details.
        sql.Should().Contain("request_date");
        sql.Should().Contain("dc_no");
        sql.Should().Contain("courier");
        sql.Should().Contain("tracking_no");
    }

    [Fact]
    public void The_issue_is_dated_from_the_movement_not_the_request()
    {
        // A partially-issued request rewrites its own IssuedDate on every further dispatch, so the
        // only per-consignment date is the movement's. If this ever switches to the request column,
        // a technician acknowledging the first of three part-shipments is shown the date of the last.
        using var db = NewContext();
        var sql = PendingQuery(db, 42).ToQueryString();

        sql.Should().NotContain("issued_date");
    }
}
