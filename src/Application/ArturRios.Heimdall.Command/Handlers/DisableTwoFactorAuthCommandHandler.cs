using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Output;
using ArturRios.Heimdall.Command.Services;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Mediator.Command.Interfaces;
using ArturRios.Output;
using ArturRios.Util.Hashing;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Heimdall.Command.Handlers;

/// <summary>
///     Handles <see cref="DisableTwoFactorAuthCommand" /> (UC-39, FR-2F-11): loads the caller's
///     active <see cref="TwoFactorAuth" /> row (AF-39a), verifies the submitted password against the
///     stored hash and salt exactly as <c>LoginCommandHandler</c> does (AF-39b), verifies the
///     submitted second factor through the same <see cref="ITwoFactorFactorVerifier" />
///     <c>VerifyTwoFactorAuthCommandHandler</c> (UC-38) uses (AF-39c), and — only once both checks
///     pass — permanently deletes the row, cascading at the database level to its recovery codes and
///     email codes.
/// </summary>
/// <remarks>
///     Unlike UC-38, where a wrong code and a reused recovery code are made deliberately
///     indistinguishable from one another, UC-39 keeps the password check (AF-39b) and the second
///     factor check (AF-39c) as two separate alternative flows, each answering its own 401 — the
///     Use Case Specification Document lists them as distinct conditions rather than collapsing them
///     the way AF-38b/AF-38c are collapsed, so this handler follows that distinction rather than
///     inventing a shared message the spec does not define. Both still answer with the same 401
///     status code, and neither discloses more than "one of these two things was wrong" — a caller
///     who gets AF-39b already knows their password is wrong (they typed it), and AF-39c does not
///     name which factor was expected.
/// </remarks>
public class DisableTwoFactorAuthCommandHandler(
    IAsyncReadOnlyRepository<Person> personReader,
    IAsyncReadOnlyRepository<TwoFactorAuth> twoFactorReader,
    IAsyncRepository<TwoFactorAuth> twoFactorWriter,
    ITwoFactorFactorVerifier factorVerifier)
    : ICommandHandlerAsync<DisableTwoFactorAuthCommand, DisableTwoFactorAuthCommandOutput>
{
    public async Task<DataOutput<DisableTwoFactorAuthCommandOutput?>> HandleAsync(
        DisableTwoFactorAuthCommand command)
    {
        var output = DataOutput<DisableTwoFactorAuthCommandOutput?>.New;

        var person = await personReader.Query()
            .FirstOrDefaultAsync(x => x.PublicId == command.ActingPersonId && !x.IsDeleted);

        var twoFactorAuth = person is null
            ? null
            : await twoFactorReader.Query()
                .FirstOrDefaultAsync(x => x.PersonId == person.Id && x.IsActive);

        // AF-39a: nothing to disable — whether because two-factor was never active, or because the
        // caller could not be resolved as a live person at all (a Google User, or a bearer token
        // naming a person since hard deleted), neither of which could ever hold an active row.
        if (person is null || twoFactorAuth is null)
        {
            return output.WithError(TwoFactorMessages.NotActive);
        }

        // UC-39 step 2 (AF-39b): the same check LoginCommandHandler makes.
        if (!Hash.TextMatches(command.Password, person.PasswordHash, person.Salt))
        {
            return output.WithError(TwoFactorMessages.PasswordMismatch);
        }

        // UC-39 step 3 (AF-39c): the same second-factor check UC-38 makes. The matched row (if any)
        // never needs marking used here — the whole configuration, recovery codes included, is about
        // to be removed regardless of which factor matched.
        var verification = await factorVerifier.VerifyAsync(twoFactorAuth, command.Code, command.RecoveryCode);

        if (!verification.Matched)
        {
            return output.WithError(TwoFactorMessages.FactorInvalid);
        }

        // UC-39 step 4: only once both checks pass. ON DELETE CASCADE (TwoFactorAuthDbMap,
        // TwoFactorRecoveryCodeDbMap, TwoFactorEmailCodeDbMap) removes the recovery codes and email
        // codes at the database level — no explicit child deletion needed.
        var deletion = await twoFactorWriter.DeleteAsync(twoFactorAuth);

        if (!deletion.Success)
        {
            return output.WithErrors(deletion.Errors);
        }

        return output
            .WithData(new DisableTwoFactorAuthCommandOutput { Disabled = true })
            .WithMessage(TwoFactorMessages.Disabled);
    }
}
