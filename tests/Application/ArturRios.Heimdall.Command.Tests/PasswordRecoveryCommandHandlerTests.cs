using ArturRios.Heimdall.Command.Handlers;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Output;
using ArturRios.Heimdall.Command.Services;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Output;
using ArturRios.Util.Test.Attributes;
using FluentValidation;
using FluentValidation.Results;
using Moq;

namespace ArturRios.Heimdall.Command.Tests;

// Unit tests for PasswordRecoveryCommandHandler (UC-12). The handler answers; the work happens later,
// in PasswordRecoveryProcessor (see its tests). What is pinned here is AF-12a's timing half: the
// request path does the same thing for every address — validate, queue, answer — so nothing it does
// can take longer for a registered address than for an unknown one.
public class PasswordRecoveryCommandHandlerTests
{
    /// <summary>Records what was queued, and can pretend to be full.</summary>
    private sealed class RecordingQueue(bool accepts = true) : IPasswordRecoveryQueue
    {
        public List<PasswordRecoveryRequest> Queued { get; } = [];

        public bool TryEnqueue(PasswordRecoveryRequest request)
        {
            if (accepts)
            {
                Queued.Add(request);
            }

            return accepts;
        }
    }

    private static Mock<IValidator<PasswordRecoveryCommand>> ValidValidator()
    {
        var validator = new Mock<IValidator<PasswordRecoveryCommand>>();
        validator
            .Setup(v => v.ValidateAsync(It.IsAny<PasswordRecoveryCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ValidationResult());
        return validator;
    }

    private static PasswordRecoveryCommand Command(string email, Guid? scopeId = null) =>
        new() { Email = email, ScopeId = scopeId };

    /// <summary>The response every path must produce, asserted identically everywhere.</summary>
    private static void AssertGenericSuccess(DataOutput<PasswordRecoveryCommandOutput?> output)
    {
        Assert.True(output.Success);
        Assert.Empty(output.Errors);
        Assert.Contains(AuthMessages.PasswordRecoveryRequested, output.Messages);
    }

    [UnitFact]
    public async Task GivenAValidRequest_WhenHandling_ThenItIsQueuedAsAskedAndAnsweredGenerically()
    {
        // Given
        var queue = new RecordingQueue();
        var scopeId = Guid.NewGuid();

        // When
        var output = await new PasswordRecoveryCommandHandler(ValidValidator().Object, queue)
            .HandleAsync(Command("user@test.local", scopeId));

        // Then — the address and scope are handed on as given; nothing about them is looked up here
        AssertGenericSuccess(output);
        Assert.Equal(new PasswordRecoveryRequest("user@test.local", scopeId), Assert.Single(queue.Queued));
    }

    [UnitFact]
    public void GivenTheHandler_WhenInspectingWhatItDependsOn_ThenNothingCanTellOneAddressFromAnother()
    {
        // AF-12a's timing half, stated structurally: the handler cannot look anybody up, issue a
        // token or send an email, because it holds nothing that could. Before, it took the person
        // repository and the reset service, and a registered address waited for a token insert and a
        // Mailgun round trip that an unknown one skipped.
        var dependencies = typeof(PasswordRecoveryCommandHandler).GetConstructors()
            .Single()
            .GetParameters()
            .Select(parameter => parameter.ParameterType)
            .ToList();

        Assert.Equal([typeof(IValidator<PasswordRecoveryCommand>), typeof(IPasswordRecoveryQueue)], dependencies);
    }

    [UnitFact]
    public async Task GivenAFullQueue_WhenHandling_ThenTheAnswerIsUnchanged()
    {
        // Given a queue that drops the request
        var queue = new RecordingQueue(accepts: false);

        // When
        var output = await new PasswordRecoveryCommandHandler(ValidValidator().Object, queue)
            .HandleAsync(Command("admin@test.local"));

        // Then — a dropped request must not answer differently from a queued one
        AssertGenericSuccess(output);
    }

    [UnitFact]
    public async Task GivenInvalidInput_WhenHandling_ThenReturnsValidationErrorAndQueuesNothing()
    {
        // Given the validator rejects the command (NFR-10) — the one answer this endpoint gives that
        // is not the generic success, and one that depends on the request alone
        var queue = new RecordingQueue();
        var validator = new Mock<IValidator<PasswordRecoveryCommand>>();
        validator
            .Setup(v => v.ValidateAsync(It.IsAny<PasswordRecoveryCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ValidationResult([
                new ValidationFailure(nameof(PasswordRecoveryCommand.Email), AuthMessages.EmailInvalid)
            ]));

        // When
        var output = await new PasswordRecoveryCommandHandler(validator.Object, queue)
            .HandleAsync(Command("not-an-email"));

        // Then
        Assert.False(output.Success);
        Assert.Contains(AuthMessages.EmailInvalid, output.Errors);
        Assert.DoesNotContain(AuthMessages.PasswordRecoveryRequested, output.Messages);
        Assert.Empty(queue.Queued);
    }
}
