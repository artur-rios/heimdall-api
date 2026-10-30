using ArturRios.Heimdall.Command.Services;
using ArturRios.Heimdall.Data.Configuration;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Util.Hashing;

namespace ArturRios.Heimdall.WebApi.Tests.Support;

/// <summary>What <see cref="Seeds.ScopeAsync" /> writes beyond a unique name; every property left <c>null</c> keeps the column default.</summary>
public sealed record ScopeOptions
{
    /// <summary>The scope's name; omitted, a unique <c>scope-…</c> one.</summary>
    public string? Name { get; init; }

    public string? Description { get; init; }
    public bool IsDeleted { get; init; }
    public bool? SelfRegistrationEnabled { get; init; }
    public bool? GoogleSignInEnabled { get; init; }
}

/// <summary>What <see cref="Seeds.PersonAsync" /> writes beyond the role; every property left unset keeps a live, verified person.</summary>
public sealed record PersonOptions
{
    /// <summary>The full address; omitted, a unique one built from <see cref="EmailPrefix" />.</summary>
    public string? Email { get; init; }

    public string EmailPrefix { get; init; } = "person";

    /// <summary>The display name; omitted, the role's name.</summary>
    public string? Name { get; init; }

    public bool EmailVerified { get; init; } = true;
    public bool IsDeleted { get; init; }

    /// <summary>When set, processing is restricted since then, on the accuracy-contested ground.</summary>
    public DateTime? RestrictedAt { get; init; }

    /// <summary>The scope the person belongs to as a User: a membership row, and <c>Person.ScopeId</c> set before the insert (#237).</summary>
    public Scope? MemberOf { get; init; }

    /// <summary>A <c>Person.ScopeId</c> to set without a membership row.</summary>
    public long? ScopeId { get; init; }

    /// <summary>The scopes the person owns.</summary>
    public IReadOnlyList<Scope> Owns { get; init; } = [];

    /// <summary>Anything else a test needs on the row, applied before the insert.</summary>
    public Action<Person>? Configure { get; init; }
}

/// <summary>
///     Seeds scopes and persons straight through the database, so a test's Given never depends on the
///     endpoint it tests — and a new required column or trigger rule on either table is fixed here, once.
/// </summary>
/// <remarks>
///     Every seeded person carries the same salted Argon2id hash of <see cref="Password" />, derived once
///     per process: hashing at 600 MB per person dominated the suite's time and memory. No test needs two
///     persons to hold distinct salts or hashes (the only tests that compare them compare a person's hash
///     before and after a production change of it), so there is no per-call password option; a test that
///     one day needs its own hashes derives them itself.
/// </remarks>
public static class Seeds
{
    /// <summary>The password every seeded person can sign in with.</summary>
    public const string Password = "Str0ng-Test-Pass!";

    private static readonly Lazy<(byte[] Hash, byte[] Salt)> SharedCredentials = new(() =>
    {
        var hash = ArturRios.Util.Hashing.Hash.EncodeWithRandomSalt(Password, out var salt, PasswordHashing.Configuration);
        return (hash, salt);
    });

    /// <summary>The shared password's hash and salt, as fresh copies a row can own.</summary>
    public static (byte[] Hash, byte[] Salt) Credentials()
    {
        var (hash, salt) = SharedCredentials.Value;
        return (hash.ToArray(), salt.ToArray());
    }

    public static Task<Scope> ScopeAsync(PostgresFixture db, ScopeOptions? options = null) =>
        ScopeAsync(() => db.CreateContext(), options);

    public static Task<Scope> ScopeAsync(IsolatedDatabase db, ScopeOptions? options = null) =>
        ScopeAsync(() => db.CreateContext(), options);

    public static Task<Person> PersonAsync(
        PostgresFixture db, Roles role = Roles.User, PersonOptions? options = null) =>
        PersonAsync(() => db.CreateContext(), role, options);

    public static Task<Person> PersonAsync(
        IsolatedDatabase db, Roles role = Roles.User, PersonOptions? options = null) =>
        PersonAsync(() => db.CreateContext(), role, options);

    private static async Task<Scope> ScopeAsync(Func<AppDbContext> createContext, ScopeOptions? options)
    {
        await using var context = createContext();
        return await ScopeAsync(context, options);
    }

    /// <summary>Seeds through a context the caller owns, for tests that write several rows in one.</summary>
    public static async Task<Scope> ScopeAsync(AppDbContext context, ScopeOptions? options = null)
    {
        options ??= new ScopeOptions();

        var scope = new Scope
        {
            PublicId = Guid.NewGuid(),
            Name = options.Name ?? $"scope-{Guid.NewGuid():N}",
            Description = options.Description,
            IsDeleted = options.IsDeleted
        };

        if (options.SelfRegistrationEnabled is { } open)
        {
            scope.SelfRegistrationEnabled = open;
        }

        if (options.GoogleSignInEnabled is { } google)
        {
            scope.GoogleSignInEnabled = google;
        }

        context.Scopes.Add(scope);
        await context.SaveChangesAsync();
        return scope;
    }

    private static async Task<Person> PersonAsync(
        Func<AppDbContext> createContext, Roles role, PersonOptions? options)
    {
        await using var context = createContext();
        return await PersonAsync(context, role, options);
    }

    /// <summary>Seeds through a context the caller owns, for tests that write several rows in one.</summary>
    public static async Task<Person> PersonAsync(
        AppDbContext context, Roles role = Roles.User, PersonOptions? options = null)
    {
        options ??= new PersonOptions();

        var (hash, salt) = Credentials();

        var person = new Person
        {
            PublicId = Guid.NewGuid(),
            Name = options.Name ?? $"{role}",
            Email = options.Email ?? $"{options.EmailPrefix}-{Guid.NewGuid():N}@test.local",
            PasswordHash = hash,
            Salt = salt,
            RoleId = (long)role,
            EmailVerified = options.EmailVerified,
            IsDeleted = options.IsDeleted,
            ProcessingRestrictedAt = options.RestrictedAt,
            RestrictionGround = options.RestrictedAt is null ? null : (int)RestrictionGrounds.AccuracyContested,
            // Set before the insert: a whole-row save ignores it once the row exists (#237).
            ScopeId = options.MemberOf?.Id ?? options.ScopeId
        };

        options.Configure?.Invoke(person);
        context.Persons.Add(person);
        await context.SaveChangesAsync();

        if (options.MemberOf is not null)
        {
            context.ScopeUsers.Add(new ScopeUser { ScopeId = options.MemberOf.Id, PersonId = person.Id });
        }

        foreach (var scope in options.Owns)
        {
            context.ScopeOwners.Add(new ScopeOwner { ScopeId = scope.Id, PersonId = person.Id });
        }

        await context.SaveChangesAsync();
        return person;
    }
}
