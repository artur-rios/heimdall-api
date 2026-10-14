using ArturRios.Heimdall.Command.Handlers;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Tests.Support;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Domain.Persistence;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Mock;
using Moq;
using Application = ArturRios.Heimdall.Domain.Entities.Application;

namespace ArturRios.Heimdall.Command.Tests;

// Unit tests for RevokeApplicationCredentialCommandHandler (UC-55): the main flow stamps RevokedAt
// and returns the credential as the audit target; AF-55a (no live application in the scope), AF-55b
// (a Scope Admin who does not own it), AF-55c (no such credential on this application — including one
// of another application) and AF-55d (already revoked, including by a revocation that lands between
// the read and the write). An expired credential can still be revoked.
public class RevokeApplicationCredentialCommandHandlerTests
{
    private readonly AsyncFakeRepository<Application, long> _applications = new();
    private readonly AsyncFakeRepository<ApplicationCredential, long> _credentials = new();

    private RevokeApplicationCredentialCommandHandler Handler(IAtomicWrites? atomicWrites = null) =>
        new(_applications, _credentials, atomicWrites ?? InMemoryAtomicWrites.Instance);

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

    private static RevokeApplicationCredentialCommand Command(
        Application application, Guid credentialId, int role = (int)Roles.SystemAdmin, Guid? actor = null) => new()
    {
        ScopeId = application.Scope.PublicId, Id = application.PublicId, CredentialId = credentialId,
        ActingRole = role, ActingPersonId = actor ?? Guid.NewGuid()
    };

    [UnitFact]
    public async Task GivenALiveCredential_WhenRevoking_ThenRevokedAtIsStampedAndItIsTheTarget()
    {
        // Given
        var application = await SeedApplicationAsync();
        var credential = new ApplicationCredential { Id = 1, ApplicationId = application.Id, SecretHash = "a", CreatedAt = DateTime.UtcNow };
        await _credentials.CreateAsync(credential);

        // When
        var output = await Handler().HandleAsync(Command(application, credential.PublicId));

        // Then
        Assert.True(output.Success);
        Assert.Equal(ApplicationCredentialMessages.CredentialRevoked, output.Messages.Single());
        Assert.Equal(credential.PublicId, output.Data!.Id);
        var stored = Assert.Single(_credentials.Query());
        Assert.NotNull(stored.RevokedAt);
        Assert.Equal(stored.RevokedAt, output.Data.RevokedAt);
    }

    [UnitFact]
    public async Task GivenAnExpiredCredential_WhenRevoking_ThenItIsRevoked()
    {
        // Given
        var application = await SeedApplicationAsync();
        var credential = new ApplicationCredential { Id = 1, ApplicationId = application.Id, SecretHash = "a", ExpiresAt = DateTime.UtcNow.AddDays(-1) };
        await _credentials.CreateAsync(credential);

        // When
        var output = await Handler().HandleAsync(Command(application, credential.PublicId));

        // Then
        Assert.True(output.Success);
    }

    [UnitFact]
    public async Task GivenARevokedCredential_WhenRevoking_ThenAlreadyRevokedAndTheStampIsKept()
    {
        // Given
        var application = await SeedApplicationAsync();
        var revokedAt = DateTime.UtcNow.AddDays(-1);
        var credential = new ApplicationCredential { Id = 1, ApplicationId = application.Id, SecretHash = "a", RevokedAt = revokedAt };
        await _credentials.CreateAsync(credential);

        // When
        var output = await Handler().HandleAsync(Command(application, credential.PublicId));

        // Then
        Assert.Equal(ApplicationCredentialMessages.CredentialAlreadyRevoked, output.Errors.Single());
        Assert.Equal(revokedAt, Assert.Single(_credentials.Query()).RevokedAt);
    }

    [UnitFact]
    public async Task GivenACredentialOfAnotherApplication_WhenRevoking_ThenCredentialNotFound()
    {
        // Given
        var application = await SeedApplicationAsync();
        var other = await SeedApplicationAsync(id: 11);
        var credential = new ApplicationCredential { Id = 1, ApplicationId = other.Id, SecretHash = "a" };
        await _credentials.CreateAsync(credential);

        // When
        var output = await Handler().HandleAsync(Command(application, credential.PublicId));

        // Then
        Assert.Equal(ApplicationCredentialMessages.CredentialNotFound, output.Errors.Single());
        Assert.Null(Assert.Single(_credentials.Query()).RevokedAt);
    }

    [UnitFact]
    public async Task GivenTheWrongScope_WhenRevoking_ThenApplicationNotFound()
    {
        // Given
        var application = await SeedApplicationAsync();
        var command = Command(application, Guid.NewGuid());
        command.ScopeId = Guid.NewGuid();

        // When
        var output = await Handler().HandleAsync(command);

        // Then
        Assert.Equal(ApplicationMessages.ApplicationNotFound, output.Errors.Single());
    }

    [UnitFact]
    public async Task GivenAScopeAdminWhoDoesNotOwnIt_WhenRevoking_ThenNotAuthorized()
    {
        // Given
        var application = await SeedApplicationAsync();
        var credential = new ApplicationCredential { Id = 1, ApplicationId = application.Id, SecretHash = "a" };
        await _credentials.CreateAsync(credential);

        // When
        var output = await Handler().HandleAsync(Command(application, credential.PublicId, (int)Roles.ScopeAdmin));

        // Then
        Assert.Equal(ApplicationCredentialMessages.NotAuthorizedToManageCredentials, output.Errors.Single());
        Assert.Null(Assert.Single(_credentials.Query()).RevokedAt);
    }

    [UnitFact]
    public async Task GivenARevocationThatLandedAfterTheRead_WhenRevoking_ThenAlreadyRevoked()
    {
        // Given — read live, but the conditional write finds it already revoked
        var application = await SeedApplicationAsync();
        var credential = new ApplicationCredential { Id = 1, ApplicationId = application.Id, SecretHash = "a" };
        await _credentials.CreateAsync(credential);
        var atomicWrites = new Mock<IAtomicWrites>();
        atomicWrites.Setup(w => w.TryRevokeCredentialAsync(credential, It.IsAny<DateTime>())).ReturnsAsync(false);

        // When
        var output = await Handler(atomicWrites.Object).HandleAsync(Command(application, credential.PublicId));

        // Then
        Assert.Equal(ApplicationCredentialMessages.CredentialAlreadyRevoked, output.Errors.Single());
        Assert.Null(output.Data);
    }
}
