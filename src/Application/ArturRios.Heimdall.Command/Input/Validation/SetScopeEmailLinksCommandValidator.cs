using ArturRios.Heimdall.Shared.Messages;
using FluentValidation;

namespace ArturRios.Heimdall.Command.Input.Validation;

/// <summary>
///     Shape validation for <see cref="SetScopeEmailLinksCommand" />: each link, when present, is an
///     absolute http or https URL that fits its column. An absent or blank one is a clear, not an error.
/// </summary>
public class SetScopeEmailLinksCommandValidator : AbstractValidator<SetScopeEmailLinksCommand>
{
    private const int MaxLength = 2048;

    public SetScopeEmailLinksCommandValidator()
    {
        RuleFor(command => command.EmailVerificationUrl)
            .Must(BeUsableLink)
            .WithMessage(ScopeMessages.EmailLinkInvalid);

        RuleFor(command => command.PasswordResetUrl)
            .Must(BeUsableLink)
            .WithMessage(ScopeMessages.EmailLinkInvalid);
    }

    // The scheme is checked, not just absoluteness, for the reason UpdateScopeCommandValidator gives
    // for the privacy notice URI: on Unix, Uri.TryCreate parses "/some/path" as an absolute file://
    // URI, so an absoluteness check alone accepts a local filesystem path as an emailed link.
    private static bool BeUsableLink(string? url) =>
        string.IsNullOrWhiteSpace(url) ||
        (url.Length <= MaxLength
         && Uri.TryCreate(url, UriKind.Absolute, out var parsed)
         && (parsed.Scheme == Uri.UriSchemeHttps || parsed.Scheme == Uri.UriSchemeHttp));
}
