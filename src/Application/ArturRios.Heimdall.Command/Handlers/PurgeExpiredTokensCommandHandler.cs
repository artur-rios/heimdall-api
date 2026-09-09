using ArturRios.Data.Relational.Core.Entities;
using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Output;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Heimdall.Shared.Retention;
using ArturRios.Mediator.Command.Interfaces;
using ArturRios.Output;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Heimdall.Command.Handlers;

/// <summary>
///     Handles <see cref="PurgeExpiredTokensCommand" /> (NFR-19): removes password reset tokens,
///     email verification tokens, and two-factor email codes whose retention period has run out.
///     All failures are returned as errors on the <see cref="DataOutput{T}" /> rather than thrown.
/// </summary>
/// <remarks>
///     <para>
///         <b>What makes a row purgeable.</b> One condition, <c>ExpiresAt &lt;= now - grace</c>,
///         applied to all three tables. It covers a used row and an expired-unused row alike, and it
///         is the only condition that can be applied to all three: neither
///         <see cref="PasswordResetToken" /> nor <see cref="EmailVerificationToken" /> records when
///         it was consumed, and only <see cref="TwoFactorEmailCode" /> carries a
///         <c>CreatedAt</c>. Expiry is a sound proxy in any case — each of these is issued with a
///         lifetime measured in minutes or hours, so a row's expiry is never far from its issue.
///     </para>
///     <para>
///         <b>Why a live token is never at risk.</b> The cutoff is strictly in the past, so a token
///         that has not yet expired cannot match however the grace period is configured — the
///         options type refuses a zero or negative one. A person mid-recovery is never affected by
///         a run.
///     </para>
///     <para>
///         <b>Running on more than one instance.</b> NFR-06 requires that no instance assume it is
///         alone, and two purges may overlap. Nothing is claimed or locked: each run selects a
///         batch, and <c>DeleteRangeAsync</c> re-reads those ids and removes what still exists, so
///         an instance whose rows another already deleted simply removes fewer than it selected and
///         reports the true count. That is the whole of the coordination, and it is enough because
///         deleting an already-deleted row is the one concurrent outcome this pass can have.
///     </para>
/// </remarks>
public class PurgeExpiredTokensCommandHandler(
    IAsyncReadOnlyRepository<PasswordResetToken> passwordResetTokenReader,
    IAsyncRepository<PasswordResetToken> passwordResetTokenWriter,
    IAsyncReadOnlyRepository<EmailVerificationToken> emailVerificationTokenReader,
    IAsyncRepository<EmailVerificationToken> emailVerificationTokenWriter,
    IAsyncReadOnlyRepository<TwoFactorEmailCode> twoFactorEmailCodeReader,
    IAsyncRepository<TwoFactorEmailCode> twoFactorEmailCodeWriter,
    DataRetentionOptions retention)
    : ICommandHandlerAsync<PurgeExpiredTokensCommand, PurgeExpiredTokensCommandOutput>
{
    public async Task<DataOutput<PurgeExpiredTokensCommandOutput?>> HandleAsync(PurgeExpiredTokensCommand command)
    {
        var output = DataOutput<PurgeExpiredTokensCommandOutput?>.New;
        var cutoff = DateTime.UtcNow - retention.SingleUseTokenGrace;

        // Oldest first, so a backlog drains in the order it accumulated and no row can be starved by
        // fresher ones arriving ahead of it.
        var passwordResetTokens = await PurgeAsync(
            passwordResetTokenReader.Query()
                .Where(token => token.ExpiresAt <= cutoff)
                .OrderBy(token => token.ExpiresAt),
            passwordResetTokenWriter);

        var emailVerificationTokens = await PurgeAsync(
            emailVerificationTokenReader.Query()
                .Where(token => token.ExpiresAt <= cutoff)
                .OrderBy(token => token.ExpiresAt),
            emailVerificationTokenWriter);

        var twoFactorEmailCodes = await PurgeAsync(
            twoFactorEmailCodeReader.Query()
                .Where(code => code.ExpiresAt <= cutoff)
                .OrderBy(code => code.ExpiresAt),
            twoFactorEmailCodeWriter);

        // Every table is attempted before the run reports, so one failing table does not hide
        // whether the others were cleared — and the next run retries whatever was missed.
        var errors = passwordResetTokens.Errors
            .Concat(emailVerificationTokens.Errors)
            .Concat(twoFactorEmailCodes.Errors)
            .ToList();

        if (errors.Count > 0)
        {
            return output.WithErrors(errors);
        }

        // A run that removed nothing is a success: most runs find nothing to do, and a failed
        // outcome would fill the audit trail with refusals that never happened.
        return output
            .WithData(new PurgeExpiredTokensCommandOutput
            {
                PasswordResetTokensPurged = passwordResetTokens.Count,
                EmailVerificationTokensPurged = emailVerificationTokens.Count,
                TwoFactorEmailCodesPurged = twoFactorEmailCodes.Count
            })
            .WithMessage(RetentionMessages.ExpiredTokensPurged);
    }

    /// <summary>
    ///     Removes at most <c>PurgeBatchSize</c> of the candidates and reports how many rows were
    ///     actually removed, or the persistence errors if the delete failed.
    /// </summary>
    private async Task<(int Count, IEnumerable<string> Errors)> PurgeAsync<T>(
        IQueryable<T> candidates,
        IAsyncRepository<T> writer) where T : Entity
    {
        var ids = await candidates
            .Take(retention.PurgeBatchSize)
            .Select(entity => entity.Id)
            .ToListAsync();

        if (ids.Count == 0)
        {
            return (0, []);
        }

        var deletion = await writer.DeleteRangeAsync(ids);

        // The count comes from what the delete reported removing, not from what was selected: they
        // differ when another instance got there first, and the trail should record what this run
        // did rather than what it intended.
        return deletion.Success
            ? (deletion.Data?.Count() ?? 0, [])
            : (0, deletion.Errors);
    }
}
