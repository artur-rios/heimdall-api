using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Output;
using ArturRios.Heimdall.Command.Services;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Persistence;
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
///     AF-39b (wrong password) and AF-39c (wrong second factor) answer identically —
///     <c>AuthMessages.PasswordOrCodeIncorrect</c>, 401 — so the answer never says which of the two
///     was wrong. Both checks are charged, together, to the account's re-authentication budget
///     (<see cref="ReauthenticationBudget" />, FR-AU-13), and a paused budget is refused the same way,
///     after the same checks, so it does not show either.
/// </remarks>
public class DisableTwoFactorAuthCommandHandler(
    IAsyncReadOnlyRepository<Person, long> personReader,
    IAsyncReadOnlyRepository<TwoFactorAuth, long> twoFactorReader,
    IAsyncRepository<TwoFactorAuth, long> twoFactorWriter,
    ITwoFactorFactorVerifier factorVerifier,
    IAtomicWrites atomicWrites)
    : ICommandHandlerAsync<DisableTwoFactorAuthCommand, DisableTwoFactorAuthCommandOutput>
{
    public async Task<DataOutput<DisableTwoFactorAuthCommandOutput?>> HandleAsync(
        DisableTwoFactorAuthCommand command, CancellationToken cancellationToken = default)
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

        // FR-AU-13: one charge for the request, before either credential is compared. A paused
        // budget still has both compared, as far as they get, and refuses whatever the result.
        var admitted = await ReauthenticationBudget.TryChargeAsync(atomicWrites, person);

        // UC-39 step 2 (AF-39b): the same check LoginCommandHandler makes.
        var passwordMatches =
            await PasswordHashGate.Shared.TextMatchesAsync(command.Password, person.PasswordHash, person.Salt);

        // UC-39 step 3 (AF-39c): the same second-factor check UC-38 makes, answered as AF-39b is — and
        // made whatever the password, so a wrong password takes as long to refuse as a wrong factor
        // does. The matched row (if any) never needs marking used here — the whole configuration,
        // recovery codes included, is about to be removed regardless of which factor matched.
        var verification = await factorVerifier.VerifyAsync(twoFactorAuth, command.Code, command.RecoveryCode);

        if (!passwordMatches || !verification.Matched || !admitted)
        {
            return output.WithError(AuthMessages.PasswordOrCodeIncorrect);
        }

        await ReauthenticationBudget.ClearAsync(atomicWrites, person);

        // FR-AU-11: removing a credential revokes every token issued while it stood, the caller's own
        // included, so the account's sessions after this point are only ones signed in without it.
        // Before the deletion, so a failed one costs an extra sign-in rather than a live old session.
        await atomicWrites.RotateSecurityStampAsync(person);

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
