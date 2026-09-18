namespace ProjectBeacon.Application.Auth;

public record LoginRequest(string Login, string Password, string? IpAddress = null);

public record RegisterRequest(string Login, string Email, string Password, string? InviteToken = null);

public record LogoutRequest(Guid UserId);

public record BootstrapRequest;

public record LoginCommand(LoginRequest Request);

public record RegisterCommand(RegisterRequest Request);

public record ForgotPasswordRequest(string Email);

public record ResetPasswordRequest(string Token, string Password);

public record ChangePasswordRequest(Guid UserId, string CurrentPassword, string NewPassword);
