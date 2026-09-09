using ArturRios.Data.Relational.Core.Entities;
using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Output;
using ArturRios.Heimdall.Command.Services;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Heimdall.Shared.Retention;
using ArturRios.Mediator.Command.Interfaces;
using ArturRios.Output;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Heimdall.Command.Handlers;

/// <summary>
///     Handles <see cref="AnonymiseExpiredDeletionsCommand" /> (NFR-20): finds logically deleted
///     persons and Google Users whose retention window has run out, removes the dependent rows that
///     hold their data, and overwrites every identifying value on the record itself through
///     <see cref="IdentityAnonymiser" />. All failures are returned as errors on the
///     <see cref="DataOutput{T}" /> rather than thrown.
/// </summary>
/// <remarks>
///     <para>
///         <b>Which deadline applies.</b> The one the record's <c>DeletionKind</c> names. A subject
///         who asked to be erased gets the statutory deadline; an administrative deletion gets the
///         longer reversal window (Data Retention Schedule §4). A record deleted before this
///         mechanism existed carries no kind at all, and is treated as administrative — the longer
///         of the two, because guessing that somebody had requested erasure would be inventing a
///         request that may never have been made.
///     </para>
///     <para>
///         <b>Why the dependents go first.</b> Anonymising the person while leaving their password
///         reset tokens and two-factor configuration in place would erase the name and keep the
///         material: a TOTP secret and a recovery code hash belong to the person, and a reset token
///         addresses the mailbox that was just disowned. The token purge would reach the tokens
///         eventually, on its own much shorter window, but "eventually" is not what an erasure
///         obligation accepts, and it would never reach the two-factor rows at all.
///     </para>
///     <para>
///         <b>What is deliberately left alone.</b> The scope membership and ownership rows, the
///         applications an owner held, and the audit entries naming them. All are foreign keys or
///         accountability records that NFR-07 requires to keep resolving, and none carries an
///         identifying value once the record they point at has been anonymised. The audit trail's
///         own actor reference is #97's, not this pass's — a different mechanism for a table that
///         refuses updates by design.
///     </para>
///     <para>
///         <b>NFR-12 is untouched.</b> Anonymisation changes no <c>SCOPE_OWNER</c> row, so a scope
///         keeps exactly the owners it had. The guarantee is not weakened here because it was
///         already enforced at deletion time: UC-09 refuses to logically delete a scope's last
///         owner, so no record reaching this pass was one.
///     </para>
/// </remarks>
public class AnonymiseExpiredDeletionsCommandHandler(
    IAsyncReadOnlyRepository<Person> personReader,
    IAsyncRepository<Person> personWriter,
    IAsyncReadOnlyRepository<GoogleUser> googleUserReader,
    IAsyncRepository<GoogleUser> googleUserWriter,
    IAsyncReadOnlyRepository<PasswordResetToken> passwordResetTokenReader,
    IAsyncRepository<PasswordResetToken> passwordResetTokenWriter,
    IAsyncReadOnlyRepository<EmailVerificationToken> emailVerificationTokenReader,
    IAsyncRepository<EmailVerificationToken> emailVerificationTokenWriter,
    IAsyncReadOnlyRepository<TwoFactorAuth> twoFactorAuthReader,
    IAsyncRepository<TwoFactorAuth> twoFactorAuthWriter,
    DataRetentionOptions retention)
    : ICommandHandlerAsync<AnonymiseExpiredDeletionsCommand, AnonymiseExpiredDeletionsCommandOutput>
{
    /// <summary>
    ///     <c>DeletionKinds.SubjectRequested</c> as a plain <c>int</c>. The comparison happens inside
    ///     an expression EF Core translates to SQL, and a cast of an enum member there is one more
    ///     thing for the provider to have an opinion about than it needs to be.
    /// </summary>
    private const int SubjectRequested = (int)DeletionKinds.SubjectRequested;

    /// <remarks>
    ///     The two queries below are near-identical and are still written out twice. A generic
    ///     helper over an interface both entities implement would read better and would not
    ///     translate: EF Core maps the properties of the entity types, and an expression tree
    ///     referencing an interface's members gives it member accesses it cannot resolve to columns.
    ///     The failure mode is a silent client-side evaluation of the whole table, or an exception —
    ///     neither worth the six lines saved.
    /// </remarks>
    public async Task<DataOutput<AnonymiseExpiredDeletionsCommandOutput?>> HandleAsync(
        AnonymiseExpiredDeletionsCommand command)
    {
        var output = DataOutput<AnonymiseExpiredDeletionsCommandOutput?>.New;
        var now = DateTime.UtcNow;

        // Two cutoffs rather than one, because the deadline depends on why the record was deleted.
        var erasureCutoff = now - retention.SubjectErasureDeadline;
        var administrativeCutoff = now - retention.AdministrativeDeletionWindow;

        var persons = await personReader.Query()
            .Where(person => person.IsDeleted
                             && person.AnonymisedAt == null
                             && person.DeletedAt != null
                             && (person.DeletionKind == SubjectRequested
                                 ? person.DeletedAt <= erasureCutoff
                                 : person.DeletedAt <= administrativeCutoff))
            .OrderBy(person => person.DeletedAt)
            .Take(retention.PurgeBatchSize)
            .ToListAsync();

        var googleUsers = await googleUserReader.Query()
            .Where(googleUser => googleUser.IsDeleted
                                 && googleUser.AnonymisedAt == null
                                 && googleUser.DeletedAt != null
                                 && (googleUser.DeletionKind == SubjectRequested
                                     ? googleUser.DeletedAt <= erasureCutoff
                                     : googleUser.DeletedAt <= administrativeCutoff))
            .OrderBy(googleUser => googleUser.DeletedAt)
            .Take(retention.PurgeBatchSize)
            .ToListAsync();

        if (persons.Count == 0 && googleUsers.Count == 0)
        {
            return Success(output, 0, 0, 0);
        }

        var errors = new List<string>();
        var dependentsRemoved = 0;

        if (persons.Count > 0)
        {
            var (removed, dependentErrors) = await RemovePersonDependentsAsync(persons);

            dependentsRemoved += removed;
            errors.AddRange(dependentErrors);
        }

        // A failed dependent removal stops the run before the record is overwritten. Anonymising
        // anyway would leave the person's two-factor secret behind with nothing left to connect it
        // to a name — material kept past its purpose, and unreachable by the next run, which selects
        // on records not yet anonymised.
        if (errors.Count > 0)
        {
            return output.WithErrors(errors);
        }

        foreach (var person in persons)
        {
            IdentityAnonymiser.Anonymise(person, now);
        }

        foreach (var googleUser in googleUsers)
        {
            IdentityAnonymiser.Anonymise(googleUser, now);
        }

        errors.AddRange(await SaveAsync(persons, personWriter));
        errors.AddRange(await SaveAsync(googleUsers, googleUserWriter));

        return errors.Count > 0
            ? output.WithErrors(errors)
            : Success(output, persons.Count, googleUsers.Count, dependentsRemoved);
    }

    /// <summary>
    ///     Permanently removes the rows that hold data belonging to the persons being anonymised:
    ///     their password reset and email verification tokens, and their two-factor configuration —
    ///     whose <c>ON DELETE CASCADE</c> foreign keys take the recovery codes and email codes with
    ///     it.
    /// </summary>
    private async Task<(int Removed, IEnumerable<string> Errors)> RemovePersonDependentsAsync(
        IReadOnlyCollection<Person> persons)
    {
        var personIds = persons.Select(person => person.Id).ToList();
        var removed = 0;
        var errors = new List<string>();

        var passwordResetTokenIds = await passwordResetTokenReader.Query()
            .Where(token => personIds.Contains(token.PersonId))
            .Select(token => token.Id)
            .ToListAsync();

        var emailVerificationTokenIds = await emailVerificationTokenReader.Query()
            .Where(token => personIds.Contains(token.PersonId))
            .Select(token => token.Id)
            .ToListAsync();

        var twoFactorAuthIds = await twoFactorAuthReader.Query()
            .Where(configuration => personIds.Contains(configuration.PersonId))
            .Select(configuration => configuration.Id)
            .ToListAsync();

        Collect(await DeleteAllAsync(passwordResetTokenIds, passwordResetTokenWriter));
        Collect(await DeleteAllAsync(emailVerificationTokenIds, emailVerificationTokenWriter));
        Collect(await DeleteAllAsync(twoFactorAuthIds, twoFactorAuthWriter));

        return (removed, errors);

        void Collect((int Removed, IEnumerable<string> Errors) result)
        {
            removed += result.Removed;
            errors.AddRange(result.Errors);
        }
    }

    /// <summary>
    ///     Permanently removes the entities with the given ids, or does nothing when there are none.
    ///     Reports how many rows were actually removed, which is what the delete reported rather
    ///     than what was selected.
    /// </summary>
    private static async Task<(int Removed, IEnumerable<string> Errors)> DeleteAllAsync<T>(
        IReadOnlyCollection<long> ids, IAsyncRepository<T> writer) where T : Entity
    {
        if (ids.Count == 0)
        {
            return (0, []);
        }

        var deletion = await writer.DeleteRangeAsync(ids);

        return deletion.Success
            ? (deletion.Data?.Count() ?? 0, [])
            : (0, deletion.Errors);
    }

    private static async Task<IEnumerable<string>> SaveAsync<T>(
        IReadOnlyCollection<T> records, IAsyncRepository<T> writer) where T : Entity
    {
        if (records.Count == 0)
        {
            return [];
        }

        var update = await writer.UpdateRangeAsync(records);

        return update.Success ? [] : update.Errors;
    }

    private static DataOutput<AnonymiseExpiredDeletionsCommandOutput?> Success(
        DataOutput<AnonymiseExpiredDeletionsCommandOutput?> output,
        int persons,
        int googleUsers,
        int dependents) =>
        output
            .WithData(new AnonymiseExpiredDeletionsCommandOutput
            {
                PersonsAnonymised = persons,
                GoogleUsersAnonymised = googleUsers,
                DependentsRemoved = dependents
            })
            .WithMessage(RetentionMessages.ExpiredDeletionsAnonymised);
}
