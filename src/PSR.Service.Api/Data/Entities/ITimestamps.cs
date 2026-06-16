namespace PSR.Service.Api.Data.Entities;

/// <summary>Entities with created/updated timestamps stamped automatically on save.</summary>
public interface ITimestamps
{
    DateTime CreatedAt { get; set; }
    DateTime UpdatedAt { get; set; }
}
