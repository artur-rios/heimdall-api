using ArturRios.Configuration.Enums;
using ArturRios.IdentityManager.Data.Seeding;
using ArturRios.IdentityManager.Domain.Enums;
using ArturRios.IdentityManager.WebApi.Tests.Support;
using ArturRios.Util.Hashing;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Functional;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArturRios.IdentityManager.WebApi.Tests;

/// <summary>
///     The API seeds reference data as it starts. Constructing this class boots the API against the
///     migrated container, so each test asserts the database state the seeder left behind.
/// </summary>
[Collection(nameof(FunctionalCollection))]
public class SeedingTests(PostgresFixture fixture) : WebApiTest<Program>(EnvironmentType.Local)
{
    [FunctionalFact]
    public async Task GivenApiStarted_WhenRolesRead_ThenEveryEnumMemberIsStoredWithItsEnumId()
    {
        await using var context = fixture.CreateContext();

        var roles = await context.Roles.OrderBy(x => x.Id).ToListAsync();

        Assert.Equal(3, roles.Count);
        Assert.Equal(new[] { 1L, 2L, 3L }, roles.Select(x => x.Id).ToArray());
        Assert.Equal(
            new[] { nameof(Roles.SystemAdmin), nameof(Roles.ScopeAdmin), nameof(Roles.User) },
            roles.Select(x => x.Name).ToArray());
        Assert.All(roles, role => Assert.False(string.IsNullOrWhiteSpace(role.Description)));
    }

    [FunctionalFact]
    public async Task GivenApiStarted_WhenSystemAdminsRead_ThenTheMasterUserExistsWithAHashedPassword()
    {
        await using var context = fixture.CreateContext();

        // Scoped to the master's e-mail rather than to every System Admin: the container is shared
        // by the whole functional collection, and other classes seed System Admins of their own.
        var masters = await context.Persons
            .Where(x => x.Email == PostgresFixture.MasterUserEmail && !x.IsDeleted)
            .ToListAsync();

        var master = Assert.Single(masters);

        Assert.Equal((long)Roles.SystemAdmin, master.RoleId);
        Assert.True(master.EmailVerified);
        Assert.NotEmpty(master.PasswordHash);
        Assert.NotEmpty(master.Salt);

        // The password is stored hashed, not in the clear, and the stored hash verifies.
        Assert.True(Hash.TextMatches(PostgresFixture.MasterUserPassword, master.PasswordHash, master.Salt));
    }

    [FunctionalFact]
    public async Task GivenSeedingAlreadyRan_WhenSeederRunsAgain_ThenNoDuplicateRowsAreCreated()
    {
        await using var context = fixture.CreateContext();

        var seeder = new DatabaseSeeder(
            context,
            new MasterUserOptions("Master User", PostgresFixture.MasterUserEmail, PostgresFixture.MasterUserPassword),
            NullLogger<DatabaseSeeder>.Instance);

        await seeder.SeedAsync();

        // Roles are seeded by nothing but the seeder, so their total is a safe idempotency check.
        // The master user is counted by e-mail, since other classes seed System Admins of their own.
        Assert.Equal(3, await context.Roles.CountAsync());
        Assert.Equal(1, await context.Persons.CountAsync(x => x.Email == PostgresFixture.MasterUserEmail));
    }
}
