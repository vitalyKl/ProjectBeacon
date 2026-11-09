namespace ProjectBeacon.Application.Mail;

/// <summary>Sends plain-text mail. The purpose overload records safe delivery metadata.</summary>
public interface IEmailSender
{
    bool IsConfigured { get; }

    Task SendAsync(string to, string subject, string textBody, CancellationToken ct = default);

    /// <summary>
    /// Sends mail with a short purpose code. The default implementation preserves compatibility
    /// with fake senders that implement only the original method.
    /// </summary>
    Task SendAsync(string to, string subject, string textBody, string purpose, CancellationToken ct = default)
        => SendAsync(to, subject, textBody, ct);
}
