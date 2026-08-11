using System.Text;

namespace PSR.Service.Api.Reference;

/// <summary>One legacy name the dealer master doesn't have yet. Never auto-inserted — an admin
/// reviews these, because <c>passtestdata.Customer</c> is free text that mixes real dealers with
/// direct customers.</summary>
public record DealerImportCandidateDto(
    string Name,                // cleaned for display; original casing kept
    int? WarrantyMonths,        // from dealer_warranty; null when the name only appears in passtestdata
    string? Address,            // Address1 + Address2 off the newest passtestdata invoice
    string? Remarks,
    string Source,              // dealer_warranty | passtestdata | both
    int MachineCount,           // passtestdata rows carrying this name (0 = not seen there)
    string? PossibleMatch);     // existing dealer this nearly collides with — likely a duplicate

public record DealerImportCandidatesDto(
    List<DealerImportCandidateDto> Candidates,
    int ExistingDealers,
    int LegacyDealerRows,
    int PasstestNames,
    List<string> Warnings);

public record DealerImportItem(string Name, int WarrantyMonths, string? Address, string? Remarks);

public record DealerImportRequest(List<DealerImportItem> Dealers);

public record DealerImportResultDto(int Created, int Skipped, List<string> SkippedNames);

/// <summary>Collapses the spelling variants that 20 years of hand-typed dealer names produce, so the
/// same dealer isn't imported twice under two spellings. Matching only — the name stored is always
/// what the admin approved, never the key.</summary>
public static class DealerNameKey
{
    private static readonly string[] Prefixes = { "M/S.", "M/S", "M/", "MS.", "MESSRS." };

    /// <summary>Display form: collapse runs of whitespace, trim. Casing left alone.</summary>
    public static string Clean(string raw)
    {
        var sb = new StringBuilder(raw.Length);
        var space = false;
        foreach (var ch in raw.Trim())
        {
            if (char.IsWhiteSpace(ch)) { space = true; continue; }
            if (space && sb.Length > 0) sb.Append(' ');
            space = false;
            sb.Append(ch);
        }
        return sb.ToString();
    }

    /// <summary>Comparison key: upper-cased, M/S prefix dropped, &amp; spelled out, punctuation
    /// removed, whitespace collapsed. "M/s. Sri Ram &amp; Co." and "SRI RAM AND CO" both land on
    /// "SRI RAM AND CO".</summary>
    public static string Normalize(string raw)
    {
        var s = Clean(raw).ToUpperInvariant();

        foreach (var p in Prefixes)
            if (s.StartsWith(p, StringComparison.Ordinal)) { s = s[p.Length..].TrimStart(); break; }

        var sb = new StringBuilder(s.Length);
        var space = false;
        foreach (var ch in s)
        {
            if (ch == '&') { space = true; sb.Append(sb.Length > 0 ? " AND" : "AND"); continue; }
            if (char.IsLetterOrDigit(ch))
            {
                if (space && sb.Length > 0) sb.Append(' ');
                space = false;
                sb.Append(ch);
                continue;
            }
            space = true;   // punctuation and whitespace alike become one separator
        }
        return sb.ToString();
    }

    /// <summary>True when two keys are close enough that a human should look before creating a
    /// second dealer — one edit per ~8 characters, capped at 3.</summary>
    public static bool IsNearMatch(string a, string b)
    {
        if (a.Length < 5 || b.Length < 5) return false;          // short names collide by accident
        var budget = Math.Min(3, 1 + Math.Min(a.Length, b.Length) / 8);
        if (Math.Abs(a.Length - b.Length) > budget) return false; // cheap reject before the O(n*m) walk
        return Distance(a, b, budget) <= budget;
    }

    /// <summary>Levenshtein distance, abandoned once every cell in a row exceeds <paramref name="cap"/>.</summary>
    private static int Distance(string a, string b, int cap)
    {
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            var best = cur[0];
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
                if (cur[j] < best) best = cur[j];
            }
            if (best > cap) return cap + 1;
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }
}
