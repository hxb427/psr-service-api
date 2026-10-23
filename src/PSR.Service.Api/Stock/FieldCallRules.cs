using PSR.Service.Api.Common;
using PSR.Service.Api.Data.Entities;

namespace PSR.Service.Api.Stock;

/// <summary>What a call register entry is allowed to say.
///
/// Separated from the endpoint for the same reason <see cref="FieldVisitRules"/> is: the rules a
/// save is refused by should be one readable thing tests can pin, rather than something that emerges
/// from the order of a dozen guard clauses. There is no arithmetic here because a call has no money —
/// what is left is the enum spellings, the two dates, and the column widths.</summary>
internal static class FieldCallRules
{
    /// <summary>Parses one of our enum names, case-insensitively, listing the alternatives when it
    /// fails. The list is generated from the enum rather than written out, so adding a call type
    /// cannot leave behind a message that names four of the five.</summary>
    public static (T value, string? error) ParseRequired<T>(string? raw, string label) where T : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(raw)) return (default, $"Name the {label}.");
        if (Enum.TryParse<T>(raw.Trim(), ignoreCase: true, out var parsed)) return (parsed, null);
        return (default, $"'{raw.Trim()}' is not a {label}. Expected one of: {string.Join(", ", Enum.GetNames<T>())}.");
    }

    /// <summary>As above, but an omitted value takes <paramref name="fallback"/>.
    ///
    /// A value that WAS sent and is wrong is still refused. That asymmetry is the point: silently
    /// turning a misspelling into the default is how a register fills up with Local calls that were
    /// outstation, and nobody ever finds out.</summary>
    public static (T value, string? error) ParseOptional<T>(string? raw, T fallback, string label)
        where T : struct, Enum
        => string.IsNullOrWhiteSpace(raw) ? (fallback, null) : ParseRequired<T>(raw, label);

    /// <summary>The same parse for a query-string filter, where a value that will not parse is simply
    /// not a filter. A list someone is browsing reads better dropping back to the whole register than
    /// returning a 400 for a typo in a URL.</summary>
    public static T? ParseFilter<T>(string? raw) where T : struct, Enum
        => !string.IsNullOrWhiteSpace(raw) && Enum.TryParse<T>(raw.Trim(), ignoreCase: true, out var v)
            ? v : null;

    /// <summary>The two days a call carries, taken onto the shop's wall clock, or why they cannot be.
    ///
    /// Both go through <see cref="ShopClock.BusinessDate(DateTime)"/> before anything compares them,
    /// so a client that sent a zoned midnight is corrected here rather than booked a day early — and
    /// so the comparison below is between two shop days rather than two instants, which is the only
    /// way it gives the same answer at nine in the morning and at seven in the evening.
    ///
    /// An absent received date means today. An absent attended date means not yet attended, which is
    /// an ordinary state and not an error.
    ///
    /// The ordering is the one rule worth enforcing: a call cannot be attended before it came in, and
    /// a mis-keyed year is exactly what that catches. A received date in the future is deliberately
    /// NOT refused — a handset whose clock runs a few minutes ahead of the shop's would trip it at
    /// midnight, and refusing a real call over a clock skew is worse than storing a date somebody can
    /// see is wrong.</summary>
    public static (DateTime received, DateTime? attended, string? error) Dates(
        DateTime? receivedRaw, DateTime? attendedRaw)
    {
        var received = ShopClock.BusinessDate(receivedRaw) ?? ShopClock.Today;
        var attended = ShopClock.BusinessDate(attendedRaw);
        if (attended is { } a && a < received)
            return (received, attended, "The attended date cannot fall before the call was received.");
        return (received, attended, null);
    }

    /// <summary>Why the value will not fit its column, or null when it will.
    ///
    /// Checked because nothing else checks it: this API registers no validation filter, so the
    /// <c>[StringLength]</c> attributes on the request record document the limit without enforcing
    /// it. Left unchecked the string reaches MySQL, which either errors — a 500 for what is plainly a
    /// bad request — or truncates it quietly, and a remark cut off mid-sentence is worse than one
    /// refused. Measured on the trimmed value, because trimming is what gets stored.</summary>
    public static string? TooLong(string? value, int max, string label)
        => value is { } v && v.Trim().Length > max
            ? $"{label} cannot be longer than {max} characters."
            : null;

    /// <summary>Every length limit on a call, checked in the order the form shows them so the first
    /// complaint a user gets is about the first field they overfilled.
    ///
    /// The numbers are the column widths from <c>FieldCallConfiguration</c>, repeated here because
    /// there is no way to ask EF for them that is clearer than saying them.</summary>
    public static string? Lengths(FieldCall call) =>
        TooLong(call.CustomerName, 200, "Customer name")
        ?? TooLong(call.CustomerAddress, 500, "Customer address")
        ?? TooLong(call.CallReferenceNo, 100, "Call reference number")
        ?? TooLong(call.MachineModel, 100, "Machine model")
        ?? TooLong(call.MachineSerial, 100, "Machine/part serial")
        ?? TooLong(call.SoftwareVersion, 50, "Software version")
        ?? TooLong(call.Remarks, 1000, "Remarks");
}
