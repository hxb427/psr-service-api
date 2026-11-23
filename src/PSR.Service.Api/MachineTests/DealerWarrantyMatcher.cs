using Microsoft.EntityFrameworkCore;
using PSR.Service.Api.Data;
using PSR.Service.Api.Reference;

namespace PSR.Service.Api.MachineTests;

/// <summary>How a factory record's customer was tied to a dealer, which decides where the warranty
/// term comes from.</summary>
public static class DealerMatchKind
{
    /// <summary>Exactly one dealer carries the customer's name (after normalisation).</summary>
    public const string Exact = "exact";

    /// <summary>The operator chose the dealer — it overrides any match.</summary>
    public const string Selected = "selected";

    /// <summary>More than one dealer carries the name. The candidates are those dealers.</summary>
    public const string Multiple = "multiple";

    /// <summary>No dealer carries the name. The candidates, if any, are near spellings only.</summary>
    public const string None = "none";
}

public record DealerMatch(string Kind, DealerCandidateDto? Dealer, List<DealerCandidateDto> Candidates)
{
    /// <summary>The dealer's term, or null when it is unset. 0 is "not known" throughout, as on
    /// <see cref="Data.Entities.Dealer.WarrantyMonths"/>.</summary>
    public int? Months => Dealer?.WarrantyMonths is > 0 and var m ? m : null;
}

/// <summary>Ties passtestdata.Customer to the dealer master, so a machine's warranty term is its
/// dealer's term rather than the hand-typed Warranty column on the factory record.
///
/// The dealer master was imported from passtestdata.Customer through <see cref="DealerNameKey"/>, so
/// the same key links them back: "M/s. Sri Ram &amp; Co." on a record finds the dealer "SRI RAM AND CO".
/// Only an exact key match is ever applied. A near spelling is offered as a candidate for the operator
/// to confirm, never taken on its own — the term it would bring decides whether a repair is billed.
///
/// Inactive dealers are matched too: a dealer that has since been switched off still sold the machine.
/// One instance per request: the dealer table is small, and loading it fresh means an edit to a
/// dealer's term shows on the very next lookup.</summary>
public sealed class DealerWarrantyMatcher
{
    private sealed record Entry(DealerCandidateDto Dealer, string Key);

    private const int MaxCandidates = 10;

    private readonly List<Entry> _entries;
    private readonly DealerCandidateDto? _selected;
    private readonly Dictionary<string, DealerMatch> _memo = new(StringComparer.Ordinal);

    /// <summary>No dealers at all: every record reads as unmatched.</summary>
    public static readonly DealerWarrantyMatcher Empty = new([], null);

    public DealerWarrantyMatcher(IEnumerable<DealerCandidateDto> dealers, long? selectedDealerId)
    {
        _entries = dealers.Select(d => new Entry(d, DealerNameKey.Normalize(d.Name))).ToList();
        _selected = selectedDealerId is { } id ? _entries.FirstOrDefault(e => e.Dealer.Id == id)?.Dealer : null;
    }

    /// <summary><paramref name="selectedDealerId"/> is the operator's own pick; an id that no longer
    /// exists is ignored and the record is matched as if none had been passed.</summary>
    public static async Task<DealerWarrantyMatcher> LoadAsync(
        AppDbContext db, long? selectedDealerId, CancellationToken ct)
    {
        var dealers = await db.Dealers.AsNoTracking()
            .Select(d => new DealerCandidateDto(d.Id, d.Name, d.WarrantyMonths, d.IsActive))
            .ToListAsync(ct);
        return new DealerWarrantyMatcher(dealers, selectedDealerId);
    }

    public DealerMatch Resolve(string? customer)
    {
        if (_selected is not null) return new DealerMatch(DealerMatchKind.Selected, _selected, []);

        var key = string.IsNullOrWhiteSpace(customer) ? "" : DealerNameKey.Normalize(customer);
        if (key.Length == 0) return new DealerMatch(DealerMatchKind.None, null, []);

        if (_memo.TryGetValue(key, out var hit)) return hit;
        return _memo[key] = Match(key);
    }

    private DealerMatch Match(string key)
    {
        var exact = _entries.Where(e => e.Key == key).Select(e => e.Dealer).ToList();
        if (exact.Count == 1) return new DealerMatch(DealerMatchKind.Exact, exact[0], []);

        if (exact.Count > 1)
        {
            // Two dealers on one key is a duplicate somebody has since switched off; the live one is the
            // dealer. Only when that still leaves a choice is it put to the operator.
            var active = exact.Where(d => d.IsActive).ToList();
            if (active.Count == 1) return new DealerMatch(DealerMatchKind.Exact, active[0], []);
            return new DealerMatch(DealerMatchKind.Multiple, null, Ordered(exact).ToList());
        }

        var near = _entries
            .Where(e => DealerNameKey.IsNearMatch(key, e.Key) || ContainsWholeName(key, e.Key))
            .Select(e => e.Dealer);
        return new DealerMatch(DealerMatchKind.None, null, Ordered(near).Take(MaxCandidates).ToList());
    }

    private static IEnumerable<DealerCandidateDto> Ordered(IEnumerable<DealerCandidateDto> dealers) =>
        dealers.OrderByDescending(d => d.IsActive).ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase);

    /// <summary>One name is the other with words added — "SRI RAM AND CO" inside "SRI RAM AND CO
    /// PALAKKAD". Whole words only, and never on a short name, which would turn up everywhere.</summary>
    private static bool ContainsWholeName(string a, string b)
    {
        var (shorter, longer) = a.Length <= b.Length ? (a, b) : (b, a);
        if (shorter.Length < 6 || shorter.Length == longer.Length) return false;
        return $" {longer} ".Contains($" {shorter} ", StringComparison.Ordinal);
    }
}
