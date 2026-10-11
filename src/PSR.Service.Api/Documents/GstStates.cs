namespace PSR.Service.Api.Documents;

/// <summary>One state or union territory and the GST code that identifies it — the two digits every
/// GSTIN registered there opens with.</summary>
public sealed record GstState(string Code, string Name);

/// <summary>The GST state codes, and the single question billing asks of them: is the party in the
/// company's own state, or out of it?
///
/// A supply inside the state carries CGST + SGST and one across a state line carries IGST. That is the
/// statute, not a preference, so a document derives it from the party's state instead of being told.
/// The company's own state comes from <see cref="CompanyInfo.StateCode"/> (32, Kerala), so a shop that
/// moves states changes one config value rather than any code here.
///
/// The codes are fixed by notification and the list is short, so it lives here rather than in a table:
/// the client needs the same list to put a picker on the generate form, and a picker that cannot open
/// without a round trip is worse than one compiled in.</summary>
public static class GstStates
{
    /// <summary>Every code a document can be raised against today, in code order — what a picker offers.
    /// 97 (Other Territory) is what a supply outside any state is billed under, and it is inter-state
    /// like any other code that is not the company's own.</summary>
    public static readonly IReadOnlyList<GstState> All =
    [
        new("01", "Jammu and Kashmir"),
        new("02", "Himachal Pradesh"),
        new("03", "Punjab"),
        new("04", "Chandigarh"),
        new("05", "Uttarakhand"),
        new("06", "Haryana"),
        new("07", "Delhi"),
        new("08", "Rajasthan"),
        new("09", "Uttar Pradesh"),
        new("10", "Bihar"),
        new("11", "Sikkim"),
        new("12", "Arunachal Pradesh"),
        new("13", "Nagaland"),
        new("14", "Manipur"),
        new("15", "Mizoram"),
        new("16", "Tripura"),
        new("17", "Meghalaya"),
        new("18", "Assam"),
        new("19", "West Bengal"),
        new("20", "Jharkhand"),
        new("21", "Odisha"),
        new("22", "Chhattisgarh"),
        new("23", "Madhya Pradesh"),
        new("24", "Gujarat"),
        new("26", "Dadra and Nagar Haveli and Daman and Diu"),
        new("27", "Maharashtra"),
        new("29", "Karnataka"),
        new("30", "Goa"),
        new("31", "Lakshadweep"),
        new("32", "Kerala"),
        new("33", "Tamil Nadu"),
        new("34", "Puducherry"),
        new("35", "Andaman and Nicobar Islands"),
        new("36", "Telangana"),
        new("37", "Andhra Pradesh"),
        new("38", "Ladakh"),
        new("97", "Other Territory"),
    ];

    /// <summary>Codes that were retired but are still sitting in party masters typed years ago. They
    /// resolve to a name so an old record still prints one; they are not offered in a picker, because
    /// nobody should be choosing them now. Either way they are not 32, so the tax comes out the same.</summary>
    private static readonly IReadOnlyList<GstState> Retired =
    [
        new("25", "Daman and Diu"),          // merged into 26 in 2020
        new("28", "Andhra Pradesh"),         // pre-bifurcation; registrations there are 37 now
    ];

    /// <summary>Names that mean a listed state without being spelled like one. A party master is years of
    /// typing, and "Tamilnadu" should not read as a state nobody has heard of.</summary>
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["tamilnadu"] = "33",
        ["orissa"] = "21",
        ["pondicherry"] = "34",
        ["uttaranchal"] = "05",
        ["new delhi"] = "07",
        ["nct of delhi"] = "07",
        ["dadra and nagar haveli"] = "26",
        ["daman and diu"] = "26",
        ["andaman and nicobar"] = "35",
        ["jammu & kashmir"] = "01",
    };

    /// <summary>A code in the form everything else here compares: two digits, leading zero kept. It takes
    /// the leading digits off whatever it is given, so "7", "07 ", "32 - Kerala" and a GSTIN pasted into
    /// the code box all come back as the code they start with. Null when there are no leading digits.</summary>
    public static string? NormalizeCode(string? code)
    {
        var text = code?.Trim();
        if (string.IsNullOrEmpty(text)) return null;

        var digits = new string(text.TakeWhile(char.IsAsciiDigit).ToArray());
        return digits.Length switch
        {
            0 => null,
            1 => "0" + digits,
            2 => digits,
            _ => digits[..2],
        };
    }

    /// <summary>The state a GSTIN belongs to: a GSTIN opens with the code of the state that issued it.</summary>
    public static string? CodeFromGstin(string? gstin)
    {
        var text = gstin?.Trim();
        return text is { Length: >= 2 } && char.IsAsciiDigit(text[0]) && char.IsAsciiDigit(text[1])
            ? text[..2]
            : null;
    }

    public static GstState? ByCode(string? code)
        => NormalizeCode(code) is { } c
            ? All.FirstOrDefault(s => s.Code == c) ?? Retired.FirstOrDefault(s => s.Code == c)
            : null;

    /// <summary>A state looked up by the name somebody typed, allowing for the usual spellings.</summary>
    public static GstState? ByName(string? name)
    {
        var text = name?.Trim();
        if (string.IsNullOrEmpty(text)) return null;
        if (Aliases.TryGetValue(text, out var aliased)) return ByCode(aliased);
        return All.FirstOrDefault(s => s.Name.Equals(text, StringComparison.OrdinalIgnoreCase))
            ?? Retired.FirstOrDefault(s => s.Name.Equals(text, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The code to bill a party against: the code it was given, else the one its GSTIN opens
    /// with, else the one its state name implies. Null when the party carries none of the three.
    ///
    /// The GSTIN comes before the name deliberately. It is the party's own registration and cannot have
    /// been issued in a state other than the one it names, whereas a name is whatever fitted in the box.</summary>
    public static string? ResolveCode(string? code, string? name, string? gstin)
        => NormalizeCode(code) ?? CodeFromGstin(gstin) ?? ByName(name)?.Code;

    /// <summary>The named state to bill a party against, for printing beside the code. Null for a code
    /// that matches no state on the list — which still bills, it just prints without a name.</summary>
    public static GstState? Resolve(string? code, string? name, string? gstin)
        => ByCode(ResolveCode(code, name, gstin));

    /// <summary>Whether a document for this party bills IGST (true) or CGST + SGST (false).
    ///
    /// A party with no state recorded anywhere is billed as intra-state: the only parties that reach the
    /// counter without one are walk-ins standing in it, and the alternative is taxing a local sale as
    /// though the goods crossed a border. A company with no state code of its own is the opposite case —
    /// nothing can be said to share a state with it, so everything is inter-state, which is also how
    /// every document behaved while the split was switched off.</summary>
    public static bool IsInterState(string? partyStateCode, string? partyState, string? partyGstin,
        string? companyStateCode)
    {
        var home = NormalizeCode(companyStateCode);
        if (home is null) return true;

        var party = ResolveCode(partyStateCode, partyState, partyGstin);
        return party is not null && party != home;
    }
}
