using ArturRios.Heimdall.Command.Handlers;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Input.Validation;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Heimdall.Shared.Services;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Mock;
using Moq;

namespace ArturRios.Heimdall.Command.Tests;

// Unit tests for SetSelfRegistrationCommandHandler (UC-47 toggle, FR-SR-02): the same rules as UC-24's
// Google toggle — the value must be stated, the scope must be live, a Scope Admin must own it.
public class SetSelfRegistrationCommandHandlerTests
{
    private static IScopeOwnershipChecker OwnershipChecker(bool allowed = true)
    {
        var checker = new Mock<IScopeOwnershipChecker>();
        checker
            .Setup(c => c.ActorMayManageScopeAsync(It.IsAny<int>(), It.IsAny<Guid>(), It.IsAny<long>()))
            .ReturnsAsync(allowed);
        return checker.Object;
    }

    private static async Task<(AsyncFakeRepository<Scope, long> scopes, Scope scope)> ScopeStoreAsync(
        bool isDeleted = false, bool selfRegistrationEnabled = false)
    {
        var scopes = new AsyncFakeRepository<Scope, long>();
        var scope = new Scope
        {
            PublicId = Guid.NewGuid(), Name = "Acme", IsDeleted = isDeleted,
            SelfRegistrationEnabled = selfRegistrationEnabled
        };
        await scopes.CreateAsync(scope);
        return (scopes, scope);
    }

    private static SetSelfRegistrationCommandHandler Handler(
        AsyncFakeRepository<Scope, long> scopes, bool ownershipAllowed = true) =>
        new(new SetSelfRegistrationCommandValidator(), scopes, scopes, OwnershipChecker(ownershipAllowed));

    private static SetSelfRegistrationCommand Command(Guid scopeId, bool? enabled) => new()
    {
        Id = scopeId, Enabled = enabled, ActingRole = (int)Roles.SystemAdmin, ActingPersonId = Guid.NewGuid()
    };

    [UnitFact]
    public async Task GivenAClosedScope_WhenEnabling_ThenFlagIsOnAndReturned()
    {
        var (scopes, scope) = await ScopeStoreAsync();

        var output = await Handler(scopes).HandleAsync(Command(scope.PublicId, enabled: true));

        Assert.True(output.Success);
        Assert.Contains(ScopeMessages.SelfRegistrationUpdatedSuccessfully, output.Messages);
        Assert.True(output.Data!.SelfRegistrationEnabled);
        Assert.True(scopes.Query().Single().SelfRegistrationEnabled);
    }

    [UnitFact]
    public async Task GivenAnOwningScopeAdmin_WhenEnabling_ThenFlagIsOn()
    {
        var (scopes, scope) = await ScopeStoreAsync();
        var command = Command(scope.PublicId, enabled: true);
        command.ActingRole = (int)Roles.ScopeAdmin;

        var output = await Handler(scopes, ownershipAllowed: true).HandleAsync(command);

        Assert.True(output.Success);
        Assert.True(scopes.Query().Single().SelfRegistrationEnabled);
    }

    [UnitFact]
    public async Task GivenAnOpenScope_WhenDisabling_ThenFlagIsOff()
    {
        var (scopes, scope) = await ScopeStoreAsync(selfRegistrationEnabled: true);

        var output = await Handler(scopes).HandleAsync(Command(scope.PublicId, enabled: false));

        Assert.True(output.Success);
        Assert.False(scopes.Query().Single().SelfRegistrationEnabled);
    }

    [UnitFact]
    public async Task GivenNoEnabledValue_WhenHandling_ThenEnabledRequiredAndFlagUnchanged()
    {
        var (scopes, scope) = await ScopeStoreAsync(selfRegistrationEnabled: true);

        var output = await Handler(scopes).HandleAsync(Command(scope.PublicId, enabled: null));

        Assert.False(output.Success);
        Assert.Contains(ScopeMessages.EnabledRequired, output.Errors);
        Assert.True(scopes.Query().Single().SelfRegistrationEnabled);
    }

    [UnitFact]
    public async Task GivenADeletedScope_WhenHandling_ThenScopeNotFound()
    {
        var (scopes, scope) = await ScopeStoreAsync(isDeleted: true);

        var output = await Handler(scopes).HandleAsync(Command(scope.PublicId, enabled: true));

        Assert.Contains(ScopeMessages.ScopeNotFound, output.Errors);
        Assert.False(scopes.Query().Single().SelfRegistrationEnabled);
    }

    [UnitFact]
    public async Task GivenAScopeAdminWhoDoesNotOwnTheScope_WhenHandling_ThenNotScopeOwnerAndFlagUnchanged()
    {
        var (scopes, scope) = await ScopeStoreAsync();

        var output = await Handler(scopes, ownershipAllowed: false)
            .HandleAsync(Command(scope.PublicId, enabled: true));

        Assert.Contains(ScopeMessages.NotScopeOwner, output.Errors);
        Assert.False(scopes.Query().Single().SelfRegistrationEnabled);
    }
}
