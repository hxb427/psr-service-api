using PSR.Service.Api.Data.Entities;

namespace PSR.Service.Api.Services;

/// <summary>Which column of the price list a part on a service job is billed at: the dealer rate on a
/// dealer's job, the customer rate on a direct customer's.
///
/// One rule for every part-priced line a job carries — a component the technician fitted, the
/// replacement unit handed over on a total loss or a swap, and a component added on the generate form —
/// because they all end up on the same bill. Until this existed every one of them was priced at the
/// customer rate, so a dealer was billed the retail price for parts the old app had always charged them
/// trade for.
///
/// Whose job it is comes off <see cref="ServiceJob.DealerId"/>, the same test billing uses to decide whose
/// name and GSTIN go on the document (BillingService.ResolvePartyAsync): a job with a dealer on it is the
/// dealer's.
///
/// A column left at zero falls back to the other one, as the old app's PI page did. A part nobody has
/// given a dealer rate is billed to a dealer at the customer rate rather than at nothing — a zero is a
/// gap in the price list, not a decision to give the part away.</summary>
public static class PartyRate
{
    /// <summary>Whether the job is billed as a dealer's.</summary>
    public static bool IsDealer(ServiceJob job) => job.DealerId is not null;

    /// <summary>The tax-exclusive unit rate to bill this part at for this kind of party.</summary>
    public static decimal For(Part part, bool isDealer) => isDealer
        ? (part.DealerRate > 0 ? part.DealerRate : part.CustomerRate)
        : (part.CustomerRate > 0 ? part.CustomerRate : part.DealerRate);

    /// <inheritdoc cref="For(Part, bool)"/>
    public static decimal For(Part part, ServiceJob job) => For(part, IsDealer(job));

    /// <summary>Which column <see cref="For(Part, bool)"/> took, in words, for the generate form to show
    /// beside the figure — including when it had to fall back, which is otherwise invisible.</summary>
    public static string Describe(Part part, bool isDealer) => isDealer
        ? (part.DealerRate > 0 ? "Dealer rate" : "Customer rate (no dealer rate set)")
        : (part.CustomerRate > 0 ? "Customer rate" : "Dealer rate (no customer rate set)");
}
