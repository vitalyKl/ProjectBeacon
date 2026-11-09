namespace ProjectBeacon.Infrastructure.Mail;

using System.Net;
using System.Net.Mail;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Security.Cryptography;
using Application.Mail;
using Application.Persistence;
using Application.Security;
using Domain.Entities.Email;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

/// <summary>
/// SMTP sender with live database configuration and MAIL__* fallback for existing installs.
/// Settings are read for every delivery so operator changes apply across Web replicas.
/// </summary>
public sealed class SmtpEmailSender : IEmailSender
{
    private readonly ILogger<SmtpEmailSender> _log;
    private readonly IBeaconDbFactory _dbFactory;
    private readonly ISecretProtector _secrets;
    private readonly IConfiguration _configuration;

    public SmtpEmailSender(
        IBeaconDbFactory dbFactory,
        ISecretProtector secrets,
        IConfiguration configuration,
        ILogger<SmtpEmailSender> log)
    {
        _dbFactory = dbFactory;
        _secrets = secrets;
        _configuration = configuration;
        _log = log;
    }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_configuration["MAIL:Host"])
        && !string.IsNullOrWhiteSpace(_configuration["MAIL:From"]);

    public Task SendAsync(string to, string subject, string textBody, CancellationToken ct = default) =>
        SendAsync(to, subject, textBody, "other", ct);

    public async Task SendAsync(
        string to,
        string subject,
        string textBody,
        string purpose,
        CancellationToken ct = default)
    {
        var kind = NormalizePurpose(purpose);
        MailConfiguration settings;
        try
        {
            settings = await ResolveAsync(ct);
        }
        catch (CryptographicException)
        {
            await RecordAsync(to, kind, "Failed", "smtp_secret_unreadable", ct);
            throw new InvalidOperationException(
                "The saved SMTP credential cannot be decrypted with the current data-protection key.");
        }

        if (!settings.Enabled || string.IsNullOrWhiteSpace(settings.Host) || string.IsNullOrWhiteSpace(settings.From))
        {
            await RecordAsync(to, kind, "Skipped", "smtp_not_configured", ct);
            if (kind == "smtp_test")
                throw new InvalidOperationException("SMTP is not configured or is disabled.");
            _log.LogInformation("Mail delivery skipped because SMTP is not configured or is disabled.");
            return;
        }

        try
        {
            using var client = new SmtpClient(settings.Host, settings.Port)
            {
                EnableSsl = settings.UseStartTls,
                Timeout = 15000
            };

            if (!string.IsNullOrEmpty(settings.Username))
                client.Credentials = new NetworkCredential(settings.Username, settings.Password);

            using var message = new MailMessage(settings.From, to, subject, textBody);
            await client.SendMailAsync(message, ct);
            await RecordAsync(to, kind, "Sent", null, ct);
        }
        catch (OperationCanceledException)
        {
            await RecordAsync(to, kind, "Failed", "smtp_cancelled", CancellationToken.None);
            throw;
        }
        catch (SmtpException ex)
        {
            var code = ex.StatusCode is SmtpStatusCode.MailboxBusy
                or SmtpStatusCode.MailboxUnavailable
                or SmtpStatusCode.TransactionFailed
                    ? "smtp_rejected"
                    : "smtp_send_failed";
            await RecordAsync(to, kind, "Failed", code, ct);
            throw;
        }
        catch
        {
            await RecordAsync(to, kind, "Failed", "smtp_send_failed", ct);
            throw;
        }
    }

    public async Task TestConnectionAsync(CancellationToken ct = default)
    {
        var settings = await ResolveAsync(ct);
        if (!settings.Enabled || string.IsNullOrWhiteSpace(settings.Host))
            throw new InvalidOperationException("SMTP is not configured or is disabled.");
        if (!settings.UseStartTls && !string.IsNullOrEmpty(settings.Username))
            throw new InvalidOperationException("SMTP authentication requires STARTTLS.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));

        using var client = new TcpClient();
        await client.ConnectAsync(settings.Host, settings.Port, timeout.Token);
        Stream stream = client.GetStream();
        var session = new SmtpSession(stream);

        try
        {
            await session.ExpectAsync(220, timeout.Token);
            var hello = await session.CommandAsync("EHLO projectbeacon", timeout.Token);
            RequireCode(hello, 250);

            if (settings.UseStartTls)
            {
                if (!hello.Text.Contains("STARTTLS", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The SMTP server does not advertise STARTTLS.");

                await session.CommandAsync("STARTTLS", timeout.Token, 220);
                await session.DisposeAsync();

                var tls = new SslStream(stream, leaveInnerStreamOpen: false);
                await tls.AuthenticateAsClientAsync(
                    new SslClientAuthenticationOptions { TargetHost = settings.Host },
                    timeout.Token);
                stream = tls;
                session = new SmtpSession(stream);

                var secureHello = await session.CommandAsync("EHLO projectbeacon", timeout.Token);
                RequireCode(secureHello, 250);
                hello = secureHello;
            }

            if (!string.IsNullOrEmpty(settings.Username))
            {
                if (!hello.Text.Contains("AUTH", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The SMTP server does not advertise an authentication mechanism.");

                var authPlain = Convert.ToBase64String(Encoding.UTF8.GetBytes(
                    "\0" + settings.Username + "\0" + settings.Password));
                var auth = await session.CommandAsync("AUTH PLAIN " + authPlain, timeout.Token);
                if (auth.Code == 334)
                    auth = await session.CommandAsync(authPlain, timeout.Token);
                if (auth.Code != 235)
                {
                    // Some relays advertise LOGIN but not PLAIN. Fall back only on an unsupported-mechanism response.
                    if (auth.Code is not (500 or 501 or 502 or 504)
                        || !hello.Text.Contains("LOGIN", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("SMTP authentication failed.");

                    auth = await session.CommandAsync("AUTH LOGIN", timeout.Token);
                    RequireCode(auth, 334);
                    auth = await session.CommandAsync(
                        Convert.ToBase64String(Encoding.UTF8.GetBytes(settings.Username)), timeout.Token);
                    RequireCode(auth, 334);
                    auth = await session.CommandAsync(
                        Convert.ToBase64String(Encoding.UTF8.GetBytes(settings.Password)), timeout.Token);
                    RequireCode(auth, 235);
                }
            }

            await session.CommandAsync("QUIT", timeout.Token, 221);
        }
        finally
        {
            await session.DisposeAsync();
            if (!ReferenceEquals(stream, client.GetStream()))
                await stream.DisposeAsync();
        }
    }

    private static void RequireCode(SmtpResponse response, int expected)
    {
        if (response.Code != expected)
            throw new InvalidOperationException("SMTP connection test failed during protocol negotiation.");
    }

    private sealed class SmtpSession : IAsyncDisposable
    {
        private readonly StreamReader _reader;
        private readonly StreamWriter _writer;

        public SmtpSession(Stream stream)
        {
            _reader = new StreamReader(stream, Encoding.ASCII, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            _writer = new StreamWriter(stream, Encoding.ASCII, bufferSize: 1024, leaveOpen: true)
            {
                NewLine = "\r\n",
                AutoFlush = true
            };
        }

        public async Task ExpectAsync(int expectedCode, CancellationToken ct)
        {
            var response = await ReadResponseAsync(ct);
            RequireCode(response, expectedCode);
        }

        public async Task<SmtpResponse> CommandAsync(
            string command,
            CancellationToken ct,
            int? expectedCode = null)
        {
            await _writer.WriteLineAsync(command.AsMemory(), ct);
            await _writer.FlushAsync(ct);
            var response = await ReadResponseAsync(ct);
            if (expectedCode is { } expected)
                RequireCode(response, expected);
            return response;
        }

        private async Task<SmtpResponse> ReadResponseAsync(CancellationToken ct)
        {
            var first = await _reader.ReadLineAsync(ct);
            if (string.IsNullOrEmpty(first)
                || first.Length < 3
                || !int.TryParse(first.AsSpan(0, 3), out var code))
                throw new InvalidOperationException("SMTP server returned an invalid response.");

            var lines = new StringBuilder(first);
            var line = first;
            var lineCount = 0;
            while (line.Length > 3 && line[3] == '-')
            {
                if (++lineCount > 40)
                    throw new InvalidOperationException("SMTP server returned too many response lines.");
                line = await _reader.ReadLineAsync(ct)
                    ?? throw new InvalidOperationException("SMTP server closed the connection unexpectedly.");
                lines.Append('\n').Append(line);
            }

            return new SmtpResponse(code, lines.ToString());
        }

        public async ValueTask DisposeAsync()
        {
            await _writer.DisposeAsync();
            _reader.Dispose();
        }
    }

    private sealed record SmtpResponse(int Code, string Text);

    private async Task<MailConfiguration> ResolveAsync(CancellationToken ct)
    {
        var tenant = GlobalTenant();
        await using var db = _dbFactory.CreateDbContext(tenant);
        var saved = await db.PlatformEmailSettings
            .AsNoTracking()
            .SingleOrDefaultAsync(row => row.Id == PlatformEmailSettings.SingletonId, ct);

        if (saved is not null)
        {
            var password = string.IsNullOrEmpty(saved.PasswordCipher)
                ? string.Empty
                : _secrets.Open(saved.PasswordCipher);
            return new MailConfiguration(
                saved.Enabled,
                saved.Host,
                saved.Port,
                saved.Username,
                password,
                saved.FromAddress,
                saved.UseStartTls);
        }

        var host = Config("MAIL:Host");
        var port = int.TryParse(Config("MAIL:Port"), out var configuredPort) ? configuredPort : 587;
        var username = Config("MAIL:User");
        var passwordFromEnvironment = Config("MAIL:Password");
        var from = Config("MAIL:From");
        return new MailConfiguration(
            !string.IsNullOrWhiteSpace(host) && !string.IsNullOrWhiteSpace(from),
            host,
            port,
            username,
            passwordFromEnvironment,
            from,
            port != 25);
    }

    private async Task RecordAsync(
        string recipient,
        string purpose,
        string status,
        string? errorCode,
        CancellationToken ct)
    {
        try
        {
            var tenant = GlobalTenant();
            await using var db = _dbFactory.CreateDbContext(tenant);
            db.EmailDeliveryLogs.Add(EmailDeliveryLog.Create(
                DateTime.UtcNow,
                string.IsNullOrWhiteSpace(recipient) ? "unknown" : recipient.Trim(),
                purpose,
                status,
                errorCode));
            await db.SaveChangesAsync(ct);
        }
        catch
        {
            _log.LogWarning("Could not persist outgoing mail delivery metadata.");
        }
    }

    private string Config(string key) =>
        _configuration[key] ?? Environment.GetEnvironmentVariable(key.Replace(":", "__")) ?? string.Empty;

    private static string NormalizePurpose(string? purpose) => purpose switch
    {
        "org_invite" => "org_invite",
        "project_invite" => "project_invite",
        "password_reset" => "password_reset",
        "smtp_test" => "smtp_test",
        _ => "other"
    };

    private static TenantContext GlobalTenant()
    {
        var tenant = new TenantContext();
        tenant.Assign(null, null, unscoped: true);
        return tenant;
    }

    private sealed record MailConfiguration(
        bool Enabled,
        string Host,
        int Port,
        string Username,
        string Password,
        string From,
        bool UseStartTls);
}
