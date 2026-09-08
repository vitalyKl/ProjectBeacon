namespace ProjectBeacon.Domain.Entities;

using ProjectBeacon.Domain.Common;

public class User : Entity
{
    public User() { }

    public string Login { get; private set; } = string.Empty;
    public string Email { get; private set; } = string.Empty;
    public string PasswordHash { get; private set; } = string.Empty;
    public bool IsAdmin { get; private set; }

    public static User Create(string login, string email, string passwordHash, bool isAdmin = false)
    {
        var user = Entity.New<User>();
        user.Login = login;
        user.Email = email;
        user.PasswordHash = passwordHash;
        user.IsAdmin = isAdmin;
        return user;
    }
}
