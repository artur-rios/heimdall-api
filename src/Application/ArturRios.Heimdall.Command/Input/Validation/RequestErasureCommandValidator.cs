using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Shared.Messages;
using FluentValidation;

namespace ArturRios.Heimdall.Command.Input.Validation;

/// <summary>
///     Validates <see cref="RequestErasureCommand" /> (NFR-10). Only the shape is checked here:
///     that some credential was presented at all.
/// </summary>
/// <remarks>
///     Which credential is required depends on whether the caller is a person or a Google User, and
///     that is not knowable from the request — it takes a database read. Deciding it here would
///     mean either duplicating the lookup or answering "wrong credential type", which tells an
///     anonymous-shaped caller which identity table they are in. The handler answers both cases
///     with the one message AF-42a defines.
/// </remarks>
public class RequestErasureCommandValidator : AbstractValidator<RequestErasureCommand>
{
    public RequestErasureCommandValidator()
    {
        RuleFor(command => command)
            .Must(command => !string.IsNullOrWhiteSpace(command.Password) ||
                             !string.IsNullOrWhiteSpace(command.IdToken))
            .WithMessage(ErasureMessages.CredentialNotAccepted);
    }
}
