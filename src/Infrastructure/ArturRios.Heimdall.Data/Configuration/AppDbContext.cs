using ArturRios.Data.Relational.Core.Configuration;
using ArturRios.Heimdall.Data.EntityMaps;
using ArturRios.Heimdall.Domain.Entities;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ArturRios.Heimdall.Data.Configuration;

public class AppDbContext(
    DbContextOptions options,
    ILoggerFactory loggerFactory,
    DbContextDiagnosticsOptions diagnostics) : BaseDbContext(options), IDataProtectionKeyContext
{
    private const string Schema = "heimdall";

    public DbSet<Person> Persons { get; init; }
    public DbSet<Scope> Scopes { get; init; }
    public DbSet<Role> Roles { get; init; }
    public DbSet<Application> Applications { get; init; }
    public DbSet<GoogleUser> GoogleUsers { get; init; }
    public DbSet<ScopeOwner> ScopeOwners { get; init; }
    public DbSet<ScopeUser> ScopeUsers { get; init; }
    public DbSet<ScopePermission> ScopePermissions { get; init; }
    public DbSet<PasswordResetToken> PasswordResetTokens { get; init; }
    public DbSet<EmailVerificationToken> EmailVerificationTokens { get; init; }
    public DbSet<TwoFactorAuth> TwoFactorAuths { get; init; }
    public DbSet<TwoFactorEmailCode> TwoFactorEmailCodes { get; init; }
    public DbSet<TwoFactorRecoveryCode> TwoFactorRecoveryCodes { get; init; }
    public DbSet<AuditLog> AuditLogs { get; init; }

    /// <summary>
    ///     ASP.NET Core's Data Protection key ring, kept in the database rather than on a local
    ///     filesystem (<see cref="IDataProtectionKeyContext" />).
    /// </summary>
    /// <remarks>
    ///     Not a domain table, and the only one here that no entity map configures — its shape
    ///     belongs to Data Protection. It is in this context because the keys have to outlive the
    ///     container and be reachable from every instance: the TOTP secrets of UC-36 are encrypted
    ///     with them (NFR-16), and a key ring that is lost or not shared makes every one of those
    ///     secrets undecryptable, which silently stops the authenticator-app factor from ever
    ///     verifying again. The default is a directory on the local filesystem, which the image does
    ///     not persist and two instances do not share.
    /// </remarks>
    public DbSet<DataProtectionKey> DataProtectionKeys { get; init; }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        optionsBuilder
            .UseLoggerFactory(loggerFactory)
            .UseSnakeCaseNamingConvention()
            .EnableDetailedErrors(diagnostics.DetailedErrors)
            .EnableSensitiveDataLogging(diagnostics.SensitiveDataLogging);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<Person>().Configure();
        modelBuilder.Entity<Scope>().Configure();
        modelBuilder.Entity<Role>().Configure();
        modelBuilder.Entity<Application>().Configure();
        modelBuilder.Entity<GoogleUser>().Configure();
        modelBuilder.Entity<ScopePermission>().Configure();
        modelBuilder.Entity<ScopeOwner>().Configure();
        modelBuilder.Entity<ScopeUser>().Configure();
        modelBuilder.Entity<PasswordResetToken>().Configure();
        modelBuilder.Entity<EmailVerificationToken>().Configure();
        modelBuilder.Entity<TwoFactorAuth>().Configure();
        modelBuilder.Entity<TwoFactorEmailCode>().Configure();
        modelBuilder.Entity<TwoFactorRecoveryCode>().Configure();
        modelBuilder.Entity<AuditLog>().Configure();
    }
}
