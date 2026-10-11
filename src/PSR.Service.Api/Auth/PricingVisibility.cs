using System.Security.Claims;
using PSR.Service.Api.Data.Entities;

namespace PSR.Service.Api.Auth;

/// <summary>How much of a figure a caller is allowed to see.</summary>
internal enum PricingScope
{
    /// <summary>No money at all. Every rate and total is serialized as null.</summary>
    None,

    /// <summary>What someone standing in front of a customer needs in order to quote and collect:
    /// both selling rates, the GST percentage and the HSN code. NOT the purchase rate.</summary>
    Quoting,

    /// <summary>Everything, purchase rate included.</summary>
    Full,
}

/// <summary>Who may see what a thing costs.
///
/// Until now this was one list — admin, manager, supervisor, viewer — repeated in three files, and a
/// technician was outside it, so the app showed a technician no rate at all. That was defensible while
/// the phone only recorded what was fitted. It stopped being defensible once the technician is the
/// person who has to tell the customer what the visit comes to and take the money for it: the
/// alternative to a rate on the screen is a phone call to the office, or a guess.
///
/// Two ways to lift it were open: add <c>technician</c> to the existing list, or split the list. This
/// splits it, because the rates are not one thing.
///
/// <list type="bullet">
/// <item><b>Customer rate, dealer rate, GST, HSN</b> are selling prices and tax. They are printed on
/// the customer's own receipt, so they are not a secret from anybody in the room. A technician billing
/// a dealer needs the dealer column for the same reason they need the customer column — the rate type
/// on the visit decides which one the line bills at, and withholding one of them would leave half the
/// choice unusable.</item>
/// <item><b>Purchase rate</b> is what the shop paid. It is the margin, it appears on no customer
/// document, and a phone in a customer's workshop is the last place it should be readable — over a
/// shoulder, on a shared handset, or on a device that walks off. It stays with the office.</item>
/// </list>
///
/// So a technician gets <see cref="PricingScope.Quoting"/> and nothing wider. The roles that were
/// already trusted with the whole catalogue keep <see cref="PricingScope.Full"/>, and the roles that
/// saw no pricing before — store_manager, accounts, inward — still see none: widening those is a
/// separate decision nobody has asked for.</summary>
internal static class PricingVisibility
{
    /// <summary>The office. Reads the purchase rate, so it reads the margin.</summary>
    private static readonly string[] FullRoles =
        { RoleNames.Admin, RoleNames.Manager, RoleNames.Supervisor, RoleNames.Viewer };

    public static PricingScope For(ClaimsPrincipal user) =>
        FullRoles.Any(user.IsInRole) ? PricingScope.Full
        : user.IsInRole(RoleNames.Technician) ? PricingScope.Quoting
        : PricingScope.None;

    /// <summary>Selling rates, GST and HSN — what a quote is built from.</summary>
    public static bool SellingRates(PricingScope scope) => scope >= PricingScope.Quoting;

    /// <summary>The purchase rate, and nothing else is gated on this.</summary>
    public static bool CostRates(PricingScope scope) => scope == PricingScope.Full;

    public static bool SellingRates(ClaimsPrincipal user) => SellingRates(For(user));
    public static bool CostRates(ClaimsPrincipal user) => CostRates(For(user));
}
