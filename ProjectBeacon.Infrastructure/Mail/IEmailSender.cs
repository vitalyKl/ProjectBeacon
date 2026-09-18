namespace ProjectBeacon.Infrastructure.Mail;

public interface IEmailSender
{
    bool IsConfigured { get; }

    Task SendAsync(string to, string subject, string textBody, CancellationToken ct = default);
}
