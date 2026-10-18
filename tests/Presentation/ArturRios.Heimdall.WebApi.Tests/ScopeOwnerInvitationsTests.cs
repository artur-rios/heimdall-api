using ArturRios.Heimdall.Data.Persistence;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.WebApi.Tests.Support;
using ArturRios.Util.Test.Attributes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;
using static ArturRios.Heimdall.WebApi.Tests.Support.ApplicationSeeds;

namespace ArturRios.Heimdall.WebApi.Tests;

/// <summary>
///     <see cref="ScopeOwnerInvitations" /> against real PostgreSQL (FR-SC-17, FR-SC-18): opening is
///     idempotent per pair and closes an expired row first, acceptance ends the invitation and inserts the
///     ownership in one transaction, and of any number of racing answers exactly one wins.
/// </summary>
/// <remarks>
///     <para>
///         The accept-against-withdraw race is made deterministic as <c>ApplicationCredentialRaceTests</c>
///         makes its own: just before the accepting context sends its first <c>UPDATE</c>, a
///         <see cref="BeforeFirstUpdate" /> interceptor withdraws the invitation through a second connection
///         and commits.
///     </para>
///     <para>
///         Each condition of <see cref="ScopeOwnerInvitation.EligibleInvitee" />, and the scope's and the
///         invitation's own, has a test of its own proving the conditional <c>UPDATE</c> refuses it.
///     </para>
/// </remarks>
[Collection(nameof(FunctionalCollection))]
public class ScopeOwnerInvitationsTests(PostgresFixture db)
{
    private static ScopeOwnerInvitation New(Scope scope, Person invitee, Person? inviter, DateTime createdAt,
        DateTime expiresAt) => new()
    {
        PublicId = Guid.NewGuid(),
        ScopeId = scope.Id,
        InviteePersonId = invitee.Id,
        InvitedByPersonId = inviter?.Id,
        CreatedAt = createdAt,
        ExpiresAt = expiresAt
    };

    private static ScopeOwnerInvitation New(Scope scope, Person invitee, Person? inviter) =>
        New(scope, invitee, inviter, DateTime.UtcNow, DateTime.UtcNow.AddDays(7));

    private async Task<ScopeOwnerInvitation> InsertAsync(ScopeOwnerInvitation invitation)
    {
        await using var context = db.CreateContext();
        context.ScopeOwnerInvitations.Add(invitation);
        await context.SaveChangesAsync();
        return invitation;
    }

    private async Task<ScopeOwnerInvitation> StoredAsync(ScopeOwnerInvitation invitation)
    {
        await using var context = db.CreateContext();
        return await context.ScopeOwnerInvitations.AsNoTracking().SingleAsync(x => x.Id == invitation.Id);
    }

    private async Task<List<ScopeOwnerInvitation>> OfPairAsync(Scope scope, Person invitee)
    {
        await using var context = db.CreateContext();
        return await context.ScopeOwnerInvitations.AsNoTracking()
            .Where(x => x.ScopeId == scope.Id && x.InviteePersonId == invitee.Id)
            .ToListAsync();
    }

    private async Task<int> OwnershipCountAsync(Scope scope, Person person)
    {
        await using var context = db.CreateContext();
        return await context.ScopeOwners.CountAsync(x => x.ScopeId == scope.Id && x.PersonId == person.Id);
    }

    private async Task<(Scope Scope, Person Inviter, Person Invitee)> SeedAsync()
    {
        var scope = await SeedScopeAsync(db);
        var inviter = await SeedScopeAdminAsync(db, scope);
        var invitee = await SeedScopeAdminAsync(db);
        return (scope, inviter, invitee);
    }

    /// <summary>Changes the invitee's row behind the port's back, as another request would have.</summary>
    private async Task UpdateInviteeAsync(Person invitee, Action<UpdateSettersBuilder<Person>> set)
    {
        await using var context = db.CreateContext();
        await context.Persons.Where(x => x.Id == invitee.Id).ExecuteUpdateAsync(set);
    }

