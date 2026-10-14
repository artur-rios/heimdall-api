using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Heimdall.Shared.Time;
using FluentValidation;

namespace ArturRios.Heimdall.Command.Input.Validation;

/// <summary>UC-53 AF-53c: an expiry, when given, must lie in the future — read as UTC when it has no offset.</summary>
public class IssueApplicationCredentialCommandValidator : AbstractValidator<IssueApplicationCredentialCommand>
{
    public IssueApplicationCredentialCommandValidator()
    {
        RuleFor(command => command.ExpiresAt)
            .Must(expiresAt => expiresAt is null || UtcInstant.Of(expiresAt.Value) > DateTime.UtcNow)
            .WithMessage(ApplicationCredentialMessages.ExpiryNotInFuture);
    }
}
