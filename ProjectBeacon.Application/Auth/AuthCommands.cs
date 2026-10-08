namespace ProjectBeacon.Application.Auth;
/// <summary>
/// Login and password. IpAddress, when set, is stored on the session.
/// </summary>
public record LoginRequest(string Login, string Password, string? IpAddress = null);
/// <summary>
/// Login, email, and password. InviteToken is required when invite-only registration is on.
/// </summary>
public record RegisterRequest(string Login, string Email, string Password, string? InviteToken = null);
/// <summary>
/// User whose current session should end.
/// </summary>
public record LogoutRequest(Guid UserId);
/// <summary>
/// Empty marker. The bootstrap token is checked separately and is not a field here.
/// </summary>
public record BootstrapRequest;
/// <summary>
/// Command for login.
/// </summary>
public record LoginCommand(LoginRequest Request);
/// <summary>
/// Command for register.
/// </summary>
public record RegisterCommand(RegisterRequest Request);
/// <summary>
/// Email address for a reset link. The handler succeeds even when the address is unknown.
/// </summary>
public record ForgotPasswordRequest(string Email);
/// <summary>
/// Raw password-reset token and the new password.
/// </summary>
public record ResetPasswordRequest(string Token, string Password);
/// <summary>
/// User, current password, and the replacement password.
/// </summary>
public record ChangePasswordRequest(Guid UserId, string CurrentPassword, string NewPassword);
