namespace PSR.Service.Api.Data.Entities;

/// <summary>A published client build. The in-app updater polls for the highest active
/// <see cref="VersionCode"/> for its own <see cref="ClientId"/> and offers (or forces) the download.
///
/// Only mobile clients are listed here: the WPF desktop app updates through Velopack against its own
/// release feed, so it never asks this table for anything.</summary>
public class AppVersion
{
    public long Id { get; set; }

    /// <summary>Which app this build belongs to — a <see cref="Settings.ClientKinds"/> value. Rows are
    /// scoped per client so one app's release can never be offered to another.</summary>
    public string ClientId { get; set; } = Settings.ClientKinds.FieldPortal;

    /// <summary>Human-readable version ("0.2.0"), shown in the update prompt.</summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>Sort key and the only thing compared when deciding "is there something newer" — the
    /// pubspec build number (the +N in "0.2.0+7"). Monotonically increasing per client.</summary>
    public int VersionCode { get; set; }

    /// <summary>S3 object key for the signed APK (preferred) OR a full http(s) URL for testing.</summary>
    public string ApkS3Key { get; set; } = string.Empty;

    public string? Notes { get; set; }

    /// <summary>Whether the updater refuses to be dismissed. Independent of the version floor: this
    /// nags on the device, the floor turns the API off. Usually you set this first and raise the
    /// floor only once uptake is high.</summary>
    public bool Mandatory { get; set; }

    /// <summary>Cleared to pull a bad release without deleting the audit trail.</summary>
    public bool IsActive { get; set; } = true;

    public DateTime PublishedAt { get; set; } = DateTime.UtcNow;
}
