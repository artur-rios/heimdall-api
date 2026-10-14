using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Query.Handlers;
using ArturRios.Heimdall.Query.Input;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Mock;
using Application = ArturRios.Heimdall.Domain.Entities.Application;

namespace ArturRios.Heimdall.Query.Tests;

// Unit tests for ListApplicationCredentialsQueryHandler (UC-54): every credential of the application,
// newest first, each with whether it is live — never a secret or a hash — plus AF-54a (no live
// application in the scope) and AF-54b (a Scope Admin who does not own it).
public class ListApplicationCredentialsQueryHandlerTests
{
    private readonly AsyncFakeRepository<Application, long> _applications = new();
    private readonly AsyncFakeRepository<ApplicationCredential, long> _credentials = new();

    private ListApplicationCredentialsQueryHandler Handler() => new(_applications, _credentials);

    private async Task<Application> SeedApplicationAsync(long id = 10)
    {
        var scope = new Scope { Id = id, PublicId = Guid.NewGuid(), Name = "Acme" };
        var owner = new Person
        {
            Id = id, PublicId = Guid.NewGuid(), Name = "Owner", Email = $"owner-{id}@test.local",
            RoleId = (long)Roles.ScopeAdmin, ScopeOwnerships = [new ScopeOwner { ScopeId = scope.Id, Scope = scope }]
        };
        var application = new Application
        {
            Id = id, PublicId = Guid.NewGuid(), Name = "Billing", ScopeId = scope.Id, Scope = scope,
            OwnerId = owner.Id, Owner = owner
        };
        await _applications.CreateAsync(application);
        return application;
    }

    private static ListApplicationCredentialsQuery Query(Application application, int role = (int)Roles.SystemAdmin, Guid? actor = null) => new()
    {
        ScopeId = application.Scope.PublicId, Id = application.PublicId,
        ActingRole = role, ActingPersonId = actor ?? Guid.NewGuid()
    };

    [UnitFact]
    public async Task GivenCredentials_WhenListing_ThenNewestFirstWithLivenessAndNoSecret()
    {
        // Given — one live, one revoked, one expired, and one of another application
        var application = await SeedApplicationAsync();
        var other = await SeedApplicationAsync(id: 11);
        var now = DateTime.UtcNow;
        var oldest = new ApplicationCredential { Id = 1, ApplicationId = application.Id, SecretHash = "a", CreatedAt = now.AddDays(-3), RevokedAt = now.AddDays(-1) };
        var middle = new ApplicationCredential { Id = 2, ApplicationId = application.Id, SecretHash = "b", CreatedAt = now.AddDays(-2), ExpiresAt = now.AddDays(-1) };
        var newest = new ApplicationCredential { Id = 3, ApplicationId = application.Id, SecretHash = "c", CreatedAt = now.AddDays(-1), LastUsedAt = now };
        await _credentials.CreateAsync(oldest);
        await _credentials.CreateAsync(middle);
        await _credentials.CreateAsync(newest);
        await _credentials.CreateAsync(new ApplicationCredential { Id = 4, ApplicationId = other.Id, SecretHash = "d", CreatedAt = now });

        // When
        var output = await Handler().HandleAsync(Query(application));

        // Then
        Assert.True(output.Success);
        Assert.Equal(ApplicationCredentialMessages.CredentialsRetrieved, output.Messages.Single());
        var listed = output.Data!.Credentials;
        Assert.Equal([newest.PublicId, middle.PublicId, oldest.PublicId], listed.Select(c => c.Id));
        Assert.Equal([true, false, false], listed.Select(c => c.Live));
        Assert.Equal(now, listed[0].LastUsedAt);
        Assert.Equal(oldest.RevokedAt, listed[2].RevokedAt);
    }

    [UnitFact]
    public void GivenTheOutputType_WhenInspected_ThenItCarriesNoSecretOrHash()
    {
        // Given / When
        var properties = typeof(ArturRios.Heimdall.Query.Output.ApplicationCredentialOutput)
            .GetProperties().Select(p => p.Name).ToList();

        // Then
        Assert.DoesNotContain(properties, name => name.Contains("Secret") || name.Contains("Hash"));
    }

    [UnitFact]
    public async Task GivenNoCredentials_WhenListing_ThenAnEmptyList()
    {
        // Given
        var application = await SeedApplicationAsync();

        // When
        var output = await Handler().HandleAsync(Query(application));

        // Then
        Assert.True(output.Success);
        Assert.Empty(output.Data!.Credentials);
    }

    [UnitFact]
    public async Task GivenTheWrongScope_WhenListing_ThenApplicationNotFound()
    {
        // Given
        var application = await SeedApplicationAsync();
        var query = Query(application);
        query.ScopeId = Guid.NewGuid();

        // When
        var output = await Handler().HandleAsync(query);

        // Then
        Assert.Equal(ApplicationMessages.ApplicationNotFound, output.Errors.Single());
    }

    [UnitFact]
    public async Task GivenAScopeAdminWhoDoesNotOwnIt_WhenListing_ThenNotAuthorized()
    {
        // Given
        var application = await SeedApplicationAsync();

        // When
        var output = await Handler().HandleAsync(Query(application, (int)Roles.ScopeAdmin));

        // Then
        Assert.Equal(ApplicationCredentialMessages.NotAuthorizedToManageCredentials, output.Errors.Single());
    }
}
