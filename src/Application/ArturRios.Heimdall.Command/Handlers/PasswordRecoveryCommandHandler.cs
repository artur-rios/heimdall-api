using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Output;
using ArturRios.Heimdall.Command.Services;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Mediator.Command.Interfaces;
using ArturRios.Output;
using FluentValidation;

namespace ArturRios.Heimdall.Command.Handlers;

/// <summary>
///     Handles <see cref="PasswordRecoveryCommand" /> (UC-12): validates the request and queues it,
///     then answers. Finding the person and issuing and mailing the reset token (FR-PR-01/02) happen
///     afterwards, in <see cref="PasswordRecoveryProcessor" />.
/// </summary>
/// <remarks>
///     <para>
///         Every valid request returns the same success output. AF-12a — the address belongs to
///         nobody — is not an error flow here but the absence of work later on: the processor simply
///         issues no token. A person who is logically deleted or restricted, or a <c>User</c> whose
///         scope is, is treated the same way, for the reason UC-11 gives at length: an endpoint open
///         to anonymous callers must not become a directory of which addresses are registered and
///         which accounts still exist.
///     </para>
///     <para>
///         The same answer is not enough if it takes a different time to arrive. While the lookup,
///         the token insert and the Mailgun call ran here, a registered address answered a Mailgun
///         round trip later than an unknown one. So this handler does no work that depends on the
///         address: it validates, queues, and answers — identically, and in the same time, for every
///         address. A queue that is full drops the request; the answer does not change, and the
///         caller, who receives no email, asks again.
///     </para>
/// </remarks>
public class PasswordRecoveryCommandHandler(
    IValidator<PasswordRecoveryCommand> validator,
    IPasswordRecoveryQueue recoveryQueue)
    : ICommandHandlerAsync<PasswordRecoveryCommand, PasswordRecoveryCommandOutput>
{
    public async Task<DataOutput<PasswordRecoveryCommandOutput?>> HandleAsync(PasswordRecoveryCommand command, CancellationToken cancellationToken = default)
    {
        var output = DataOutput<PasswordRecoveryCommandOutput?>.New;

        // NFR-10: validate input shape. The only rejection this endpoint ever issues, and one that
        // depends on the request alone.
        var validation = await validator.ValidateAsync(command, cancellationToken);

        if (!validation.IsValid)
        {
            return output.WithErrors(validation.Errors.Select(failure => failure.ErrorMessage));
        }

        // UC-12 steps 2 to 4, deferred. The result is deliberately ignored: a dropped request must
        // answer exactly as a queued one does.
        recoveryQueue.TryEnqueue(new PasswordRecoveryRequest(command.Email, command.ScopeId));

        // UC-12 step 5: the same answer either way.
        return output
            .WithData(new PasswordRecoveryCommandOutput())
            .WithMessage(AuthMessages.PasswordRecoveryRequested);
    }
}
