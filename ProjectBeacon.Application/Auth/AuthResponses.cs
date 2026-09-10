namespace ProjectBeacon.Application.Auth;

public record LoginResponse(Guid UserId, string Login, string Email, bool IsAdmin, string? Token = null);

public record RegisterResponse(Guid UserId, string Login, string Email);

public record BootstrapResponse(Guid UserId, string Login, string Email, bool IsAdmin, string Password);
