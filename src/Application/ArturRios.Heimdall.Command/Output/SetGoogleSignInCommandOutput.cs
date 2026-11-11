using ArturRios.Mediator.Command;

namespace ArturRios.Heimdall.Command.Output;

/// <summary>
///     The scope as updated by <see cref="Input.SetGoogleSignInCommand" /> (UC-24) — step 5 of the
///     main flow returns the scope, not just the flag. Only externally-facing <c>PublicId</c>
///     identifiers are exposed; internal Ids never leave the data layer. <c>IsDeleted</c> is absent:
///     the handler only ever answers for a non-deleted scope (AF-24a), so it would be <c>false</c> on
///     every response by construction.
/// </summary>
public class SetGoogleSignInCommandOutput : CommandOutput
{
    /// <summary>Public identifier of the scope.</summary>
    public Guid Id { get; init; }

    /// <summary>Scope display name.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Scope description, if any.</summary>
    public string? Description { get; init; }

    /// <summary>Whether Google sign-in is enabled for the scope, as just set.</summary>
    public bool GoogleSignInEnabled { get; init; }

    /// <summary>Base link for this scope's verification emails, or <c>null</c> to use the global one.</summary>
    public string? EmailVerificationUrl { get; set; }

    /// <summary>Base link for this scope's password-reset emails, or <c>null</c> to use the global one.</summary>
    public string? PasswordResetUrl { get; set; }

    /// <summary>Public identifiers of the scope's owners.</summary>
    public IEnumerable<Guid> OwnerIds { get; init; } = [];

    /// <summary>Creation timestamp.</summary>
    public DateTime CreatedAt { get; init; }

    /// <summary>Last update timestamp.</summary>
    public DateTime UpdatedAt { get; init; }
}
