namespace PSR.Service.Api.Data.Entities;

/// <summary>An Indian state or union territory. Reference data, seeded by migration and not editable
/// through the API: the list of states is not the shop's to maintain.
///
/// Field records used to carry one free-text Place, which is fine to read and useless to group by —
/// "Thrissur", "trichur" and "THRISSUR, Kerala" are three districts as far as any report is concerned.
/// A technician's coverage, and which district a machine population sits in, are exactly the questions
/// the office asks, so the answer has to be an id rather than a spelling.</summary>
public class IndianState
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    /// <summary>Display order. Alphabetical as seeded; kept as a column so the shop's own states can be
    /// floated to the top of a picker later without renaming anything.</summary>
    public int SortOrder { get; set; }
}

/// <summary>A district within a state. Seeded for Kerala only, which is where the shop works — the
/// other 35 states are present so a record from outside Kerala can still name its state, and their
/// districts can be seeded the day someone needs them.</summary>
public class District
{
    public int Id { get; set; }
    public int StateId { get; set; }
    public string Name { get; set; } = string.Empty;

    public IndianState State { get; set; } = null!;
}
