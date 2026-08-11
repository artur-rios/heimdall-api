using System.Net;
using ArturRios.Configuration.Enums;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Output;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.WebApi.Security;
using ArturRios.Heimdall.WebApi.Tests.Support;
using ArturRios.Output;
using ArturRios.Util.Hashing;
using ArturRios.Util.Http;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Functional;
using ArturRios.Util.WebApi.Security.Authentication;
using ArturRios.Util.WebApi.Security.Constants;

namespace ArturRios.Heimdall.WebApi.Tests;

// Functional tests for the JWT-claim integration of UC-31…UC-35 (FR-SP): at login, the issued token
// carries the names of the acting scope's permissions whose IncludeAsJwtClaim flag is set, omits the
// ones whose flag is clear, omits logically deleted permissions, and carries none for a System Admin
// (who belongs to no scope). A Scope Admin's token carries the union over every scope they own.
[Collection(nameof(FunctionalCollection))]
public class AuthControllerScopePermissionClaimTests(PostgresFixture db)
    : WebApiTest<Program>(EnvironmentType.Local)
{
    private const string Password = "Str0ng-Login-Pass!";

    private static string UniqueEmail(string prefix) => $"{prefix}-{Guid.NewGuid():N}@test.local";

    private async Task<Scope> SeedScopeAsync(bool isDeleted = false)
    {
        await using var context = db.CreateContext();
        var scope = new Scope
        {
            PublicId = Guid.NewGuid(), Name = $"scope-{Guid.NewGuid():N}", IsDeleted = isDeleted
        };
        context.Scopes.Add(scope);
        await context.SaveChangesAsync();
        return scope;
    }

    private async Task<Person> SeedPersonAsync(
        Roles role, string email, bool isDeleted = false, string password = Password)
    {
        await using var context = db.CreateContext();
        var person = new Person
        {
            PublicId = Guid.NewGuid(),
            Name = $"{role}",
            Email = email,
            PasswordHash = Hash.EncodeWithRandomSalt(password, out var salt),
            Salt = salt,
            RoleId = (long)role,
            EmailVerified = true,
            IsDeleted = isDeleted
        };
        context.Persons.Add(person);
        await context.SaveChangesAsync();
        return person;
    }

    private async Task<Person> SeedUserAsync(Scope scope, string email, bool isDeleted = false)
    {
        var person = await SeedPersonAsync(Roles.User, email, isDeleted);

        await using var context = db.CreateContext();
        context.ScopeUsers.Add(new ScopeUser { ScopeId = scope.Id, PersonId = person.Id });
        await context.SaveChangesAsync();

        return person;
    }

    private async Task<Person> SeedScopeAdminAsync(string email, params Scope[] owned)
    {
        var person = await SeedPersonAsync(Roles.ScopeAdmin, email);

        await using var context = db.CreateContext();
        context.ScopeOwners.AddRange(
            owned.Select(scope => new ScopeOwner { ScopeId = scope.Id, PersonId = person.Id }));
        await context.SaveChangesAsync();

        return person;
    }

    private async Task SeedScopePermissionAsync(
        Scope scope, string name, bool includeAsJwtClaim, bool isDeleted = false)
    {
        await using var context = db.CreateContext();
        context.ScopePermissions.Add(new ScopePermission
        {
            PublicId = Guid.NewGuid(),
            Name = name,
            Description = "A test permission.",
            IncludeAsJwtClaim = includeAsJwtClaim,
            IsDeleted = isDeleted,
            ScopeId = scope.Id
        });
        await context.SaveChangesAsync();
    }

    private Task<HttpOutput<DataOutput<LoginCommandOutput?>?>> LoginAsync(
        string email, string password = Password, Guid? scopeId = null) =>
        Gateway.PostAsync<DataOutput<LoginCommandOutput?>>(
            "/api/auth/login",
            new LoginCommand { Email = email, Password = password, ScopeId = scopeId });

    /// <summary>Reads the identity out of an issued token, to assert on its permission claims.</summary>
    private static IdentityUser ClaimsOf(string token) =>
        (IdentityUser)new IdentityUserMapper().FromClaims(TokenClaimsReader.Read(token)!)!;

    [FunctionalFact]
    public async Task GivenUserOfScopeWithFlaggedAndUnflaggedPermissions_WhenPostLogin_ThenTokenCarriesOnlyFlaggedNames()
    {
        // Given a live scope with one flagged and one unflagged permission, and a User of that scope
        var scope = await SeedScopeAsync();
        var flagged = $"documents.read-{Guid.NewGuid():N}";
        var unflagged = $"documents.write-{Guid.NewGuid():N}";
        await SeedScopePermissionAsync(scope, flagged, includeAsJwtClaim: true);
        await SeedScopePermissionAsync(scope, unflagged, includeAsJwtClaim: false);
        var email = UniqueEmail("user");
        await SeedUserAsync(scope, email);

        // When
        var response = await LoginAsync(email, scopeId: scope.PublicId);

        // Then — the issued token carries only the flagged permission's name
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var claims = ClaimsOf(response.Body!.Data!.Token);
        Assert.Contains(flagged, claims.ScopePermissionClaims);
        Assert.DoesNotContain(unflagged, claims.ScopePermissionClaims);
    }

    [FunctionalFact]
    public async Task GivenScopeAdminOwningScopesWithFlaggedPermissions_WhenPostLogin_ThenTokenCarriesAllOwnedScopesFlaggedNames()
    {
        // Given a Scope Admin owning two scopes, each with one flagged permission
        var first = await SeedScopeAsync();
        var second = await SeedScopeAsync();
        var firstPermission = $"first.read-{Guid.NewGuid():N}";
        var secondPermission = $"second.read-{Guid.NewGuid():N}";
        await SeedScopePermissionAsync(first, firstPermission, includeAsJwtClaim: true);
        await SeedScopePermissionAsync(second, secondPermission, includeAsJwtClaim: true);
        var email = UniqueEmail("admin");
        await SeedScopeAdminAsync(email, first, second);

        // When
        var response = await LoginAsync(email);

        // Then — the token carries the flagged names from every owned scope
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var claims = ClaimsOf(response.Body!.Data!.Token);
        Assert.Contains(firstPermission, claims.ScopePermissionClaims);
        Assert.Contains(secondPermission, claims.ScopePermissionClaims);
    }

    [FunctionalFact]
    public async Task GivenFlaggedButLogicallyDeletedPermission_WhenPostLogin_ThenTokenDoesNotCarryIt()
    {
        // Given a flagged permission that has been logically deleted (FR-SP-09)
        var scope = await SeedScopeAsync();
        var deleted = $"deleted.read-{Guid.NewGuid():N}";
        await SeedScopePermissionAsync(scope, deleted, includeAsJwtClaim: true, isDeleted: true);
        var email = UniqueEmail("user");
        await SeedUserAsync(scope, email);

        // When
        var response = await LoginAsync(email, scopeId: scope.PublicId);

        // Then — a deleted permission is not emitted, even when its flag is set
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain(deleted, ClaimsOf(response.Body!.Data!.Token).ScopePermissionClaims);
    }

    [FunctionalFact]
    public async Task GivenSystemAdmin_WhenPostLogin_ThenTokenCarriesNoScopePermissionClaim()
    {
        // Given the master System Admin the API seeds on start-up, who belongs to no scope
        // When
        var response = await LoginAsync(PostgresFixture.MasterUserEmail, PostgresFixture.MasterUserPassword);

        // Then — a System Admin carries no scope-permission claim
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(ClaimsOf(response.Body!.Data!.Token).ScopePermissionClaims);
    }
}
