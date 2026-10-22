using ArturRios.Heimdall.Command.Handlers;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Tests.Support;
using ArturRios.Heimdall.Command.Input.Validation;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Heimdall.Shared.Services;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Mock;
using Moq;

namespace ArturRios.Heimdall.Command.Tests;

// Unit tests for SetScopeEmailLinksCommandHandler: the same checks as the self-registration toggle —
// the values are usable, the scope is live, a Scope Admin actor owns it — then both link bases are
// written (a null or empty one clears) and the scope is returned. Nothing else on the scope moves.
public class SetScopeEmailLinksCommandHandlerTests
{
    private const string VerifyUrl = "https://fortuna.test/verify-email";
    private const string ResetUrl = "https://fortuna.test/password-reset";

    private static IScopeOwnershipChecker OwnershipChecker(bool allowed = true)
    {
        var checker = new Mock<IScopeOwnershipChecker>();
        checker
            .Setup(c => c.ActorMayManageScopeAsync(It.IsAny<int>(), It.IsAny<Guid>(), It.IsAny<long>()))
            .ReturnsAsync(allowed);
        return checker.Object;
    }

    private static async Task<(AsyncFakeRepository<Scope, long> scopes, Scope scope)> ScopeStoreAsync(
        bool isDeleted = false, string? verifyUrl = null, string? resetUrl = null)
    {
        var scopes = new AsyncFakeRepository<Scope, long>();
        var scope = new Scope
        {
            PublicId = Guid.NewGuid(), Name = "Acme", Description = "Acme tenant", IsDeleted = isDeleted,
            GoogleSignInEnabled = true, SelfRegistrationEnabled = true, DefaultLegalBasis = 2,
            PrivacyNoticeUri = "https://acme.test/privacy",
            EmailVerificationUrl = verifyUrl, PasswordResetUrl = resetUrl
        };
        await scopes.CreateAsync(scope);
        return (scopes, scope);
    }

    private static SetScopeEmailLinksCommandHandler Handler(
        AsyncFakeRepository<Scope, long> scopes, bool ownershipAllowed = true) =>
        new(new SetScopeEmailLinksCommandValidator(), scopes, InMemoryAtomicWrites.Instance,
            OwnershipChecker(ownershipAllowed));

    private static SetScopeEmailLinksCommand Command(Guid scopeId, string? verifyUrl, string? resetUrl) => new()
    {
        Id = scopeId, EmailVerificationUrl = verifyUrl, PasswordResetUrl = resetUrl,
        ActingRole = (int)Roles.SystemAdmin, ActingPersonId = Guid.NewGuid()
    };

    [UnitFact]
    public async Task GivenAScopeWithNoLinks_WhenSettingBoth_ThenBothAreStoredAndReturned()
    {
        var (scopes, scope) = await ScopeStoreAsync();

        var output = await Handler(scopes).HandleAsync(Command(scope.PublicId, VerifyUrl, ResetUrl));

        Assert.True(output.Success);
        Assert.Contains(ScopeMessages.EmailLinksUpdatedSuccessfully, output.Messages);
        Assert.Equal(VerifyUrl, output.Data!.EmailVerificationUrl);
        Assert.Equal(ResetUrl, output.Data.PasswordResetUrl);
        Assert.Equal(scope.PublicId, output.Data.Id);
        var stored = scopes.Query().Single();
        Assert.Equal(VerifyUrl, stored.EmailVerificationUrl);
        Assert.Equal(ResetUrl, stored.PasswordResetUrl);
    }

    [UnitFact]
    public async Task GivenAScopeWithLinks_WhenSettingNull_ThenBothAreCleared()
    {
        var (scopes, scope) = await ScopeStoreAsync(verifyUrl: VerifyUrl, resetUrl: ResetUrl);

        var output = await Handler(scopes).HandleAsync(Command(scope.PublicId, null, null));

        Assert.True(output.Success);
        Assert.Null(output.Data!.EmailVerificationUrl);
        Assert.Null(output.Data.PasswordResetUrl);
        var stored = scopes.Query().Single();
        Assert.Null(stored.EmailVerificationUrl);
        Assert.Null(stored.PasswordResetUrl);
    }

    [UnitFact]
    public async Task GivenAScopeWithLinks_WhenSettingEmptyStrings_ThenBothAreClearedToNull()
    {
        var (scopes, scope) = await ScopeStoreAsync(verifyUrl: VerifyUrl, resetUrl: ResetUrl);

        var output = await Handler(scopes).HandleAsync(Command(scope.PublicId, "", "  "));

        Assert.True(output.Success);
        var stored = scopes.Query().Single();
        Assert.Null(stored.EmailVerificationUrl);
        Assert.Null(stored.PasswordResetUrl);
    }

