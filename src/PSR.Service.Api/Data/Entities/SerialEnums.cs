namespace PSR.Service.Api.Data.Entities;

/// <summary>Lifecycle status of a single serial-tracked unit. Stored as a string so the tokens stay
/// stable and shared with the (future) mobile technician app. Desktop drives the SERVICE_CENTER-side
/// transitions; RECEIVED / COLLECTED / IN_TRANSIT_TECH are produced by the technician (mobile) later.</summary>
public enum SerialStatus
{
    Issued,          // dispatched to a technician; owner = SERVICE_CENTER (in transit) until acknowledged
    Received,        // technician confirmed in-hand (set by mobile)
    Installed,       // fitted at a customer during service
    Used,            // consumed (replacement unit / sale)
    Collected,       // faulty unit collected back from a customer; held by the technician
    Defective,       // known faulty
    Missing,         // reported not received / lost
    InTransitSc,     // technician shipping the unit back to the service center
    ReturnedToSc,    // received back at the service center (re-issuable)
    Repaired,        // repaired at the service center (re-issuable)
    InTransitTech,   // held in a pending technician-to-technician transfer (mobile)
}

/// <summary>Who currently holds the unit.</summary>
public enum SerialOwnerType
{
    ServiceCenter,
    Technician,
    Customer,
}
