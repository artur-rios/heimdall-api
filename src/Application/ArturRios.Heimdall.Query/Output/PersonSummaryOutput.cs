using ArturRios.Mediator.Query;

namespace ArturRios.Heimdall.Query.Output;

/// <summary>
///     The minimum a person picker needs: who they are and how to recognise them (FR-PE-12).
/// </summary>
/// <remarks>
///     A separate type from <see cref="PersonOutput" />, and deliberately so. UC-07's visibility rule
///     otherwise lets a Scope Admin see only the administrators co-owning their own scopes, and
///     UI-14 needs them to find one they share no scope with — so this listing's audience is wider
///     than that rule. Three fields is what makes the widening safe: a Scope Admin learns that an
///     administrator with a given name and address exists, which they can already establish by
///     submitting that address to <c>POST /api/scopes/{id}/owners</c> and reading the duplicate-email
///     refusal. Reusing <see cref="PersonOutput" /> would instead hand them <c>Role</c>,
///     <c>OwnedScopeIds</c>, <c>EmailVerified</c>, <c>TwoFactorEnabled</c>, and the timestamps.
/// </remarks>
public class PersonSummaryOutput : QueryOutput
{
    /// <summary>Public identifier of the person.</summary>
    public Guid Id { get; set; }

    /// <summary>Full name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Email address.</summary>
    public string Email { get; set; } = string.Empty;
}
