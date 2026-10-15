using ArturRios.Heimdall.Shared.Messages;
using FluentValidation;

namespace ArturRios.Heimdall.Command.Input.Validation;

/// <summary>UC-56 AF-56a: the request names an application, a scope and a secret of bounded length.</summary>
public class IssueApplicationTokenCommandValidator : AbstractValidator<IssueApplicationTokenCommand>
{
    /// <summary>A secret is 47 characters; the bound only stops hashing an arbitrarily large body.</summary>
    private const int MaxSecretLength = 100;

    public IssueApplicationTokenCommandValidator()
    {
        RuleFor(command => command.ApplicationId)
            .NotEmpty().WithMessage(ApplicationTokenMessages.ApplicationIdRequired);

        RuleFor(command => command.ScopeId)
            .NotEmpty().WithMessage(ApplicationTokenMessages.ScopeIdRequired);

        RuleFor(command => command.Secret)
            .NotEmpty().WithMessage(ApplicationTokenMessages.SecretRequired)
            .MaximumLength(MaxSecretLength).WithMessage(ApplicationTokenMessages.SecretTooLong);
    }
}
