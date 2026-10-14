using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Query.Handlers;
using ArturRios.Heimdall.Query.Input;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Heimdall.Shared.Security;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Mock;
using Moq;
using Application = ArturRios.Heimdall.Domain.Entities.Application;

namespace ArturRios.Heimdall.Query.Tests;

// Unit tests for CheckApplicationTokenQueryHandler (UC-57): a token the validator accepts is still
// refused unless its application and scope are live, the scope matches, and the issuing credential
// is live — so revocation, expiry and deletion take effect at the next re-check (AF-57a, one answer).
public class CheckApplicationTokenQueryHandlerTests
{
    private readonly AsyncFakeRepository<ApplicationCredential, long> _credentials = new();
    private readonly Mock<IApplicationTokenValidator> _validator = new();
    private readonly DateTime _expiresAt = DateTime.UtcNow.AddMinutes(10);

    private CheckApplicationTokenQueryHandler Handler() => new(_validator.Object, _credentials);

    private async Task<ApplicationCredential> SeedAsync(
        bool applicationDeleted = false, bool scopeDeleted = false, DateTime? expiresAt = null, DateTime? revokedAt = null)
    {
        var scope = new Scope { Id = 1, PublicId = Guid.NewGuid(), Name = "Acme", IsDeleted = scopeDeleted };
        var application = new Application
        {
            Id = 1, PublicId = Guid.NewGuid(), Name = "Billing Service", IsDeleted = applicationDeleted,
            ScopeId = scope.Id, Scope = scope
        };
        var credential = new ApplicationCredential
        {
            Id = 1, ApplicationId = application.Id, Application = application, SecretHash = "h",
            ExpiresAt = expiresAt, RevokedAt = revokedAt
        };
        await _credentials.CreateAsync(credential);
        return credential;
    }

    private void TokenSays(ApplicationCredential credential, Guid? scopeId = null) =>
        _validator.Setup(v => v.ValidateAsync("token")).ReturnsAsync(new ApplicationTokenPrincipal(
            credential.Application.PublicId, scopeId ?? credential.Application.Scope.PublicId,
            credential.PublicId, _expiresAt));

    [UnitFact]
    public async Task GivenAValidTokenOfALiveCredential_WhenChecking_ThenTheApplicationIsReturned()
    {
        // Given
        var credential = await SeedAsync();
        TokenSays(credential);

        // When
        var output = await Handler().HandleAsync(new CheckApplicationTokenQuery { Token = "token" });

        // Then
        Assert.True(output.Success);
        Assert.Equal(ApplicationTokenMessages.TokenAccepted, output.Messages.Single());
        Assert.Equal(credential.Application.PublicId, output.Data!.ApplicationId);
        Assert.Equal("Billing Service", output.Data.Name);
        Assert.Equal(credential.Application.Scope.PublicId, output.Data.ScopeId);
        Assert.Equal(credential.PublicId, output.Data.CredentialId);
        Assert.Equal(_expiresAt, output.Data.ExpiresAt);
    }

    [UnitFact]
    public async Task GivenATokenTheValidatorRefuses_WhenChecking_ThenNotAccepted()
    {
        // Given
        await SeedAsync();
        _validator.Setup(v => v.ValidateAsync(It.IsAny<string?>())).ReturnsAsync((ApplicationTokenPrincipal?)null);

        // When
        var output = await Handler().HandleAsync(new CheckApplicationTokenQuery { Token = "token" });

        // Then
        Assert.Equal([ApplicationTokenMessages.TokenNotAccepted], output.Errors);
    }

    [UnitTheory]
    [InlineData("revoked")]
    [InlineData("expired")]
    [InlineData("application-deleted")]
    [InlineData("scope-deleted")]
    [InlineData("other-scope")]
    public async Task GivenAValidTokenThatIsNoLongerBackedByLiveData_WhenChecking_ThenNotAccepted(string state)
    {
        // Given
        var credential = await SeedAsync(
            applicationDeleted: state == "application-deleted",
            scopeDeleted: state == "scope-deleted",
            expiresAt: state == "expired" ? DateTime.UtcNow.AddSeconds(-1) : null,
            revokedAt: state == "revoked" ? DateTime.UtcNow.AddSeconds(-1) : null);
        TokenSays(credential, scopeId: state == "other-scope" ? Guid.NewGuid() : null);

        // When
        var output = await Handler().HandleAsync(new CheckApplicationTokenQuery { Token = "token" });

        // Then
        Assert.Equal([ApplicationTokenMessages.TokenNotAccepted], output.Errors);
        Assert.Null(output.Data);
    }
}
