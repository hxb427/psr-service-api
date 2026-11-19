namespace PSR.Service.Api.Data.Entities;

/// <summary>One line on a service job (replaces the encoded COMPONENTS string).</summary>
public class ServiceLine
{
    public long Id { get; set; }
    public long ServiceId { get; set; }
    public ServiceLineType LineType { get; set; }
    public long? PartId { get; set; }            // Component / Replacement
    public long? ServiceChargeId { get; set; }   // ServiceCharge
    public string? Description { get; set; }
    public int Qty { get; set; } = 1;
    public decimal UnitPrice { get; set; }
    public decimal Amount { get; set; }
    public string? ReplacementSerialNo { get; set; }

    /// <summary>The PI or invoice this line was added on, when somebody added it on the generate form
    /// rather than the technician adding it on the bench. Null on every other line.
    ///
    /// A billing line is a charge, never a movement: nothing was taken out of anybody's stock for it, so
    /// it is never consumed (it only ever lands on a job that is already past completion, and a job on a
    /// document cannot be reverted to reach completion again) and it is kept out of the technician's
    /// "top used" ranking, which is about what they reach for on the bench. It is always billed, whatever
    /// the job's <see cref="ServiceJob.IncludeComponentPrices"/> says — somebody chose to add it.</summary>
    public string? AddedOnDocNo { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ServiceJob Service { get; set; } = null!;
}
