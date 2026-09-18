namespace ProjectBeacon.Infrastructure.Mail;

using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

public sealed class SmtpEmailSender : IEmailSender
{
    private readonly ILogger<SmtpEmailSender> _log;
    private readonly string _host;
    private readonly int _port;
    private readonly string _user;
    private readonly string _password;
    private readonly string _from;

    public SmtpEmailSender(IConfiguration configuration, ILogger<SmtpEmailSender> log)
    {
        _log = log;
        _host = configuration["MAIL:Host"] ?? Environment.GetEnvironmentVariable("MAIL__Host") ?? string.Empty;
        _port = int.TryParse(configuration["MAIL:Port"] ?? Environment.GetEnvironmentVariable("MAIL__Port"), out var port)
            ? port
            : 587;
        _user = configuration["MAIL:User"] ?? Environment.GetEnvironmentVariable("MAIL__User") ?? string.Empty;
        _password = configuration["MAIL:Password"] ?? Environment.GetEnvironmentVariable("MAIL__Password") ?? string.Empty;
        _from = configuration["MAIL:From"] ?? Environment.GetEnvironmentVariable("MAIL__From") ?? string.Empty;
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_host) && !string.IsNullOrWhiteSpace(_from);

    public async Task SendAsync(string to, string subject, string textBody, CancellationToken ct = default)
    {
        if (!IsConfigured)
        {
            _log.LogInformation("Mail not configured; skip send to {To}. {Subject}\n{Body}", to, subject, textBody);
            return;
        }

        using var client = new SmtpClient(_host, _port);
        if (!string.IsNullOrEmpty(_user))
        {
            client.Credentials = new NetworkCredential(_user, _password);
            client.EnableSsl = _port != 25;
        }

        using var message = new MailMessage(_from, to, subject, textBody);
        await client.SendMailAsync(message, ct);
    }
}
