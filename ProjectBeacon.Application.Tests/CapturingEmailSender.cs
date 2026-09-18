namespace ProjectBeacon.Application.Tests;

using Infrastructure.Mail;

internal sealed class CapturingEmailSender : IEmailSender
{
    public bool IsConfigured => true;
    public List<(string To, string Subject, string Body)> Sent { get; } = [];

    public Task SendAsync(string to, string subject, string textBody, CancellationToken ct = default)
    {
        Sent.Add((to, subject, textBody));
        return Task.CompletedTask;
    }
}
