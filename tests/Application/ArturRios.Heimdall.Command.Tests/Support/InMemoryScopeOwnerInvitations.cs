using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Domain.Persistence;
using ArturRios.Util.Test.Mock;

namespace ArturRios.Heimdall.Command.Tests.Support;

/// <summary>
///     <see cref="IScopeOwnerInvitations" /> over the instances the fake repositories hold — the ones a
///     handler reads — applying each method's condition in memory as <c>ScopeOwnerInvitations</c> applies it
///     in PostgreSQL. A real race is the functional tests' subject; <see cref="BeforeConditionalWrite" />
///     lands a competing change between a handler's read and its write.
/// </summary>
/// <remarks>
///     Openness and the invitee's eligibility are the entity's own expressions,
///     <see cref="ScopeOwnerInvitation.OpenAt" /> and <see cref="ScopeOwnerInvitation.EligibleInvitee" />,
///     compiled, so this double and the PostgreSQL writes cannot drift apart on either.
/// </remarks>
public sealed class InMemoryScopeOwnerInvitations(
    AsyncFakeRepository<ScopeOwnerInvitation, long> invitations,
    AsyncFakeRepository<Person, long> persons,
    AsyncFakeRepository<Scope, long> scopes) : IScopeOwnerInvitations
{
    private static readonly Func<Person, bool> EligibleInvitee = ScopeOwnerInvitation.EligibleInvitee.Compile();

    /// <summary>Called with the invitation just before an end or an acceptance evaluates its condition.</summary>
    public Action<ScopeOwnerInvitation>? BeforeConditionalWrite { get; init; }

    public async Task<ScopeOwnerInvitationOpening> OpenAsync(ScopeOwnerInvitation invitation, DateTime now)
    {
        var unended = invitations.Query()
            .Where(x => x.ScopeId == invitation.ScopeId && x.InviteePersonId == invitation.InviteePersonId &&
                        x.EndedAt == null)
            .ToList();

        foreach (var expired in unended.Where(x => x.ExpiresAt <= now))
        {
            expired.EndedAt = expired.ExpiresAt;
        }

        if (unended.FirstOrDefault(OpenAt(now)) is { } open)
        {
            return new ScopeOwnerInvitationOpening(open, Created: false);
        }

        await invitations.CreateAsync(invitation);

        return new ScopeOwnerInvitationOpening(invitation, Created: true);
    }

    public Task<bool> TryEndAsync(ScopeOwnerInvitation invitation, ScopeOwnerInvitationOutcomes outcome, DateTime now)
    {
        if (outcome == ScopeOwnerInvitationOutcomes.Accepted)
        {
            throw new ArgumentException("An acceptance writes an ownership too; use TryAcceptAsync.", nameof(outcome));
        }

        BeforeConditionalWrite?.Invoke(invitation);

        if (!OpenAt(now)(invitation))
        {
            return Task.FromResult(false);
        }

        invitation.EndedAt = now;
        invitation.Outcome = outcome;

        return Task.FromResult(true);
    }

    public Task<bool> TryAcceptAsync(ScopeOwnerInvitation invitation, DateTime now)
    {
        BeforeConditionalWrite?.Invoke(invitation);

        var scope = scopes.Query().FirstOrDefault(x => x.Id == invitation.ScopeId);
        var invitee = persons.Query().FirstOrDefault(x => x.Id == invitation.InviteePersonId);

        // The scope's liveness is not part of EligibleInvitee, which covers the person only.
        if (!OpenAt(now)(invitation) || scope is not { IsDeleted: false } || invitee is null ||
            !EligibleInvitee(invitee))
        {
            return Task.FromResult(false);
        }

        invitation.EndedAt = now;
        invitation.Outcome = ScopeOwnerInvitationOutcomes.Accepted;

        if (!invitee.ScopeOwnerships.Any(ownership => ownership.ScopeId == invitation.ScopeId))
        {
            invitee.ScopeOwnerships.Add(new ScopeOwner { ScopeId = invitation.ScopeId, PersonId = invitee.Id });
        }

        return Task.FromResult(true);
    }

    /// <remarks>
    ///     As the PostgreSQL write does: voids only invitations not yet ended, answers with exactly their
    ///     public ids (in no promised order), and clears the persons as inviter on every invitation, ended or not.
    /// </remarks>
    public Task<IReadOnlyList<Guid>> ForgetPersonsAsync(IReadOnlyCollection<long> personIds, DateTime now)
    {
        var voided = new List<Guid>();

        foreach (var invitation in invitations.Query().ToList())
        {
            if (personIds.Contains(invitation.InviteePersonId) && invitation.EndedAt is null)
            {
                invitation.EndedAt = now;
                invitation.Outcome = ScopeOwnerInvitationOutcomes.Voided;
                voided.Add(invitation.PublicId);
            }

            if (invitation.InvitedByPersonId is { } inviterId && personIds.Contains(inviterId))
            {
                invitation.InvitedByPersonId = null;
            }
        }

        return Task.FromResult<IReadOnlyList<Guid>>(voided);
    }

    private static Func<ScopeOwnerInvitation, bool> OpenAt(DateTime now) => ScopeOwnerInvitation.OpenAt(now).Compile();
}
