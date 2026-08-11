namespace ArturRios.Heimdall.Data.Seeding;

/// <summary>
///     Credentials for the master system administrator, read from the
///     <c>HEIMDALL_MASTER_USER_*</c> environment variables. They are used only when the
///     database holds no system administrator yet — see <c>DatabaseSeeder</c>.
/// </summary>
public sealed record MasterUserOptions(string Name, string Email, string Password)
{
    /// <summary>Environment variable holding the master user's display name.</summary>
    public const string NameVariable = "HEIMDALL_MASTER_USER_NAME";

    /// <summary>Environment variable holding the master user's e-mail address.</summary>
    public const string EmailVariable = "HEIMDALL_MASTER_USER_EMAIL";

    /// <summary>Environment variable holding the master user's plain-text password.</summary>
    public const string PasswordVariable = "HEIMDALL_MASTER_USER_PASSWORD";

    /// <summary>Whether all three values are present, so a master user could be created.</summary>
    public bool IsComplete =>
        !string.IsNullOrWhiteSpace(Name) &&
        !string.IsNullOrWhiteSpace(Email) &&
        !string.IsNullOrWhiteSpace(Password);

    /// <summary>Reads the three variables from the current process environment.</summary>
    public static MasterUserOptions FromEnvironment() => new(
        Environment.GetEnvironmentVariable(NameVariable) ?? string.Empty,
        Environment.GetEnvironmentVariable(EmailVariable) ?? string.Empty,
        Environment.GetEnvironmentVariable(PasswordVariable) ?? string.Empty);
}
