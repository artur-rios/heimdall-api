using ArturRios.Data.Relational.Core.Configuration;
using ArturRios.Heimdall.Data.EntityMaps;
using ArturRios.Heimdall.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ArturRios.Heimdall.Data.Configuration;

public class AppDbContext(
    DbContextOptions options,
    ILoggerFactory loggerFactory,
    DbContextDiagnosticsOptions diagnostics) : BaseDbContext(options)
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
    }
}
