namespace PSR.Service.Api.Documents;

/// <summary>Seller details printed on every document. Bound from the "Company" config section;
/// the defaults match the legacy Flutter app's hard-coded HARISREE ENTERPRISE header.</summary>
public class CompanyInfo
{
    public const string SectionName = "Company";

    public string Name { get; set; } = "HARISREE ENTERPRISE";
    public string Address { get; set; } =
        "7/367-C1, Harigovindam, Kattithara Sahakarana Road, Maradu P.O., Maradu, Ernakulam - 682304";
    public string Gstin { get; set; } = "32APVPR9727B1ZH";
    public string State { get; set; } = "Kerala";
    public string StateCode { get; set; } = "32";
    public string? Phone { get; set; }
    public string? Email { get; set; }
}

/// <summary>Thrown when a document cannot be generated (bad type, missing party, etc.) — maps to 400.</summary>
public class BillingException(string message) : Exception(message);
