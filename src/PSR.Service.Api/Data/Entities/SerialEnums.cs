namespace PSR.Service.Api.Data.Entities;

/// <summary>Lifecycle status of a single serial-tracked unit. Stored as a string so the tokens stay
/// stable and shared with the (future) mobile technician app. Desktop drives the SERVICE_CENTER-side
/// transitions; RECEIVED / COLLECTED / IN_TRANSIT_TECH are produced by the technician (mobile) later.</summary>
public enum SerialStatus
{
    Issued,          // dispatched to a technician; owner = SERVICE_CENTER (in transit) until acknowledged
    Received,        // technician confirmed in-hand (set by mobile)

    // Deployed at a customer, however it got there: fitted during a service, handed over as a
    // replacement, or sold across the counter. There used to be a second token, INSTALLED, for the
    // fitted case alone. Nothing anywhere read the two apart - same owner, same effect on the
    // count, same everything the ledger cares about - so all it did was put two words for one
    // state on the SN screen and leave people asking which of them meant what. The line type
    // still records whether a unit was fitted or replaced; that is where the distinction belongs.
    Used,

    Collected,       // faulty unit collected back from a customer; held by the technician
    Defective,       // known faulty
    Missing,         // reported not received / lost
    InTransitSc,     // technician shipping the unit back to the service center
    ReturnedToSc,    // received back at the service center (re-issuable)
    Repaired,        // repaired at the service center (re-issuable)
    InTransitTech,   // held in a pending technician-to-technician transfer (mobile)
    UnderRepair,     // arrived back at the service center on a FAULTY return; a repair job is open on it
    Scrapped,        // written off at the service center (repair job closed as total loss) — terminal
}

/// <summary>Who currently holds the unit.</summary>
public enum SerialOwnerType
{
    ServiceCenter,
    Technician,
    Customer,
}
