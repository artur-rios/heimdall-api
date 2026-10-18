using ArturRios.Heimdall.Data.Configuration;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Domain.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ArturRios.Heimdall.Data.Persistence;

/// <summary>
///     <see cref="IScopeOwnerInvitations" /> on PostgreSQL. Every end is an <c>ExecuteUpdate</c> whose
///     condition is "still open"; under READ COMMITTED a racing write that waits on the row re-reads it and
///     finds the condition false, so it writes nothing.
/// </summary>
/// <remarks>
///     Acceptance spans two tables, so it is a transaction, as <see cref="ApplicationCredentialSlots" /> is:
///     the conditional <c>UPDATE</c> first, and the <c>SCOPE_OWNER</c> insert only when it took. The insert is
///     targeted SQL rather than a save of the person, whose whole row must never be written back (#237).
/// </remarks>
public sealed class ScopeOwnerInvitations(AppDbContext context) : IScopeOwnerInvitations
{
    public async Task<ScopeOwnerInvitationOpening> OpenAsync(ScopeOwnerInvitation invitation, DateTime now)
    {
        // An expired row still holds the pair's slot in the partial unique index; it ended at its expiry.
        await context.ScopeOwnerInvitations
            .Where(x => x.ScopeId == invitation.ScopeId && x.InviteePersonId == invitation.InviteePersonId &&
                        x.EndedAt == null && x.ExpiresAt <= now)
            .ExecuteUpdateAsync(set => set.SetProperty(x => x.EndedAt, x => (DateTime?)x.ExpiresAt));

        if (await FindOpenAsync(invitation, now) is { } open)
        {
            return new ScopeOwnerInvitationOpening(open, Created: false);
        }

        context.ScopeOwnerInvitations.Add(invitation);

        try
        {
            await context.SaveChangesAsync();

            return new ScopeOwnerInvitationOpening(invitation, Created: true);
        }
        catch (DbUpdateException exception)
            when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // A simultaneous request opened one first; the index refused this one. Answer with theirs.
            context.Entry(invitation).State = EntityState.Detached;

            var winner = await FindOpenAsync(invitation, now);

            if (winner is null)
            {
                throw;
            }

            return new ScopeOwnerInvitationOpening(winner, Created: false);
        }
    }

    public async Task<bool> TryEndAsync(
        ScopeOwnerInvitation invitation, ScopeOwnerInvitationOutcomes outcome, DateTime now)
    {
        if (outcome == ScopeOwnerInvitationOutcomes.Accepted)
        {
            throw new ArgumentException("An acceptance writes an ownership too; use TryAcceptAsync.", nameof(outcome));
        }

        var ended = await context.ScopeOwnerInvitations
            .Where(x => x.Id == invitation.Id)
            .Where(ScopeOwnerInvitation.OpenAt(now))
            .ExecuteUpdateAsync(set => set
                .SetProperty(x => x.EndedAt, now)
                .SetProperty(x => x.Outcome, outcome)) == 1;

        if (ended)
        {
            Record(invitation, now, outcome);
        }

        return ended;
    }

    public async Task<bool> TryAcceptAsync(ScopeOwnerInvitation invitation, DateTime now)
    {
        // The table name comes from the model, never from input, so building it into the SQL is safe; the
        // values stay parameters.
        var ownerTable = context.Model.FindEntityType(typeof(ScopeOwner))!.GetSchemaQualifiedTableName();
        var strategy = context.Database.CreateExecutionStrategy();

        var accepted = await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await context.Database.BeginTransactionAsync();

            var ended = await context.ScopeOwnerInvitations
                .Where(x => x.Id == invitation.Id)
                .Where(ScopeOwnerInvitation.OpenAt(now))
                .Where(x => context.Scopes.Any(scope => scope.Id == x.ScopeId && !scope.IsDeleted))
                .Where(x => context.Persons
                    .Where(ScopeOwnerInvitation.EligibleInvitee)
                    .Any(person => person.Id == x.InviteePersonId))
                .ExecuteUpdateAsync(set => set
                    .SetProperty(x => x.EndedAt, now)
                    .SetProperty(x => x.Outcome, ScopeOwnerInvitationOutcomes.Accepted)) == 1;

            if (!ended)
            {
                await transaction.RollbackAsync();

                return false;
            }

            var sql = $"INSERT INTO {ownerTable} (scope_id, person_id) VALUES ({{0}}, {{1}}) ON CONFLICT DO NOTHING";
            await context.Database.ExecuteSqlRawAsync(sql, invitation.ScopeId, invitation.InviteePersonId);

            await transaction.CommitAsync();

            return true;
        });

        if (accepted)
        {
            Record(invitation, now, ScopeOwnerInvitationOutcomes.Accepted);
        }

        return accepted;
    }

    public async Task<IReadOnlyList<Guid>> ForgetPersonsAsync(IReadOnlyCollection<long> personIds, DateTime now)
    {
        if (personIds.Count == 0)
        {
            return [];
        }

        // Table and column names come from the model, never from input, so building them into the SQL is
        // safe; the values stay parameters.
        var invitationType = context.Model.FindEntityType(typeof(ScopeOwnerInvitation))!;
        var table = invitationType.GetSchemaQualifiedTableName();
        var endedAt = Column(nameof(ScopeOwnerInvitation.EndedAt));
        var outcome = Column(nameof(ScopeOwnerInvitation.Outcome));
        var invitee = Column(nameof(ScopeOwnerInvitation.InviteePersonId));
        var publicId = Column(nameof(ScopeOwnerInvitation.PublicId));
        var ids = personIds.ToArray();
        var strategy = context.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await context.Database.BeginTransactionAsync();

            // RETURNING answers with exactly the rows this UPDATE changed. A row a racing answer ended first
            // fails the guard, so it is neither voided nor reported: every id returned is a void that took
            // effect, and the caller audits each one (G-F7).
            var sql = $$"""
                UPDATE {{table}} SET {{endedAt}} = {0}, {{outcome}} = {1}
                WHERE {{invitee}} = ANY({2}) AND {{endedAt}} IS NULL
                RETURNING {{publicId}} AS "Value"
                """;
            var voided = await context.Database
                .SqlQueryRaw<Guid>(sql, now, (short)ScopeOwnerInvitationOutcomes.Voided, ids)
                .ToListAsync();

            await context.ScopeOwnerInvitations
                .Where(x => x.InvitedByPersonId != null && ids.Contains(x.InvitedByPersonId.Value))
                .ExecuteUpdateAsync(set => set.SetProperty(x => x.InvitedByPersonId, (long?)null));

            await transaction.CommitAsync();

            return (IReadOnlyList<Guid>)voided;
        });

        string Column(string property) => invitationType.FindProperty(property)!.GetColumnName();
    }

    private Task<ScopeOwnerInvitation?> FindOpenAsync(ScopeOwnerInvitation invitation, DateTime now) =>
        context.ScopeOwnerInvitations.AsNoTracking()
            .Where(x => x.ScopeId == invitation.ScopeId && x.InviteePersonId == invitation.InviteePersonId)
            .Where(ScopeOwnerInvitation.OpenAt(now))
            .FirstOrDefaultAsync();

    /// <summary>
    ///     Brings the caller's instance in line with a write the change tracker did not see, as
    ///     <c>AtomicWrites</c> does, so a later save does not write the old values back.
    /// </summary>
    private void Record(ScopeOwnerInvitation invitation, DateTime endedAt, ScopeOwnerInvitationOutcomes outcome)
    {
        invitation.EndedAt = endedAt;
        invitation.Outcome = outcome;

        var entry = context.Entry(invitation);

        if (entry.State is EntityState.Unchanged or EntityState.Modified)
        {
            entry.Property(x => x.EndedAt).OriginalValue = endedAt;
            entry.Property(x => x.EndedAt).IsModified = false;
            entry.Property(x => x.Outcome).OriginalValue = outcome;
            entry.Property(x => x.Outcome).IsModified = false;
        }
    }
}