    /// <summary>Accepts with a fresh context, as the invitee's own request would.</summary>
    private async Task<bool> AcceptAsync(ScopeOwnerInvitation invitation)
    {
        await using var context = db.CreateContext();
        return await new ScopeOwnerInvitations(context).TryAcceptAsync(invitation, DateTime.UtcNow);
    }

    [FunctionalFact]
    public async Task GivenNoInvitation_WhenOpening_ThenOneIsCreated()
    {
        // Given
        var (scope, inviter, invitee) = await SeedAsync();
        await using var context = db.CreateContext();

        // When
        var opening = await new ScopeOwnerInvitations(context).OpenAsync(New(scope, invitee, inviter), DateTime.UtcNow);

        // Then
        Assert.True(opening.Created);
        var stored = Assert.Single(await OfPairAsync(scope, invitee));
        Assert.Equal(opening.Invitation.PublicId, stored.PublicId);
        Assert.Null(stored.EndedAt);
    }

    [FunctionalFact]
    public async Task GivenAnOpenInvitation_WhenOpeningAgain_ThenTheOpenOneIsReturnedAndNothingIsInserted()
    {
        // Given
        var (scope, inviter, invitee) = await SeedAsync();
        var open = await InsertAsync(New(scope, invitee, inviter));
        await using var context = db.CreateContext();

        // When
        var opening = await new ScopeOwnerInvitations(context).OpenAsync(New(scope, invitee, inviter), DateTime.UtcNow);

        // Then
        Assert.False(opening.Created);
        Assert.Equal(open.PublicId, opening.Invitation.PublicId);
        Assert.Single(await OfPairAsync(scope, invitee));
    }

    [FunctionalFact]
    public async Task GivenAnInvitationThatExpiredUnanswered_WhenOpeningAgain_ThenItEndsAtItsExpiryAndANewOneOpens()
    {
        // Given — Review Focus 2: the expired row still holds the pair's slot in the partial unique index
        var (scope, inviter, invitee) = await SeedAsync();
        var now = DateTime.UtcNow;
        var expired = await InsertAsync(New(scope, invitee, inviter, now.AddDays(-8), now.AddDays(-1)));
        await using var context = db.CreateContext();

        // When
        var opening = await new ScopeOwnerInvitations(context).OpenAsync(New(scope, invitee, inviter), now);

        // Then
        Assert.True(opening.Created);
        Assert.NotEqual(expired.PublicId, opening.Invitation.PublicId);
        var closed = await StoredAsync(expired);
        Assert.Equal(closed.ExpiresAt, closed.EndedAt);
        Assert.Null(closed.Outcome);
        Assert.Equal(2, (await OfPairAsync(scope, invitee)).Count);
    }

    [FunctionalFact]
    public async Task GivenTwoSimultaneousOpens_WhenBothRun_ThenExactlyOneIsCreated()
    {
        // Given
        var (scope, inviter, invitee) = await SeedAsync();
        await using var first = db.CreateContext();
        await using var second = db.CreateContext();
        var now = DateTime.UtcNow;

        // When
        var openings = await Task.WhenAll(
            new ScopeOwnerInvitations(first).OpenAsync(New(scope, invitee, inviter), now),
            new ScopeOwnerInvitations(second).OpenAsync(New(scope, invitee, inviter), now));

        // Then — one created, both answering with the same open invitation
        Assert.Single(openings, opening => opening.Created);
        Assert.Equal(openings[0].Invitation.PublicId, openings[1].Invitation.PublicId);
        Assert.Single(await OfPairAsync(scope, invitee));
    }

    [FunctionalFact]
    public async Task GivenAnOpenInvitation_WhenAccepting_ThenItEndsAcceptedAndTheOwnershipExists()
    {
        // Given
        var (scope, inviter, invitee) = await SeedAsync();
        var invitation = await InsertAsync(New(scope, invitee, inviter));
        await using var context = db.CreateContext();

        // When
        var accepted = await new ScopeOwnerInvitations(context).TryAcceptAsync(invitation, DateTime.UtcNow);

        // Then
        Assert.True(accepted);
        var stored = await StoredAsync(invitation);
        Assert.Equal(ScopeOwnerInvitationOutcomes.Accepted, stored.Outcome);
        Assert.NotNull(stored.EndedAt);
        Assert.Equal(1, await OwnershipCountAsync(scope, invitee));
    }