    [UnitFact]
    public async Task GivenAScopeWithLinks_WhenSettingOnlyOne_ThenTheOtherIsClearedBecauseTheBodyIsTheWholeState()
    {
        var (scopes, scope) = await ScopeStoreAsync(verifyUrl: VerifyUrl, resetUrl: ResetUrl);

        await Handler(scopes).HandleAsync(Command(scope.PublicId, "https://fortuna.test/v2", null));

        var stored = scopes.Query().Single();
        Assert.Equal("https://fortuna.test/v2", stored.EmailVerificationUrl);
        Assert.Null(stored.PasswordResetUrl);
    }

    [UnitFact]
    public async Task GivenAnOwningScopeAdmin_WhenSettingLinks_ThenTheyAreStored()
    {
        var (scopes, scope) = await ScopeStoreAsync();
        var command = Command(scope.PublicId, VerifyUrl, ResetUrl);
        command.ActingRole = (int)Roles.ScopeAdmin;

        var output = await Handler(scopes, ownershipAllowed: true).HandleAsync(command);

        Assert.True(output.Success);
        Assert.Equal(VerifyUrl, scopes.Query().Single().EmailVerificationUrl);
    }

    [UnitFact]
    public async Task GivenAScopeAdminWhoDoesNotOwnTheScope_WhenSettingLinks_ThenNotScopeOwnerAndLinksUnchanged()
    {
        var (scopes, scope) = await ScopeStoreAsync();

        var output = await Handler(scopes, ownershipAllowed: false)
            .HandleAsync(Command(scope.PublicId, VerifyUrl, ResetUrl));

        Assert.False(output.Success);
        Assert.Contains(ScopeMessages.NotScopeOwner, output.Errors);
        var stored = scopes.Query().Single();
        Assert.Null(stored.EmailVerificationUrl);
        Assert.Null(stored.PasswordResetUrl);
    }

    [UnitFact]
    public async Task GivenADeletedScope_WhenSettingLinks_ThenScopeNotFoundAndLinksUnchanged()
    {
        var (scopes, scope) = await ScopeStoreAsync(isDeleted: true);

        var output = await Handler(scopes).HandleAsync(Command(scope.PublicId, VerifyUrl, ResetUrl));

        Assert.False(output.Success);
        Assert.Contains(ScopeMessages.ScopeNotFound, output.Errors);
        Assert.Null(scopes.Query().Single().EmailVerificationUrl);
    }

    [UnitFact]
    public async Task GivenAnUnknownScope_WhenSettingLinks_ThenScopeNotFound()
    {
        var (scopes, _) = await ScopeStoreAsync();

        var output = await Handler(scopes).HandleAsync(Command(Guid.NewGuid(), VerifyUrl, ResetUrl));

        Assert.Contains(ScopeMessages.ScopeNotFound, output.Errors);
    }

    [UnitFact]
    public async Task GivenAnUnusableLink_WhenSettingLinks_ThenEmailLinkInvalidAndNothingChanges()
    {
        var (scopes, scope) = await ScopeStoreAsync(verifyUrl: VerifyUrl, resetUrl: ResetUrl);

        var output = await Handler(scopes).HandleAsync(Command(scope.PublicId, "/unix/path", ResetUrl));

        Assert.False(output.Success);
        Assert.Contains(ScopeMessages.EmailLinkInvalid, output.Errors);
        var stored = scopes.Query().Single();
        Assert.Equal(VerifyUrl, stored.EmailVerificationUrl);
        Assert.Equal(ResetUrl, stored.PasswordResetUrl);
    }

    [UnitFact]
    public async Task GivenLinks_WhenSettingThem_ThenEveryOtherScopeFieldIsUntouched()
    {
        var (scopes, scope) = await ScopeStoreAsync();

        await Handler(scopes).HandleAsync(Command(scope.PublicId, VerifyUrl, ResetUrl));

        var stored = scopes.Query().Single();
        Assert.Equal("Acme", stored.Name);
        Assert.Equal("Acme tenant", stored.Description);
        Assert.True(stored.GoogleSignInEnabled);
        Assert.True(stored.SelfRegistrationEnabled);
        Assert.Equal(2, stored.DefaultLegalBasis);
        Assert.Equal("https://acme.test/privacy", stored.PrivacyNoticeUri);
        Assert.False(stored.IsDeleted);
    }
}
