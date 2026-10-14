using ArturRios.Heimdall.Command.Handlers;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Services;
using ArturRios.Heimdall.Command.Tests.Support;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Mock;
using FluentValidation;
using FluentValidation.Results;
using Moq;
using Application = ArturRios.Heimdall.Domain.Entities.Application;

namespace ArturRios.Heimdall.Command.Tests;

// Unit tests for IssueApplicationCredentialCommandHandler (UC-53): the main flow for a System Admin
// and for the owning Scope Admin (a secret returned once, only its hash stored), AF-53a (no live
// application in the scope), AF-53b (a Scope Admin who does not own it), AF-53c (validation) and
// AF-53d (two live credentials already — expired and revoked ones do not count).
public class IssueApplicationCredentialCommandHandlerTests
{
    private readonly AsyncFakeRepository<Application, long> _applications = new();
    private readonly AsyncFakeRepository<ApplicationCredential, long> _credentials = new();

    private static Mock<IValidator<IssueApplicationCredentialCommand>> ValidValidator()
    {
        var validator = new Mock<IValidator<IssueApplicationCredentialCommand>>();
        validator
            .Setup(v => v.ValidateAsync(It.IsAny<IssueApplicationCredentialCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ValidationResult());
        return validator;
    }

    private IssueApplicationCredentialCommandHandler Handler(Mock<IValidator<IssueApplicationCredentialCommand>>? validator = null) =>
        new((validator ?? ValidValidator()).Object, _applications, new InMemoryApplicationCredentialSlots(_credentials));

    private async Task<(Application Application, Person Owner)> SeedApplicationAsync(bool isDeleted = false)
    {
        var scope = new Scope { Id = 1, PublicId = Guid.NewGuid(), Name = "Acme" };
        var owner = new Person
        {
            Id = 1, PublicId = Guid.NewGuid(), Name = "Owner", Email = "owner@test.local",
            RoleId = (long)Roles.ScopeAdmin,
            ScopeOwnerships = [new ScopeOwner { ScopeId = scope.Id, Scope = scope }]
        };
        var application = new Application
        {
            Id = 10, PublicId = Guid.NewGuid(), Name = "Billing Service", IsDeleted = isDeleted,
            ScopeId = scope.Id, Scope = scope, OwnerId = owner.Id, Owner = owner
        };
        await _applications.CreateAsync(application);
        return (application, owner);
    }

    private static IssueApplicationCredentialCommand Command(
        Application application, int role, Guid actor, DateTime? expiresAt = null) => new()
    {
        ScopeId = application.Scope.PublicId,
        Id = application.PublicId,
        ExpiresAt = expiresAt,
        ActingRole = role,
        ActingPersonId = actor
    };

    [UnitFact]
    public async Task GivenASystemAdmin_WhenIssuing_ThenASecretIsReturnedAndOnlyItsHashIsStored()
    {
        // Given
        var (application, _) = await SeedApplicationAsync();

        // When
        var output = await Handler().HandleAsync(Command(application, (int)Roles.SystemAdmin, Guid.NewGuid()));

        // Then
        Assert.True(output.Success);
        Assert.Equal(ApplicationCredentialMessages.CredentialIssued, output.Messages.Single());
        var secret = output.Data!.Secret;
        Assert.StartsWith(ApplicationCredentialSecret.Prefix, secret);
        var stored = Assert.Single(_credentials.Query());
        Assert.Equal(application.Id, stored.ApplicationId);
        Assert.Equal(ApplicationCredentialSecret.HashOf(secret), stored.SecretHash);
        Assert.DoesNotContain(secret, stored.SecretHash);
        Assert.Equal(stored.PublicId, output.Data.Id);
        Assert.Null(stored.ExpiresAt);
        Assert.Null(output.Data.ExpiresAt);
    }

    [UnitFact]
    public async Task GivenTheOwningScopeAdminAndAnExpiry_WhenIssuing_ThenTheExpiryIsStoredAsUtc()
    {
        // Given
        var (application, owner) = await SeedApplicationAsync();
        var expiresAt = DateTime.SpecifyKind(new DateTime(2030, 1, 1, 0, 0, 0), DateTimeKind.Unspecified);

        // When
        var output = await Handler().HandleAsync(Command(application, (int)Roles.ScopeAdmin, owner.PublicId, expiresAt));

        // Then
        Assert.True(output.Success);
        var stored = Assert.Single(_credentials.Query());
        Assert.Equal(DateTimeKind.Utc, stored.ExpiresAt!.Value.Kind);
        Assert.Equal(new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc), stored.ExpiresAt);
        Assert.Equal(stored.ExpiresAt, output.Data!.ExpiresAt);
    }

