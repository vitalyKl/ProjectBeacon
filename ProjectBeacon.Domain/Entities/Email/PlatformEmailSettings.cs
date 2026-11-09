namespace ProjectBeacon.Domain.Entities.Email;

using ProjectBeacon.Domain.Common;

public sealed class PlatformEmailSettings : Entity
{
    public static readonly Guid SingletonId = Guid.Parse("00000000-0000-4000-8000-000000000001");
    public bool Enabled { get; private set; }
    public string Host { get; private set; } = string.Empty;
    public int Port { get; private set; } = 587;
    public string Username { get; private set; } = string.Empty;
    public string PasswordCipher { get; private set; } = string.Empty;
    public string FromAddress { get; private set; } = string.Empty;
    public bool UseStartTls { get; private set; } = true;
    public DateTime UpdatedAt { get; private set; }

    public static PlatformEmailSettings Create(bool enabled,string host,int port,string username,string passwordCipher,string fromAddress,bool useStartTls,DateTime updatedAtUtc)
    {
        if (updatedAtUtc.Kind != DateTimeKind.Utc) throw new ArgumentException("UpdatedAt must be UTC.", nameof(updatedAtUtc));
        var row = new PlatformEmailSettings { Id = SingletonId };
        row.Update(enabled,host,port,username,passwordCipher,fromAddress,useStartTls,updatedAtUtc);
        return row;
    }

    public void Update(bool enabled,string host,int port,string username,string? passwordCipher,string fromAddress,bool useStartTls,DateTime updatedAtUtc)
    {
        if (Id != Guid.Empty && Id != SingletonId) throw new InvalidOperationException("Platform email settings must use the singleton id.");
        if (updatedAtUtc.Kind != DateTimeKind.Utc) throw new ArgumentException("UpdatedAt must be UTC.", nameof(updatedAtUtc));
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        Id=SingletonId; Enabled=enabled; Host=host.Trim(); Port=port; Username=username.Trim();
        if (passwordCipher is not null) PasswordCipher=passwordCipher;
        FromAddress=fromAddress.Trim(); UseStartTls=useStartTls; UpdatedAt=updatedAtUtc;
    }
}
