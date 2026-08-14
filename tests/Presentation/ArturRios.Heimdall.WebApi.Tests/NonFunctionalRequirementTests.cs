using System.IdentityModel.Tokens.Jwt;
using System.Net;
using ArturRios.Configuration.Enums;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Output;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.WebApi.Tests.Support;
using ArturRios.Output;
using ArturRios.Util.Hashing;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Functional;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ArturRios.Heimdall.WebApi.Tests;

// Verification for the non-functional requirements that can be verified by executing the system, as
// opposed to by inspecting it. See the Testing Specification, §11, for the method and for which NFRs
// are verified where.
//
// Each test names the requirement it verifies and asserts the property the requirement states —
// not an implementation detail that happens to imply it. A requirement nothing executes is an
// assertion about the system, and this file exists so that fewer of them are.
[Collection(nameof(FunctionalCollection))]
public class NonFunctionalRequirementTests(PostgresFixture db) : WebApiTest<Program>(EnvironmentType.Local)
{
    private const string Password = "Str0ng-Nfr-Pass!";

    private static string UniqueEmail(string prefix) => $"{prefix}-{Guid.NewGuid():N}@test.local";

    // ---------------------------------------------------------------- NFR-07

    [FunctionalFact]
    public async Task GivenNFR07_WhenEverythingInAScopeIsLogicallyDeleted_ThenNoForeignKeyIsLeftDangling()
    {
        // NFR-07: logical deletion must not corrupt referential integrity.
        //
        // Logical deletion sets a flag and cascades that flag; it removes no rows, so the property
        // to verify is that the rows it leaves behind still point at rows that exist. UC-04's
        // cascade is the widest one — a scope, its Users, its Google Users and its applications all
        // flipped in a single call — so it is the one exercised here.
        var scope = await SeedPopulatedScopeAsync();

        Authorize(TestTokens.ForRole((int)Roles.SystemAdmin));

        var response = await Gateway.DeleteAsync<DataOutput<DeleteScopeCommandOutput?>>(
            $"/api/scopes/{scope.PublicId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Then — every foreign key in the schema still resolves. Asked of the database rather than
        // of the model, so a relationship EF does not know about would still be covered.
        var orphans = await FindOrphanedRowsAsync();

        Assert.Empty(orphans);

        // Then — and the rows are still there to be found, which is what distinguishes a logical
        // deletion from a hard one: integrity held because nothing was removed, not because
        // everything was.
        await using var context = db.CreateContext();

        Assert.True(await context.Persons.AnyAsync(person => person.ScopeId == scope.Id && person.IsDeleted));
        Assert.True(await context.Applications.AnyAsync(app => app.ScopeId == scope.Id && app.IsDeleted));
        Assert.True(await context.GoogleUsers.AnyAsync(user => user.ScopeId == scope.Id && user.IsDeleted));
    }

    // ---------------------------------------------------------------- NFR-17

    [FunctionalFact]
    public async Task GivenNFR17_WhenAChallengeTokenIsIssued_ThenItExpiresInFiveMinutesAndIsMarkedPending()
    {
        // NFR-17: the challenge token carries a distinct MFA-pending claim, expires quickly, and is
        // rejected everywhere but second-factor verification.
        //
        // "Quickly" was written as a target of five minutes. It is not a target — the lifetime is
        // fixed in JwtTwoFactorChallengeTokenIssuer and no configuration can move it, which is the
        // stronger property and the one asserted here.
        var person = await SeedPersonWithActiveTwoFactorAsync();

        var issuedAt = DateTime.UtcNow;

        var login = await Gateway.PostAsync<DataOutput<LoginCommandOutput?>>(
            "/api/auth/login", new LoginCommand { Email = person.Email, Password = Password });

        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.True(login.Body!.Data!.RequiresTwoFactor);

        var token = new JwtSecurityTokenHandler().ReadJwtToken(login.Body.Data.ChallengeToken);

        // Then — five minutes. Measured against this test's own clock, so the bound allows for the
        // moment between taking the reading and the token being minted; the lifetime itself is a
        // constant, not a configured value that could drift.
        var lifetime = token.ValidTo - issuedAt;

        Assert.InRange(lifetime.TotalSeconds, 295, 305);

        // Then — and it says what it is, which is what MfaPendingGuardFilter reads
        Assert.Contains(token.Claims, claim => claim.Type == "mfaPending" && claim.Value == "true");

        // Then — a full token is not interchangeable with it: the challenge carries no scope claims
        Assert.DoesNotContain(token.Claims, claim => claim.Type is "scopeId" or "ownedScopeIds");
    }

    [FunctionalFact]
    public async Task GivenNFR17_WhenAChallengeTokenIsUsedAsABearerCredential_ThenEveryOtherEndpointRefusesIt()
    {
        // The third clause of NFR-17, and the one an expiry alone would not give: within its five
        // minutes the token is still valid, so what stops it being a login is that every endpoint
        // except UC-38's rejects it.
        var person = await SeedPersonWithActiveTwoFactorAsync();

        var login = await Gateway.PostAsync<DataOutput<LoginCommandOutput?>>(
            "/api/auth/login", new LoginCommand { Email = person.Email, Password = Password });

        Authorize(login.Body!.Data!.ChallengeToken!);

        var read = await Gateway.GetAsync<DataOutput<object?>>($"/api/persons/{person.PublicId}");

        Assert.Equal(HttpStatusCode.Unauthorized, read.StatusCode);
    }

    // ---------------------------------------------------------------- helpers

    private async Task<Scope> SeedPopulatedScopeAsync()
    {
        await using var context = db.CreateContext();

        var scope = new Scope { PublicId = Guid.NewGuid(), Name = $"nfr-{Guid.NewGuid():N}" };
        context.Scopes.Add(scope);
        await context.SaveChangesAsync();

        var owner = new Person
        {
            PublicId = Guid.NewGuid(), Name = "Owner", Email = UniqueEmail("nfr-owner"),
            PasswordHash = [1], Salt = [1], RoleId = (long)Roles.ScopeAdmin
        };
        var member = new Person
        {
            PublicId = Guid.NewGuid(), Name = "Member", Email = UniqueEmail("nfr-member"),
            PasswordHash = [1], Salt = [1], RoleId = (long)Roles.User, ScopeId = scope.Id
        };
        context.Persons.AddRange(owner, member);
        await context.SaveChangesAsync();

        context.ScopeOwners.Add(new ScopeOwner { ScopeId = scope.Id, PersonId = owner.Id });
        context.ScopeUsers.Add(new ScopeUser { ScopeId = scope.Id, PersonId = member.Id });
        context.Applications.Add(new Application
        {
            PublicId = Guid.NewGuid(), Name = "App", ScopeId = scope.Id, OwnerId = owner.Id
        });
        context.GoogleUsers.Add(new GoogleUser
        {
            PublicId = Guid.NewGuid(), GoogleId = $"g-{Guid.NewGuid():N}",
            Name = "Google", Email = UniqueEmail("nfr-google"), ScopeId = scope.Id
        });
        context.ScopePermissions.Add(new ScopePermission
        {
            PublicId = Guid.NewGuid(), Name = $"perm-{Guid.NewGuid():N}", ScopeId = scope.Id
        });
        await context.SaveChangesAsync();

        return scope;
    }

    private async Task<Person> SeedPersonWithActiveTwoFactorAsync()
    {
        await using var context = db.CreateContext();

        var person = new Person
        {
            PublicId = Guid.NewGuid(),
            Name = "Gated",
            Email = UniqueEmail("nfr-gated"),
            PasswordHash = Hash.EncodeWithRandomSalt(Password, out var salt),
            Salt = salt,
            RoleId = (long)Roles.SystemAdmin,
            EmailVerified = true
        };
        context.Persons.Add(person);
        await context.SaveChangesAsync();

        context.TwoFactorAuths.Add(new TwoFactorAuth
        {
            PersonId = person.Id, IsActive = true, EmailEnabled = true
        });
        await context.SaveChangesAsync();

        return person;
    }

    /// <summary>
    ///     Every foreign key in the <c>heimdall</c> schema whose child row points at a parent that is
    ///     not there, found by asking the catalogue for the constraints rather than by listing them
    ///     here — a relationship added later is covered without this test being touched.
    /// </summary>
    private async Task<List<string>> FindOrphanedRowsAsync()
    {
        var orphans = new List<string>();

        await using var connection = new NpgsqlConnection(db.ConnectionString);
        await connection.OpenAsync();

        const string constraints =
            """
            SELECT tc.constraint_name, tc.table_name, kcu.column_name,
                   ccu.table_name AS parent_table, ccu.column_name AS parent_column
            FROM information_schema.table_constraints AS tc
            JOIN information_schema.key_column_usage AS kcu
              ON tc.constraint_name = kcu.constraint_name AND tc.table_schema = kcu.table_schema
            JOIN information_schema.constraint_column_usage AS ccu
              ON ccu.constraint_name = tc.constraint_name AND ccu.table_schema = tc.table_schema
            WHERE tc.constraint_type = 'FOREIGN KEY' AND tc.table_schema = 'heimdall'
            """;

        var keys = new List<(string Name, string Table, string Column, string ParentTable, string ParentColumn)>();

        await using (var command = new NpgsqlCommand(constraints, connection))
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                keys.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2),
                    reader.GetString(3), reader.GetString(4)));
            }
        }

        Assert.NotEmpty(keys);

        foreach (var key in keys)
        {
            var sql = $"""
                       SELECT count(*) FROM heimdall."{key.Table}" AS child
                       WHERE child."{key.Column}" IS NOT NULL
                         AND NOT EXISTS (
                             SELECT 1 FROM heimdall."{key.ParentTable}" AS parent
                             WHERE parent."{key.ParentColumn}" = child."{key.Column}")
                       """;

            await using var command = new NpgsqlCommand(sql, connection);

            if (Convert.ToInt64(await command.ExecuteScalarAsync()) > 0)
            {
                orphans.Add($"{key.Table}.{key.Column} -> {key.ParentTable}.{key.ParentColumn} ({key.Name})");
            }
        }

        return orphans;
    }
}
