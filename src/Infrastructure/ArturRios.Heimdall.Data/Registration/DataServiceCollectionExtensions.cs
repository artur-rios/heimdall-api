using ArturRios.Heimdall.Data.Configuration;
using ArturRios.Heimdall.Data.Persistence;
using ArturRios.Heimdall.Data.Seeding;
using ArturRios.Heimdall.Domain.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;

namespace ArturRios.Heimdall.Data.Registration;

public static class DataServiceCollectionExtensions
{
    /// <summary>
    ///     Registers the persistence services that spend single-use credentials, charge bounded budgets and
    ///     take row locks as conditional writes.
    /// </summary>
    public static IServiceCollection AddPersistenceServices(this IServiceCollection services)
    {
        // Spending single-use credentials and charging bounded budgets as one conditional UPDATE
        // each, so concurrent requests cannot both spend one code or lose each other's counts.
        services.AddScoped<IAtomicWrites, AtomicWrites>();
        // UC-53's two-live-credentials limit, held under a row lock so concurrent issues cannot pass it.
        services.AddScoped<IApplicationCredentialSlots, ApplicationCredentialSlots>();
        // NFR-21 (#195): the tombstones a hard delete writes first, idempotently, as one upsert.
        services.AddScoped<IErasedSubjects, ErasedSubjects>();
        // FR-SR-14: the per-address budget of registration-attempt notices, charged as one upsert.
        services.AddScoped<IAttemptNoticeBudgets, AttemptNoticeBudgets>();
        // FR-SC-17: opening, answering, withdrawing and voiding scope owner invitations, each one
        // conditional write, and acceptance one transaction with the ownership it grants.
        services.AddScoped<IScopeOwnerInvitations, ScopeOwnerInvitations>();
        // #209, #212, #204: the transaction a command opens around the writes that must land together.
        services.AddScoped<IWriteTransactions, WriteTransactions>();
        // #204: a scope's delete cascaded to its members as set-based statements, never member by member.
        services.AddScoped<IScopeCascades, ScopeCascades>();
        // #202: the scope row locks that make a last-owner guard and its write one step.
        services.AddScoped<IInvariantLocks, InvariantLocks>();

        return services;
    }

    /// <summary>
    ///     Puts the Data Protection key ring in the database (UC-36, FR-2F-02).
    /// </summary>
    public static IServiceCollection AddDataProtectionKeyRing(this IServiceCollection services)
    {
        // UC-36 (FR-2F-02): the TOTP secret is encrypted at rest with ASP.NET Core's Data
        // Protection API before it is persisted.
        //
        // The key ring goes in the database, and the application name is fixed, because the default
        // is neither durable nor shared: keys land in a directory on the local filesystem that the
        // image does not persist and a second instance does not see. Either way the effect is the
        // same and it is silent — every stored TOTP secret becomes undecryptable, ITotpCodeVerifier
        // catches the CryptographicException and reports it as a wrong code, and every caller whose
        // second factor is an authenticator app is locked out of it with no indication why. That
        // survives a redeploy today only because a single container keeps its filesystem between
        // restarts; it would not survive being recreated, and it never survives a second replica
        // (NFR-06).
        services.AddDataProtection()
            .PersistKeysToDbContext<AppDbContext>()
            .SetApplicationName("Heimdall");

        return services;
    }

    /// <summary>
    ///     Registers the master user options and the database seeder.
    /// </summary>
    public static IServiceCollection AddDatabaseSeeding(this IServiceCollection services)
    {
        services.AddSingleton(MasterUserOptions.FromEnvironment());
        services.AddScoped<DatabaseSeeder>();

        return services;
    }
}
