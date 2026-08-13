using System.Globalization;

namespace PSR.Service.Api.Audit;

/// <summary>Collects "field old → new" lines while a handler applies an update, so the audit row says
/// WHAT changed rather than only that something did. "part.update id=42" cannot answer "who doubled
/// the customer rate"; "customer rate 100 → 200" can.
///
/// The handler still owns the assignment — <see cref="Set{T}"/> is given the value the handler already
/// decided on and applies it only when it differs, so adding a diff to an existing endpoint cannot
/// change what gets stored.
///
/// Blank and null are the same value here. A form that posts "" for an empty box would otherwise
/// report a change every time it was saved against a column that holds NULL.</summary>
public sealed class AuditDiff
{
    private readonly List<string> _lines = new();

    public bool HasChanges => _lines.Count > 0;

    /// <summary>Applies <paramref name="next"/> when it differs, recording the move. Strings are
    /// trimmed and blank-normalised before comparing; the trimmed value is what gets applied.</summary>
    public void Set(string label, string? current, string? next, Action<string?> apply)
    {
        var a = Normalise(current);
        var b = Normalise(next);
        if (string.Equals(a, b, StringComparison.Ordinal)) return;
        _lines.Add($"{label} {Show(a)} → {Show(b)}");
        apply(b);
    }

    /// <summary>The value-type overload. Constrained to <c>struct</c> rather than <c>IEquatable&lt;T&gt;</c>
    /// so enums bind here too — enums are structs but do not implement the interface, and without this
    /// they would silently fall through to the string overload and fail to compile.</summary>
    public void Set<T>(string label, T current, T next, Action<T> apply) where T : struct
    {
        if (EqualityComparer<T>.Default.Equals(current, next)) return;
        _lines.Add($"{label} {Show(current)} → {Show(next)}");
        apply(next);
    }

    /// <summary>Records a change the caller applied itself — one that spans more than a single field,
    /// or that needs a lookup to describe (a role list, a repointed foreign key).</summary>
    public void Note(string line) => _lines.Add(line);

    /// <summary>The change list, or null when nothing moved. Null is deliberate: it is what
    /// <c>IAuditService.Log</c> takes for "no details", so a caller can pass it straight through.</summary>
    public string? Summary => _lines.Count == 0 ? null : string.Join("; ", _lines);

    /// <summary>Summary behind an identifier, e.g. "PS1234: customer rate 100 → 200". Falls back to
    /// the identifier alone when nothing changed, so the row still says which record was saved.</summary>
    public string Describe(string subject) => _lines.Count == 0 ? subject : $"{subject}: {Summary}";

    private static string? Normalise(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();

    private static string Show(object? v) => v switch
    {
        null => "(empty)",
        string s => s.Length == 0 ? "(empty)" : $"'{s}'",
        bool b => b ? "yes" : "no",
        decimal d => d.ToString("0.##", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => v.ToString() ?? "(empty)",
    };
}
