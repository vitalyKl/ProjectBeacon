namespace ProjectBeacon.Infrastructure.Security;

using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Domain.Entities.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;

public static class JwtTokenService
{
    private const string Issuer = "ProjectBeacon";
    private const string Audience = "ProjectBeaconAPI";

    public static string GenerateToken(
        User user,
        string secretKey,
        TimeSpan expiration = default,
        Guid? projectId = null,
        Guid? orgId = null)
    {
        var expiry = expiration == default ? TimeSpan.FromHours(24) : expiration;

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.Login),
            new(ClaimTypes.Email, user.Email),
            new("isAdmin", user.IsAdmin.ToString())
        };
        if (projectId is { } pid)
            claims.Add(new Claim("project_id", pid.ToString()));
        if (orgId is { } oid)
            claims.Add(new Claim("org_id", oid.ToString()));

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secretKey.PadRight(64).Substring(0, 64)));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: Issuer,
            audience: Audience,
            claims: claims,
            expires: DateTime.UtcNow + expiry,
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public static ClaimsPrincipal ParsePrincipal(string token, string secretKey)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secretKey.PadRight(64).Substring(0, 64)));
        var validation = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = Issuer,
            ValidAudience = Audience,
            IssuerSigningKey = key
        };

        return new JwtSecurityTokenHandler().ValidateToken(token, validation, out _);
    }
}