    [FunctionalFact]
    public async Task GivenASystemAdminInvitee_WhenAccepting_ThenItEndsAcceptedAndTheOwnershipExists()
    {
        // Given — the other role EligibleInvitee admits
        var (scope, inviter, invitee) = await SeedAsync();
        await UpdateInviteeAsync(invitee, set => set.SetProperty(x => x.RoleId, (long)Roles.SystemAdmin));
        var invitation = await InsertAsync(New(scope, invitee, inviter));

        // When
        var accepted = await AcceptAsync(invitation);

        // Then
        Assert.True(accepted);
        Assert.Equal(ScopeOwnerInvitationOutcomes.Accepted, (await StoredAsync(invitation)).Outcome);
        Assert.Equal(1, await OwnershipCountAsync(scope, invitee));
    }

    [FunctionalFact]
    public async Task GivenAWithdrawalCommittedAfterTheAcceptReadIt_WhenAccepting_ThenRefusedAndNoOwnership()
    {
        // Given
        var (scope, inviter, invitee) = await SeedAsync();
        var invitation = await InsertAsync(New(scope, invitee, inviter));
        await using var context = db.CreateContext(new BeforeFirstUpdate(() => WithdrawAsync(invitation)));

        // When
        var accepted = await new ScopeOwnerInvitations(context).TryAcceptAsync(invitation, DateTime.UtcNow);

        // Then — the withdrawal stands and nobody was made an owner
        Assert.False(accepted);
        Assert.Equal(ScopeOwnerInvitationOutcomes.Withdrawn, (await StoredAsync(invitation)).Outcome);
        Assert.Equal(0, await OwnershipCountAsync(scope, invitee));
    }

    [FunctionalFact]
    public async Task GivenTwoSimultaneousAccepts_WhenBothRun_ThenExactlyOneWins()
    {
        // Given
        var (scope, inviter, invitee) = await SeedAsync();
        var invitation = await InsertAsync(New(scope, invitee, inviter));
        await using var first = db.CreateContext();
        await using var second = db.CreateContext();
        var now = DateTime.UtcNow;

        // When
        var results = await Task.WhenAll(
            new ScopeOwnerInvitations(first).TryAcceptAsync(await CopyAsync(invitation), now),
            new ScopeOwnerInvitations(second).TryAcceptAsync(await CopyAsync(invitation), now));

        // Then
        Assert.Single(results, accepted => accepted);
        Assert.Equal(1, await OwnershipCountAsync(scope, invitee));
    }

    [FunctionalFact]
    public async Task GivenTheInviteeAlreadyOwnsTheScope_WhenAccepting_ThenAcceptedWithOneOwnershipRow()
    {
        // Given — a System Admin added them directly while the invitation was open
        var (scope, inviter, invitee) = await SeedAsync();
        var invitation = await InsertAsync(New(scope, invitee, inviter));
        await using (var setup = db.CreateContext())
        {
            setup.ScopeOwners.Add(new ScopeOwner { ScopeId = scope.Id, PersonId = invitee.Id });
            await setup.SaveChangesAsync();
        }

        await using var context = db.CreateContext();

        // When
        var accepted = await new ScopeOwnerInvitations(context).TryAcceptAsync(invitation, DateTime.UtcNow);

        // Then
        Assert.True(accepted);
        Assert.Equal(1, await OwnershipCountAsync(scope, invitee));
    }

    [FunctionalFact]
    public async Task GivenALogicallyDeletedScope_WhenAccepting_ThenRefusedAndStillOpen()
    {
        // Given
        var (scope, inviter, invitee) = await SeedAsync();
        var invitation = await InsertAsync(New(scope, invitee, inviter));
        await using (var setup = db.CreateContext())
        {
            await setup.Scopes.Where(x => x.Id == scope.Id)
                .ExecuteUpdateAsync(set => set.SetProperty(x => x.IsDeleted, true));
        }

        await using var context = db.CreateContext();

        // When
        var accepted = await new ScopeOwnerInvitations(context).TryAcceptAsync(invitation, DateTime.UtcNow);

        // Then
        Assert.False(accepted);
        Assert.Null((await StoredAsync(invitation)).EndedAt);
        Assert.Equal(0, await OwnershipCountAsync(scope, invitee));
    }

