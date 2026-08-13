using System.Reflection;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using PSR.Service.Api.Data;
using PSR.Service.Api.Data.Entities;
using Xunit;

namespace PSR.Service.Tests;

/// <summary>
/// Guards the wiring behind every generated identifier — service numbers, stock request and return
/// numbers, transfer, field service/sale, spare sale and its returns, PI / Invoice / DC.
///
/// Uniqueness itself is enforced at two levels that these tests cannot reach: NumberSequenceService
/// takes a `SELECT ... FOR UPDATE` row lock inside the caller's transaction, so concurrent callers
/// serialise, and each destination column carries a unique index as a backstop. What tests CAN catch
/// cheaply is the failure that actually happened: a key used by NextAsync with no seeded row behind
/// it. That does not fail at build time — it fails in front of a user, as "Number sequence 'X' is
/// not configured", the first time somebody tries the feature.
/// </summary>
public class NumberSequenceSeedTests
{
    /// <summary>The model, built without touching a database (no AutoDetect, so no connection).</summary>
    private static AppDbContext BuildContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseMySql(
                "Server=localhost;Database=psr_service_model_only;User=none;Password=none",
                new MySqlServerVersion(new Version(8, 0, 32)))
            .Options;
        return new AppDbContext(options);
    }

    private static IReadOnlyList<string> DeclaredKeys() =>
        typeof(SequenceKeys)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(f => f is { IsLiteral: true, IsInitOnly: false } && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();

    private static IReadOnlyList<IDictionary<string, object?>> SeededRows()
    {
        using var db = BuildContext();
        // Seed data is stripped from the runtime model — it only matters when building migrations,
        // so it lives in the design-time model, which is the one the snapshot is generated from.
        var model = db.GetService<IDesignTimeModel>().Model;
        return model.FindEntityType(typeof(NumberSequence))!.GetSeedData().ToList();
    }

    [Fact]
    public void Every_sequence_key_has_a_seeded_row()
    {
        var seeded = SeededRows()
            .Select(r => (string)r[nameof(NumberSequence.Key)]!)
            .ToList();

        // Missing here = the next generated migration emits a DeleteData for the row, because the
        // model snapshot is built from this seed list and a key absent from it looks like a row that
        // should not exist. That is exactly how SPARE_SALE_RETURN went missing.
        DeclaredKeys().Should().BeSubsetOf(seeded,
            "every key NextAsync can be called with must be seeded, or the feature 500s on first use");
    }

    [Fact]
    public void Seeded_rows_have_no_extra_keys()
    {
        var seeded = SeededRows().Select(r => (string)r[nameof(NumberSequence.Key)]!).ToList();
        seeded.Should().BeSubsetOf(DeclaredKeys(), "a seeded row nothing can request is dead data");
    }

    [Fact]
    public void Prefixes_are_distinct_and_short()
    {
        var rows = SeededRows();
        var prefixes = rows.Select(r => (string)r[nameof(NumberSequence.Prefix)]!).ToList();

        // Two sequences sharing a prefix would produce colliding-looking numbers across registers
        // (SVC00007 meaning two different things), which is the confusion these prefixes exist to
        // prevent — the unique index is per-table and would not catch it.
        prefixes.Should().OnlyHaveUniqueItems();

        // Numbers get read aloud and written on job cards. PREFIX + 5 digits = 8 characters;
        // year-scoped ones are PREFIX-YYYY-NNNN. Keep the prefix from inflating either.
        prefixes.Should().OnlyContain(p => p.Length >= 2 && p.Length <= 3);
    }

    [Fact]
    public void Counters_start_at_one()
    {
        SeededRows().Should().OnlyContain(r => (long)r[nameof(NumberSequence.NextValue)]! == 1L);
    }
}
