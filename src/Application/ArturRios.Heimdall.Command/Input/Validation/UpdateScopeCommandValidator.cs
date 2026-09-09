using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Shared.Messages;
using FluentValidation;

namespace ArturRios.Heimdall.Command.Input.Validation;

/// <summary>
///     Input validation for <see cref="UpdateScopeCommand" /> (UC-03). Only checks the shape of the
///     request — including the field lengths the columns impose, so an overlong value is a named 400
///     here rather than the persistence layer's unclassified data-access failure; business rules
///     that require data access (existence, name uniqueness) are enforced by the handler.
/// </summary>
public class UpdateScopeCommandValidator : AbstractValidator<UpdateScopeCommand>
{
    public UpdateScopeCommandValidator()
    {
        RuleFor(command => command.Name)
            .NotEmpty()
            .WithMessage(ScopeMessages.NameRequired)
            .MaximumLength(200)
            .WithMessage(ScopeMessages.NameTooLong);

        // FluentValidation's MaximumLength rule skips a null argument, so only a non-null overlong
        // description is rejected — matching how the Application and ScopePermission validators
        // treat their optional descriptions.
        RuleFor(command => command.Description)
            .MaximumLength(500)
            .WithMessage(ScopeMessages.DescriptionTooLong);

        // NFR-23. Consent is refused rather than accepted-and-ignored: a tenant that asked for it
        // and was silently given contract performance would believe a basis applied that did not.
        RuleFor(command => command.DefaultLegalBasis)
            .Must(basis => basis != (int)LegalBases.Consent)
            .WithMessage(ScopeMessages.ConsentBasisNotSupported);

        RuleFor(command => command.DefaultLegalBasis)
            .Must(basis => basis is null || (Enum.IsDefined((LegalBases)basis.Value)
                                             && basis.Value != (int)LegalBases.Unrecorded))
            .WithMessage(ScopeMessages.LegalBasisUnknown);

        // The scheme is checked, not just absoluteness. On Unix, Uri.TryCreate parses "/some/path"
        // as an absolute file:// URI, so an absoluteness check alone accepts a local filesystem path
        // as a tenant's published privacy notice — worse than the relative link it was meant to
        // refuse, because it looks valid and points at the host's disk.
        RuleFor(command => command.PrivacyNoticeUri)
            .Must(uri => string.IsNullOrWhiteSpace(uri) ||
                         (Uri.TryCreate(uri, UriKind.Absolute, out var parsed)
                          && (parsed.Scheme == Uri.UriSchemeHttps || parsed.Scheme == Uri.UriSchemeHttp)))
            .WithMessage(ScopeMessages.PrivacyNoticeUriInvalid);

        RuleFor(command => command.PrivacyNoticeUri)
            .MaximumLength(2048)
            .WithMessage(ScopeMessages.PrivacyNoticeUriInvalid);
    }
}
