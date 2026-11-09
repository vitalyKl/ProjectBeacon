namespace ProjectBeacon.Infrastructure.Mail;

using System.Net;
using System.Net.Mail;
using System.Security.Cryptography;
using Application.Mail;
using Application.Persistence;
using Application.Security;
using Domain.Entities.Email;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

/// <summary>SMTP sender with live database configuration and MAIL__* fallback for existing installs.</summary>
public sealed class SmtpEmailSender : IEmailSender
{
    private readonly ILogger<SmtpEmailSender> _log;
    private readonly IBeaconDbFactory _dbFactory;
    private readonly ISecretProtector _secrets;
    private readonly IConfiguration _configuration;

    public SmtpEmailSender(IBeaconDbFactory dbFactory,ISecretProtector secrets,IConfiguration configuration,ILogger<SmtpEmailSender> log)
    { _dbFactory=dbFactory; _secrets=secrets; _configuration=configuration; _log=log; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_configuration["MAIL:Host"]) && !string.IsNullOrWhiteSpace(_configuration["MAIL:From"]);
    public Task SendAsync(string to,string subject,string textBody,CancellationToken ct=default) => SendAsync(to,subject,textBody,"other",ct);

    public async Task SendAsync(string to,string subject,string textBody,string purpose,CancellationToken ct=default)
    {
        var kind=NormalizePurpose(purpose);
        MailConfig cfg;
        try { cfg=await ResolveAsync(ct); }
        catch(CryptographicException)
        {
            await RecordAsync(to,kind,"Failed","smtp_secret_unreadable",ct);
            throw new InvalidOperationException("The saved SMTP credential cannot be decrypted with the current data-protection key.");
        }

        if(!cfg.Enabled||string.IsNullOrWhiteSpace(cfg.Host)||string.IsNullOrWhiteSpace(cfg.From))
        {
            await RecordAsync(to,kind,"Skipped","smtp_not_configured",ct);
            if(kind=="smtp_test") throw new InvalidOperationException("SMTP is not configured or is disabled.");
            _log.LogInformation("Mail delivery skipped because SMTP is not configured or is disabled.");
            return;
        }

        try
        {
            using var client=new SmtpClient(cfg.Host,cfg.Port){EnableSsl=cfg.UseStartTls,Timeout=15000};
            if(!string.IsNullOrEmpty(cfg.Username)) client.Credentials=new NetworkCredential(cfg.Username,cfg.Password);
            using var message=new MailMessage(cfg.From,to,subject,textBody);
            await client.SendMailAsync(message,ct);
            await RecordAsync(to,kind,"Sent",null,ct);
        }
        catch(OperationCanceledException)
        {
            await RecordAsync(to,kind,"Failed","smtp_cancelled",CancellationToken.None);
            throw;
        }
        catch(SmtpException ex)
        {
            var code=ex.StatusCode is SmtpStatusCode.MailboxBusy or SmtpStatusCode.MailboxUnavailable or SmtpStatusCode.TransactionFailed
                ? "smtp_rejected" : "smtp_send_failed";
            await RecordAsync(to,kind,"Failed",code,ct);
            throw;
        }
        catch
        {
            await RecordAsync(to,kind,"Failed","smtp_send_failed",ct);
            throw;
        }
    }

    private async Task<MailConfig> ResolveAsync(CancellationToken ct)
    {
        var tenant=GlobalTenant();
        await using var db=_dbFactory.CreateDbContext(tenant);
        var saved=await db.PlatformEmailSettings.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==PlatformEmailSettings.SingletonId,ct);
        if(saved is not null)
        {
            var password=string.IsNullOrEmpty(saved.PasswordCipher)?string.Empty:_secrets.Open(saved.PasswordCipher);
            return new MailConfig(saved.Enabled,saved.Host,saved.Port,saved.Username,password,saved.FromAddress,saved.UseStartTls);
        }
        var host=Config("MAIL:Host");
        var port=int.TryParse(Config("MAIL:Port"),out var p)?p:587;
        var user=Config("MAIL:User");
        var pass=Config("MAIL:Password");
        var from=Config("MAIL:From");
        return new MailConfig(!string.IsNullOrWhiteSpace(host)&&!string.IsNullOrWhiteSpace(from),host,port,user,pass,from,port!=25);
    }

    private async Task RecordAsync(string recipient,string purpose,string status,string? code,CancellationToken ct)
    {
        try
        {
            var tenant=GlobalTenant();
            await using var db=_dbFactory.CreateDbContext(tenant);
            db.EmailDeliveryLogs.Add(EmailDeliveryLog.Create(DateTime.UtcNow,
                string.IsNullOrWhiteSpace(recipient)?"unknown":recipient.Trim(),purpose,status,code));
            await db.SaveChangesAsync(ct);
        }
        catch { _log.LogWarning("Could not persist outgoing mail delivery metadata."); }
    }

    private string Config(string key)=>_configuration[key]??Environment.GetEnvironmentVariable(key.Replace(":","__"))??string.Empty;
    private static string NormalizePurpose(string? purpose)=>purpose switch
    {
        "org_invite"=>"org_invite","project_invite"=>"project_invite","password_reset"=>"password_reset","smtp_test"=>"smtp_test",_=>"other"
    };
    private static TenantContext GlobalTenant(){var t=new TenantContext();t.Assign(null,null,unscoped:true);return t;}
    private sealed record MailConfig(bool Enabled,string Host,int Port,string Username,string Password,string From,bool UseStartTls);
}
