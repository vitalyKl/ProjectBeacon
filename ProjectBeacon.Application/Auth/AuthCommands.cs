namespace ProjectBeacon.Application.Auth;

public record LoginRequest(string Login, string Password, string? IpAddress = null);

public record RegisterRequest(string Login, string Email, string Password);

public record LogoutRequest(Guid UserId);

public record BootstrapRequest;

public record LoginCommand(LoginRequest Request);

public record RegisterCommand(RegisterRequest Request);
