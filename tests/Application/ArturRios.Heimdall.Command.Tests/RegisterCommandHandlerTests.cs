using System.Text;
using ArturRios.Heimdall.Command.Handlers;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Input.Validation;
using ArturRios.Heimdall.Command.Output;
using ArturRios.Heimdall.Command.Services;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Output;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Mock;

namespace ArturRios.Heimdall.Command.Tests;

// Unit tests for RegisterCommandHandler (UC-47 request path). Like UC-12's handler, nothing here may
// depend on whether the address is taken: it checks the scope (which says nothing about any address),
// hashes, queues and answers. The address-dependent work is RegistrationProcessor's.
public class RegisterCommandHandlerTests
{
    private const string Password = "a-long-enough-secret";

    private sealed class RecordingQueue(bool accepts = true) : IRegistrationQueue
    {
        public List<RegistrationRequest> Queued { get; } = [];

        public bool TryEnqueue(RegistrationRequest request)
        {
            if (accepts)
            {
                Queued.Add(request);
            }

            return accepts;
        }
    }

    private static async Task<(AsyncFakeRepository<Scope, long> scopes, Scope scope)> ScopeStoreAsync(
        bool open = true, bool isDeleted = false)
    {
        var scopes = new AsyncFakeRepository<Scope, long>();
        var scope = new Scope
        {
            PublicId = Guid.NewGuid(), Name = "Acme", SelfRegistrationEnabled = open, IsDeleted = isDeleted
        };
        await scopes.CreateAsync(scope);
        return (scopes, scope);
    }

    private static RegisterCommand Command(Guid? scopeId, string email = "new@test.local") => new()
    {
        Name = "New Person", Email = email, Password = Password, ScopeId = scopeId
    };

    private static RegisterCommandHandler Handler(AsyncFakeRepository<Scope, long> scopes, IRegistrationQueue queue) =>
        new(new RegisterCommandValidator(), scopes, queue);

    private static void AssertGenericAcceptance(DataOutput<RegisterCommandOutput?> output)
    {
        Assert.True(output.Success);
        Assert.Empty(output.Errors);
        Assert.Contains(AuthMessages.RegistrationRequested, output.Messages);
    }

    [UnitFact]
    public async Task GivenAnOpenScope_WhenRegistering_ThenOneRequestIsQueuedAndAnsweredGenerically()
    {
        var (scopes, scope) = await ScopeStoreAsync();
        var queue = new RecordingQueue();

        var output = await Handler(scopes, queue).HandleAsync(Command(scope.PublicId));

        AssertGenericAcceptance(output);
        var queued = Assert.Single(queue.Queued);
        Assert.Equal(scope.PublicId, queued.ScopeId);
        Assert.Equal("New Person", queued.Name);
        Assert.Equal("new@test.local", queued.Email);
        Assert.NotEmpty(queued.PasswordHash);
        Assert.NotEmpty(queued.Salt);
    }

    [UnitFact]
    public async Task GivenAnOpenScope_WhenRegistering_ThenOnlyAHashThatVerifiesAgainstThePasswordIsQueued()
    {
        var (scopes, scope) = await ScopeStoreAsync();
        var queue = new RecordingQueue();

        await Handler(scopes, queue).HandleAsync(Command(scope.PublicId));

        var queued = Assert.Single(queue.Queued);
        Assert.NotEqual(Encoding.UTF8.GetBytes(Password), queued.PasswordHash);
        Assert.True(await PasswordHashGate.Shared.TextMatchesAsync(Password, queued.PasswordHash, queued.Salt));
    }

    [UnitFact]
    public async Task GivenAnEmailWithSurroundingSpaces_WhenRegistering_ThenItIsQueuedTrimmed()
    {
        var (scopes, scope) = await ScopeStoreAsync();
        var queue = new RecordingQueue();

        await Handler(scopes, queue).HandleAsync(Command(scope.PublicId, email: "  new@test.local "));

        Assert.Equal("new@test.local", Assert.Single(queue.Queued).Email);
    }

    [UnitFact]
    public async Task GivenAFullQueue_WhenRegistering_ThenTheAnswerIsUnchanged()
    {
        var (scopes, scope) = await ScopeStoreAsync();

        var output = await Handler(scopes, new RecordingQueue(accepts: false)).HandleAsync(Command(scope.PublicId));

        AssertGenericAcceptance(output);
    }

    [UnitFact]
    public async Task GivenAClosedScope_WhenRegistering_ThenRegistrationUnavailableAndNothingQueued()
    {
        var (scopes, scope) = await ScopeStoreAsync(open: false);
        var queue = new RecordingQueue();

        var output = await Handler(scopes, queue).HandleAsync(Command(scope.PublicId));

        Assert.Contains(AuthMessages.RegistrationUnavailable, output.Errors);
        Assert.Empty(queue.Queued);
    }

    [UnitFact]
    public async Task GivenADeletedScope_WhenRegistering_ThenRegistrationUnavailableAndNothingQueued()
    {
        var (scopes, scope) = await ScopeStoreAsync(isDeleted: true);
        var queue = new RecordingQueue();

        var output = await Handler(scopes, queue).HandleAsync(Command(scope.PublicId));

        Assert.Contains(AuthMessages.RegistrationUnavailable, output.Errors);
        Assert.Empty(queue.Queued);
    }

    [UnitFact]
    public async Task GivenAnUnknownScope_WhenRegistering_ThenRegistrationUnavailableAndNothingQueued()
    {
        var (scopes, _) = await ScopeStoreAsync();
        var queue = new RecordingQueue();

        var output = await Handler(scopes, queue).HandleAsync(Command(Guid.NewGuid()));

        Assert.Contains(AuthMessages.RegistrationUnavailable, output.Errors);
        Assert.Empty(queue.Queued);
    }

    [UnitFact]
    public async Task GivenInvalidInput_WhenRegistering_ThenValidationErrorAndNothingQueued()
    {
        var (scopes, scope) = await ScopeStoreAsync();
        var queue = new RecordingQueue();

        var output = await Handler(scopes, queue).HandleAsync(Command(scope.PublicId, email: "nope"));

        Assert.Contains(PersonMessages.EmailInvalid, output.Errors);
        Assert.Empty(queue.Queued);
    }

    [UnitFact]
    public void GivenTheHandler_WhenInspectingWhatItDependsOn_ThenItCannotLookUpAnyPerson()
    {
        // AF-47b's timing half, stated structurally, as UC-12's handler test states it.
        var dependencies = typeof(RegisterCommandHandler).GetConstructors()
            .Single()
            .GetParameters()
            .Select(parameter => parameter.ParameterType)
            .ToList();

        Assert.DoesNotContain(dependencies, type => type.GenericTypeArguments.Contains(typeof(Person)));
        Assert.DoesNotContain(dependencies, type => type.GenericTypeArguments.Contains(typeof(GoogleUser)));
    }
}
