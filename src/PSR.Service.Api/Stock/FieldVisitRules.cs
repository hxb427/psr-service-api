using PSR.Service.Api.Common;
using PSR.Service.Api.Data.Entities;

namespace PSR.Service.Api.Stock;

/// <summary>What a field visit's money and context are allowed to say, and what they add up to.
///
/// Separated from the endpoint for the same reason <see cref="FieldLineRules"/> is: the arithmetic a
/// customer is handed on a receipt should be one readable thing that tests can pin, not something that
/// emerges from the order of a few statements wrapped around a transaction.
///
/// Every figure here is computed from the server's own copy of the catalogue. Nothing a client sends
/// is treated as a price, a tax or a total — the request names a part, a quantity and which rate
/// column to use, and that is the whole of its say in what the visit costs.</summary>
internal static class FieldVisitRules
{
    /// <summary>The tax-exclusive unit rate for a part at the visit's rate type.
    ///
    /// A rate of zero is honoured rather than refused, which is the opposite of what a counter sale
    /// does (<c>SpareSaleService.ResolveRate</c> throws on an unpriced part). The two are different
    /// situations: a counter sale is a commercial act that can wait for someone to price the item,
    /// while a field service is a record of work that has already happened. Refusing the save would
    /// cost the shop the whole service record — the stock consumption, the serial custody, the
    /// customer history — over a missing number in the parts master. The visit is recorded at zero and
    /// the gap shows up on the receipt, where somebody will notice it.</summary>
    public static decimal UnitRate(Part part, FieldRateType rateType) =>
        rateType == FieldRateType.Dealer ? part.DealerRate : part.CustomerRate;

    /// <summary>Tax on a taxable amount, to the paise. Rounded per line and per charge, never once at
    /// the end, so the line figures on a receipt reproduce its total instead of missing it by a paisa.</summary>
    public static decimal Tax(decimal taxable, decimal percent) =>
        Math.Round(taxable * percent / 100m, 2);

    /// <summary>What a visit adds up to.
    ///
    /// <paramref name="warranty"/> decides only the last figure. An in-warranty repair still has a
    /// value — the parts left the shop's stock and somebody's day went into fitting them — so taxable,
    /// tax and total record it in full, and <see cref="VisitTotals.Payable"/> alone goes to nil. That
    /// is the figure the technician collects and the receipt prints, and keeping it apart from the
    /// total is what stops a covered repair reading as a repair that cost nothing to carry out.</summary>
    public static VisitTotals Totals(
        IEnumerable<(decimal Amount, decimal TaxAmount)> lines,
        decimal chargeAmount, decimal chargeTaxPercent,
        FieldWarrantyStatus? warranty)
    {
        var taxable = chargeAmount;
        var tax = Tax(chargeAmount, chargeTaxPercent);
        foreach (var l in lines)
        {
            taxable += l.Amount;
            tax += l.TaxAmount;
        }

        // Up to the next whole rupee, the rule every other payable figure in this system uses: nobody
        // at a customer's gate is handing back paise.
        var total = BillMoney.RoundUp(taxable + tax);
        var payable = warranty == FieldWarrantyStatus.In ? 0m : total;
        return new VisitTotals(taxable, tax, total, payable);
    }

    // ---------------------------------------------------------------- what the request may say

    /// <summary>Parses the rate type, or says why it cannot. Absent means Customer: that is what a
    /// walk-in is billed at and what every record written before this field existed was priced at.</summary>
    public static (FieldRateType value, string? error) ParseRateType(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return (FieldRateType.Customer, null);
        return Enum.TryParse<FieldRateType>(raw.Trim(), true, out var v)
            ? (v, null)
            : (FieldRateType.Customer, $"Unknown rate type '{raw}'. Use Customer or Dealer.");
    }

    /// <summary>Absent stays absent. A visit settled on account, or one that is covered and owes
    /// nothing, has no payment method to report, and defaulting it to Cash would put money in the
    /// record that nobody handed over.</summary>
    public static (FieldPaymentMethod? value, string? error) ParsePaymentMethod(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return (null, null);
        return Enum.TryParse<FieldPaymentMethod>(raw.Trim(), true, out var v)
            ? (v, null)
            : (null, $"Unknown payment method '{raw}'. Use Cash, Upi or BankTransfer.");
    }

    /// <summary>Absent stays absent, and is not the same as Unknown — see
    /// <see cref="FieldWarrantyStatus"/>.</summary>
    public static (FieldWarrantyStatus? value, string? error) ParseWarrantyStatus(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return (null, null);
        return Enum.TryParse<FieldWarrantyStatus>(raw.Trim(), true, out var v)
            ? (v, null)
            : (null, $"Unknown warranty status '{raw}'. Use In, Out or Unknown.");
    }

    /// <summary>Why a GPS fix cannot be accepted, or null when it can.
    ///
    /// Half a fix is refused. A latitude with no longitude is not a weaker claim about where the
    /// technician was, it is not a claim at all, and storing it would leave a record that looks
    /// located and cannot be put on a map. No fix at all is perfectly acceptable — the handset may
    /// have had no sky — so both being absent is fine.</summary>
    public static string? ValidateFix(decimal? latitude, decimal? longitude)
    {
        if (latitude is null && longitude is null) return null;
        if (latitude is null || longitude is null)
            return "A location fix needs both latitude and longitude.";
        if (latitude is < -90 or > 90) return "Latitude must be between -90 and 90.";
        if (longitude is < -180 or > 180) return "Longitude must be between -180 and 180.";
        return null;
    }
}

/// <summary>A visit's four figures. <see cref="Total"/> is what the work was worth;
/// <see cref="Payable"/> is what the customer owes for it.</summary>
internal readonly record struct VisitTotals(decimal Taxable, decimal Tax, decimal Total, decimal Payable);