    [FunctionalFact]
    public async Task GivenALogicallyDeletedInvitee_WhenAccepting_ThenRefusedAndStillOpen()
    {
        // Given
        var (scope, inviter, invitee) = await SeedAsync();
        var invitation = await InsertAsync(New(scope, invitee, inviter));
        await UpdateInviteeAsync(invitee, set => set.SetProperty(x => x.IsDeleted, true));

        // When
        var accepted = await AcceptAsync(invitation);

        // Then
        Assert.False(accepted);
        Assert.Null((await StoredAsync(invitation)).EndedAt);
        Assert.Equal(0, await OwnershipCountAsync(scope, invitee));
    }

    [FunctionalFact]
    public async Task GivenAProcessingRestrictedInvitee_WhenAccepting_ThenRefusedAndStillOpen()
    {
        // Given
        var (scope, inviter, invitee) = await SeedAsync();
        var invitation = await InsertAsync(New(scope, invitee, inviter));
        await UpdateInviteeAsync(invitee, set => set.SetProperty(x => x.ProcessingRestrictedAt, DateTime.UtcNow));

        // When
        var accepted = await AcceptAsync(invitation);

        // Then
        Assert.False(accepted);
        Assert.Null((await StoredAsync(invitation)).EndedAt);
        Assert.Equal(0, await OwnershipCountAsync(scope, invitee));
    }

    [FunctionalFact]
    public async Task GivenAnAnonymisedInvitee_WhenAccepting_ThenRefusedAndStillOpen()
    {
        // Given — anonymised but, for this test alone, not marked deleted, so only AnonymisedAt can refuse it
        var (scope, inviter, invitee) = await SeedAsync();
        var invitation = await InsertAsync(New(scope, invitee, inviter));
        await UpdateInviteeAsync(invitee, set => set.SetProperty(x => x.AnonymisedAt, DateTime.UtcNow));

        // When
        var accepted = await AcceptAsync(invitation);

        // Then
        Assert.False(accepted);
        Assert.Null((await StoredAsync(invitation)).EndedAt);
        Assert.Equal(0, await OwnershipCountAsync(scope, invitee));
    }

    [FunctionalFact]
    public async Task GivenAnInviteeWhoIsNoLongerAnAdministrator_WhenAccepting_ThenRefusedAndStillOpen()
    {
        // Given
        var (scope, inviter, invitee) = await SeedAsync();
        var invitation = await InsertAsync(New(scope, invitee, inviter));
        await UpdateInviteeAsync(invitee, set => set.SetProperty(x => x.RoleId, (long)Roles.User));

        // When
        var accepted = await AcceptAsync(invitation);

        // Then
        Assert.False(accepted);
        Assert.Null((await StoredAsync(invitation)).EndedAt);
        Assert.Equal(0, await OwnershipCountAsync(scope, invitee));
    }

    [FunctionalFact]
    public async Task GivenAnExpiredInvitation_WhenAccepting_ThenRefusedAndNothingIsWritten()
    {
        // Given
        var (scope, inviter, invitee) = await SeedAsync();
        var now = DateTime.UtcNow;
        var invitation = await InsertAsync(New(scope, invitee, inviter, now.AddDays(-8), now.AddDays(-1)));

        // When
        var accepted = await AcceptAsync(invitation);

        // Then
        Assert.False(accepted);
        var stored = await StoredAsync(invitation);
        Assert.Null(stored.EndedAt);
        Assert.Null(stored.Outcome);
        Assert.Equal(0, await OwnershipCountAsync(scope, invitee));
    }

