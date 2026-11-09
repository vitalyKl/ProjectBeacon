namespace ProjectBeacon.Application.Mail;

/// <summary>Sends plain-text mail. Purpose is allowlisted before delivery metadata is persisted.</summary>
public interface IEmailSender
{
    bool IsConfigured { get; }
    Task SendAsync(string to,string subject,string textBody,CancellationToken ct=default);
    Task SendAsync(string to,string subject,string textBody,string purpose,CancellationToken ct=default)
        => SendAsync(to,subject,textBody,ct);
}
