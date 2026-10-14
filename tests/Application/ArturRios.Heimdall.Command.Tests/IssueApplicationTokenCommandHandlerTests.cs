using ArturRios.Heimdall.Command.Handlers;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Services;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Mock;
using FluentValidation;
using FluentValidation.Results;
using Moq;
using Application = ArturRios.Heimdall.Domain.Entities.Application;

namespace ArturRios.Heimdall.Command.Tests;

// Unit tests for IssueApplicationTokenCommandHandler (UC-56): the main flow issues a token for the
// application, scope and credential, stamps LastUsedAt and returns the application as the audit
// target; every refusal (AF-56b) is the same single message, whichever check failed.
public class IssueApplicationTokenCommandHandlerTests
{
    private readonly AsyncFakeRepository<ApplicationCredential, long> _credentials = new();
    private readonly Mock<IApplicationTokenIssuer> _issuer = new();
    private readonly DateTime _expiresAt = DateTime.UtcNow.AddMinutes(10);

    public IssueApplicationTokenCommandHandlerTests()
    {
        _issuer.Setup(i => i.IssueAsync(It.IsAny<ApplicationTokenSubject>()))
            .ReturnsAsync(new AuthToken("signed.application.token", _expiresAt));
    }

    private IssueApplicationTokenCommandHandler Handler()
    {
        var validator = new Mock<IValidator<IssueApplicationTokenCommand>>();
        validator.Setup(v => v.ValidateAsync(It.IsAny<IssueApplicationTokenCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ValidationResult());

        return new IssueApplicationTokenCommandHandler(validator.Object, _credentials, _credentials, _issuer.Object);
    }

    private async Task<(ApplicationCredential Credential, string Secret)> SeedAsync(
        bool applicationDeleted = false, bool scopeDeleted = false,
        DateTime? expiresAt = null, DateTime? revokedAt = null)
    {
        var scope = new Scope { Id = 1, PublicId = Guid.NewGuid(), Name = "Acme", IsDeleted = scopeDeleted };
        var application = new Application
        {
            Id = 1, PublicId = Guid.NewGuid(), Name = "Billing Service", IsDeleted = applicationDeleted,
            ScopeId = scope.Id, Scope = scope
        };
        var secret = ApplicationCredentialSecret.New();
        var credential = new ApplicationCredential
        {
            Id = 1, ApplicationId = application.Id, Application = application,
            SecretHash = ApplicationCredentialSecret.HashOf(secret), CreatedAt = DateTime.UtcNow.AddDays(-1),
            ExpiresAt = expiresAt, RevokedAt = revokedAt
        };
        await _credentials.CreateAsync(credential);
        return (credential, secret);
    }

    private static IssueApplicationTokenCommand Command(ApplicationCredential credential, string secret) => new()
    {
        ApplicationId = credential.Application.PublicId,
        ScopeId = credential.Application.Scope.PublicId,
        Secret = secret
    };

    [UnitFact]
    public async Task GivenALiveCredential_WhenExchanging_ThenATokenIsIssuedAndLastUsedAtStamped()
    {
        // Given
        var (credential, secret) = await SeedAsync();

        // When
        var output = await Handler().HandleAsync(Command(credential, secret));

        // Then
        Assert.True(output.Success);
        Assert.Equal(ApplicationTokenMessages.TokenIssued, output.Messages.Single());
        Assert.Equal("signed.application.token", output.Data!.Token);
        Assert.Equal(_expiresAt, output.Data.ExpiresAt);
        Assert.Equal(credential.Application.PublicId, output.Data.Id);
        _issuer.Verify(i => i.IssueAsync(new ApplicationTokenSubject(
            credential.Application.PublicId, credential.Application.Scope.PublicId,
            credential.PublicId, "Billing Service")), Times.Once);
        Assert.NotNull(Assert.Single(_credentials.Query()).LastUsedAt);
    }

    [UnitFact]
    public async Task GivenASecretWithSurroundingWhitespace_WhenExchanging_ThenATokenIsIssued()
    {
        // Given — read from a file, with its trailing newline
        var (credential, secret) = await SeedAsync();

        // When
        var output = await Handler().HandleAsync(Command(credential, $"  {secret}\n"));

        // Then
        Assert.True(output.Success);
    }

    public static TheoryData<string> Refusals => new()
    {
        "wrong-secret", "other-application", "other-scope", "revoked", "expired", "application-deleted", "scope-deleted"
    };

    [UnitTheory]
    [MemberData(nameof(Refusals))]
    public async Task GivenAnyRefusal_WhenExchanging_ThenTheSameMessageAndNoToken(string refusal)
    {
        // Given
        var (credential, secret) = await SeedAsync(
            applicationDeleted: refusal == "application-deleted",
            scopeDeleted: refusal == "scope-deleted",
            expiresAt: refusal == "expired" ? DateTime.UtcNow.AddSeconds(-1) : null,
            revokedAt: refusal == "revoked" ? DateTime.UtcNow.AddSeconds(-1) : null);
        var command = Command(credential, secret);
        switch (refusal)
        {
            case "wrong-secret": command.Secret = ApplicationCredentialSecret.New(); break;
            case "other-application": command.ApplicationId = Guid.NewGuid(); break;
            case "other-scope": command.ScopeId = Guid.NewGuid(); break;
        }

        // When
        var output = await Handler().HandleAsync(command);

        // Then
        Assert.False(output.Success);
        Assert.Equal([ApplicationTokenMessages.CredentialsNotAccepted], output.Errors);
        Assert.Null(output.Data);
        _issuer.Verify(i => i.IssueAsync(It.IsAny<ApplicationTokenSubject>()), Times.Never);
        Assert.Null(Assert.Single(_credentials.Query()).LastUsedAt);
    }
}