    [FunctionalFact]
    public async Task GivenAnAlreadyDeclinedInvitation_WhenAccepting_ThenRefusedAndTheDeclineStands()
    {
        // Given
        var (scope, inviter, invitee) = await SeedAsync();
        var invitation = New(scope, invitee, inviter);
        invitation.EndedAt = DateTime.UtcNow;
        invitation.Outcome = ScopeOwnerInvitationOutcomes.Declined;
        await InsertAsync(invitation);

        // When
        var accepted = await AcceptAsync(await CopyAsync(invitation));

        // Then
        Assert.False(accepted);
        Assert.Equal(ScopeOwnerInvitationOutcomes.Declined, (await StoredAsync(invitation)).Outcome);
        Assert.Equal(0, await OwnershipCountAsync(scope, invitee));
    }

    [FunctionalFact]
    public async Task GivenAnOpenInvitation_WhenEndedTwice_ThenOnlyTheFirstOutcomeStands()
    {
        // Given
        var (scope, inviter, invitee) = await SeedAsync();
        var invitation = await InsertAsync(New(scope, invitee, inviter));
        await using var context = db.CreateContext();
        var invitations = new ScopeOwnerInvitations(context);
        var now = DateTime.UtcNow;

        // When
        var withdrawn = await invitations.TryEndAsync(await CopyAsync(invitation), ScopeOwnerInvitationOutcomes.Withdrawn, now);
        var declined = await invitations.TryEndAsync(await CopyAsync(invitation), ScopeOwnerInvitationOutcomes.Declined, now);

        // Then
        Assert.True(withdrawn);
        Assert.False(declined);
        Assert.Equal(ScopeOwnerInvitationOutcomes.Withdrawn, (await StoredAsync(invitation)).Outcome);
    }

    [FunctionalFact]
    public async Task GivenAnOpenInvitation_WhenEndedAsAccepted_ThenRefusedAndStillOpen()
    {
        // Given
        var (scope, inviter, invitee) = await SeedAsync();
        var invitation = await InsertAsync(New(scope, invitee, inviter));
        await using var context = db.CreateContext();

        // When
        var failure = await Record.ExceptionAsync(() => new ScopeOwnerInvitations(context)
            .TryEndAsync(invitation, ScopeOwnerInvitationOutcomes.Accepted, DateTime.UtcNow));

        // Then — an acceptance must also write the ownership, which only TryAcceptAsync does
        Assert.IsType<ArgumentException>(failure);
        Assert.Null((await StoredAsync(invitation)).EndedAt);
    }

    [FunctionalFact]
    public async Task GivenAnErasedInviteeAndInviter_WhenForgotten_ThenTheInviteesInvitationIsVoidedAndTheInviterCleared()
    {
        // Given — invitee A's open invitation, and an invitation A made to B
        var scope = await SeedScopeAsync(db);
        var erased = await SeedScopeAdminAsync(db, scope);
        var other = await SeedScopeAdminAsync(db);
        var otherScope = await SeedScopeAsync(db);
        var toErased = await InsertAsync(New(otherScope, erased, other));
        var fromErased = await InsertAsync(New(scope, other, erased));
        await using var context = db.CreateContext();

        // When
        var voided = await new ScopeOwnerInvitations(context).ForgetPersonsAsync([erased.Id], DateTime.UtcNow);

        // Then
        Assert.Equal([toErased.PublicId], voided);
        Assert.Equal(ScopeOwnerInvitationOutcomes.Voided, (await StoredAsync(toErased)).Outcome);
        var stillOpen = await StoredAsync(fromErased);
        Assert.Null(stillOpen.EndedAt);
        Assert.Null(stillOpen.InvitedByPersonId);
    }

    /// <summary>A fresh, untracked copy of the stored row, as a second request would read it.</summary>
    private Task<ScopeOwnerInvitation> CopyAsync(ScopeOwnerInvitation invitation) => StoredAsync(invitation);

    /// <summary>The competing owner's withdrawal: its own connection, committed when this returns.</summary>
    private async Task WithdrawAsync(ScopeOwnerInvitation invitation)
    {
        await using var other = db.CreateContext();
        await other.ScopeOwnerInvitations
            .Where(x => x.Id == invitation.Id)
            .ExecuteUpdateAsync(set => set
                .SetProperty(x => x.EndedAt, DateTime.UtcNow)
                .SetProperty(x => x.Outcome, ScopeOwnerInvitationOutcomes.Withdrawn));
    }
}
