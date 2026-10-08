namespace ProjectBeacon.Infrastructure.Mail;

/// <summary>Sends a plain-text message. <see cref="IsConfigured"/> reports whether a sender is set up.</summary>
public interface IEmailSender
{
    bool IsConfigured { get; }

    Task SendAsync(string to, string subject, string textBody, CancellationToken ct = default);
}
