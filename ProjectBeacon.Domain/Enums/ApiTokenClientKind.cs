namespace ProjectBeacon.Domain.Enums;

/// <summary>Identifies who owns a project API token and what lifecycle controls apply.</summary>
public enum ApiTokenClientKind
{
    /// <summary>Created by a human through project token management.</summary>
    Manual = 0,
    /// <summary>Created for a separately managed external integration.</summary>
    External = 1,
    /// <summary>Short-lived token issued to the workstation harness for a single running command.</summary>
    Harness = 2
}
