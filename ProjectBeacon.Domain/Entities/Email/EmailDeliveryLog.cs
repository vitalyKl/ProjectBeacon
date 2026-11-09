namespace ProjectBeacon.Domain.Entities.Email;

using ProjectBeacon.Domain.Common;

/// <summary>
/// Metadata-only outgoing mail history. Message bodies, subjects, credentials and links/tokens
/// are intentionally never persisted here.
/// </summary>
public sealed class EmailDeliveryLog : Entity
{
    public DateTime At { get; private set; }
    public string Recipient { get; private set; } = string.Empty;
    public string Purpose { get; private set; } = string.Empty;
    public string Status { get; private set; } = string.Empty;
    public string? ErrorCode { get; private set; }

    public static EmailDeliveryLog Create(
        DateTime atUtc,
        string recipient,
        string purpose,
        string status,
        string? errorCode = null)
    {
        if (atUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("Delivery time must be UTC.", nameof(atUtc));
        if (string.IsNullOrWhiteSpace(recipient) || recipient.Length > 320)
            throw new ArgumentException("Recipient must contain 1 to 320 characters.", nameof(recipient));
        if (string.IsNullOrWhiteSpace(purpose) || purpose.Length > 48)
            throw new ArgumentException("Purpose must contain 1 to 48 characters.", nameof(purpose));
        if (status is not ("Sent" or "Failed" or "Skipped"))
            throw new ArgumentOutOfRangeException(nameof(status));
        if (errorCode?.Length > 80)
            throw new ArgumentException("Error code is too long.", nameof(errorCode));

        var row = Entity.New<EmailDeliveryLog>();
        row.At = atUtc;
        row.Recipient = recipient.Trim();
        row.Purpose = purpose;
        row.Status = status;
        row.ErrorCode = errorCode;
        return row;
    }
}
