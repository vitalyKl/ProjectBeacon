namespace ProjectBeacon.Application.Auth;
/// <summary>
/// Signed-in user. Token is the session token when one was issued.
/// </summary>
public record LoginResponse(Guid UserId, string Login, string Email, bool IsAdmin, string? Token = null);
/// <summary>
/// The created user. No session token.
/// </summary>
public record RegisterResponse(Guid UserId, string Login, string Email);
/// <summary>
/// The first admin. Password is the generated password and is shown once.
/// </summary>
public record BootstrapResponse(Guid UserId, string Login, string Email, bool IsAdmin, string Password);
/// <summary>
/// Admin after break-glass reset. Password is the new generated password.
/// </summary>
public record RecoverAdminResponse(Guid UserId, string Login, string Email, bool IsAdmin, string Password);
