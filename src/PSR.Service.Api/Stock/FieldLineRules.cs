using PSR.Service.Api.Data.Entities;

namespace PSR.Service.Api.Stock;

/// <summary>What a field-service part line has to say about itself before anything is written.
///
/// Pulled out of the endpoint so the rule is one readable thing rather than a shape that emerges
/// from the order of a few ifs around database calls — and so it can be pinned by tests without
/// standing a transaction up. The side effects (consuming stock, moving serials) stay in the
/// handler; this only decides whether the line is sayable.</summary>
internal static class FieldLineRules
{
    /// <summary>The reason this line cannot be accepted, or null when it can.</summary>
    public static string? Validate(FieldLineKind kind, Part part, int qty, string? serialNo)
    {
        var named = !string.IsNullOrWhiteSpace(serialNo);

        if (kind == FieldLineKind.Used)
        {
            // A fitted unit is leaving the technician's holding and going onto a customer's
            // machine. Which unit matters to everyone downstream — the warranty it carries, the
            // job it comes back on — and the technician is holding it while they type, so the
            // number is there to be read. Required, and one unit per line so the serial and the
            // quantity cannot disagree.
            if (!part.IsSerialTracked) return null;
            if (!named) return $"{part.ItemCode} is serial-tracked — name the fitted serial.";
            if (qty != 1) return $"{part.ItemCode} is serial-tracked — one line per unit (qty 1).";
            return null;
        }

        // Collected: the faulty unit taken off the customer's machine. The serial is OPTIONAL,
        // tracked part or not, and that is a deliberate loosening of what this used to demand.
        //
        // Requiring it assumed the number can always be read. On site it often cannot: the label
        // is burnt, painted over, behind a bracket, or the unit came back in pieces. Refusing the
        // whole service entry over it left the technician two choices, and both were worse than
        // accepting the gap — abandon the record, or invent a number. An invented serial is the
        // expensive one: it attaches one customer's history to a different unit permanently, and
        // nothing downstream can tell that it is wrong.
        //
        // Nothing is corrupted by the gap. An unnamed unit simply never enters serial tracking,
        // so it cannot later be picked onto a Faulty return shipment — those pick from tracked
        // units — and the line still records that something was taken from the customer.
        return null;
    }

    /// <summary>Whether a collected line should drive the serial ledger: only when the part is
    /// followed unit by unit AND the technician could actually read the number off it.</summary>
    public static bool CollectedEntersSerialTracking(Part part, string? serialNo) =>
        part.IsSerialTracked && !string.IsNullOrWhiteSpace(serialNo);
}
