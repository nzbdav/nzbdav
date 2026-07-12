namespace NzbWebDAV.Database.Models;

public class Account
{
    public AccountType Type { get; init; }
    public string Username { get; init; }
    public string PasswordHash { get; init; }
    public string RandomSalt { get; init; }

    public enum AccountType
    {
        Admin = 1,
        WebDav = 2,
    }
}