    [UnitFact]
    public async Task GivenNoLiveApplication_WhenIssuing_ThenReturnsApplicationNotFound()
    {
        // Given
        var (application, _) = await SeedApplicationAsync(isDeleted: true);

        // When
        var output = await Handler().HandleAsync(Command(application, (int)Roles.SystemAdmin, Guid.NewGuid()));

        // Then
        Assert.False(output.Success);
        Assert.Equal(ApplicationMessages.ApplicationNotFound, output.Errors.Single());
        Assert.Empty(_credentials.Query());
    }

    [UnitFact]
    public async Task GivenAScopeAdminWhoDoesNotOwnIt_WhenIssuing_ThenReturnsNotAuthorized()
    {
        // Given
        var (application, _) = await SeedApplicationAsync();

        // When
        var output = await Handler().HandleAsync(Command(application, (int)Roles.ScopeAdmin, Guid.NewGuid()));

        // Then
        Assert.False(output.Success);
        Assert.Equal(ApplicationCredentialMessages.NotAuthorizedToManageCredentials, output.Errors.Single());
        Assert.Empty(_credentials.Query());
    }

    [UnitFact]
    public async Task GivenAnInvalidCommand_WhenIssuing_ThenTheValidatorsMessageIsReturned()
    {
        // Given
        var (application, _) = await SeedApplicationAsync();
        var validator = new Mock<IValidator<IssueApplicationCredentialCommand>>();
        validator
            .Setup(v => v.ValidateAsync(It.IsAny<IssueApplicationCredentialCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ValidationResult([new ValidationFailure("ExpiresAt", ApplicationCredentialMessages.ExpiryNotInFuture)]));

        // When
        var output = await Handler(validator).HandleAsync(Command(application, (int)Roles.SystemAdmin, Guid.NewGuid()));

        // Then
        Assert.Equal(ApplicationCredentialMessages.ExpiryNotInFuture, output.Errors.Single());
        Assert.Empty(_credentials.Query());
    }

    [UnitFact]
    public async Task GivenTwoLiveCredentials_WhenIssuing_ThenReturnsLimitReached()
    {
        // Given
        var (application, _) = await SeedApplicationAsync();
        await _credentials.CreateAsync(new ApplicationCredential { Id = 1, ApplicationId = application.Id, SecretHash = "a" });
        await _credentials.CreateAsync(new ApplicationCredential { Id = 2, ApplicationId = application.Id, SecretHash = "b" });

        // When
        var output = await Handler().HandleAsync(Command(application, (int)Roles.SystemAdmin, Guid.NewGuid()));

        // Then
        Assert.Equal(ApplicationCredentialMessages.CredentialLimitReached, output.Errors.Single());
        Assert.Equal(2, _credentials.Query().Count());
    }

    [UnitFact]
    public async Task GivenOneLiveOneRevokedAndOneExpired_WhenIssuing_ThenItSucceeds()
    {
        // Given — only live credentials count against the limit
        var (application, _) = await SeedApplicationAsync();
        await _credentials.CreateAsync(new ApplicationCredential { Id = 1, ApplicationId = application.Id, SecretHash = "a" });
        await _credentials.CreateAsync(new ApplicationCredential
        {
            Id = 2, ApplicationId = application.Id, SecretHash = "b", RevokedAt = DateTime.UtcNow.AddDays(-1)
        });
        await _credentials.CreateAsync(new ApplicationCredential
        {
            Id = 3, ApplicationId = application.Id, SecretHash = "c", ExpiresAt = DateTime.UtcNow.AddDays(-1)
        });

        // When
        var output = await Handler().HandleAsync(Command(application, (int)Roles.SystemAdmin, Guid.NewGuid()));

        // Then
        Assert.True(output.Success);
        Assert.Equal(4, _credentials.Query().Count());
    }
}
