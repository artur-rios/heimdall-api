using System.Security.Cryptography;
using System.Text;
using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Output;
using ArturRios.Heimdall.Command.Services;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Mediator.Command.Interfaces;
using ArturRios.Output;
using ArturRios.Util.Random;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Heimdall.Command.Handlers;

/// <summary>
///     Handles <see cref="RegenerateRecoveryCodesCommand" /> (UC-40, FR-2F-12): loads the caller's
///     active <see cref="TwoFactorAuth" /> row (AF-40a), verifies the submitted second factor through
///     the same <see cref="ITwoFactorFactorVerifier" /> <c>VerifyTwoFactorAuthCommandHandler</c>
///     (UC-38) and <c>DisableTwoFactorAuthCommandHandler</c> (UC-39) use (AF-40b), and — only once
///     that check passes — permanently removes every existing recovery code row for the
///     configuration, including any still unused, and issues ten fresh ones, hashed the same way
///     <c>ConfirmTwoFactorAuthCommandHandler</c> (UC-37) hashes the codes it issues.
/// </summary>
/// <remarks>
///     Per the Use Case Specification Document's closing note on UC-40: regeneration replaces the
///     whole set at once rather than topping up the used ones back to ten — a partial refill would
///     leave old, already-distributed codes valid alongside new ones, defeating the point of rotating
///     them. The matched row the verifier returns (an email code or a recovery code) never needs
///     marking used here — every existing recovery code is about to be deleted regardless, and an
///     email code that authorized a regeneration carries no further significance once the request
///     succeeds.
/// </remarks>
public class RegenerateRecoveryCodesCommandHandler(
    IAsyncReadOnlyRepository<Person> personReader,
    IAsyncReadOnlyRepository<TwoFactorAuth> twoFactorReader,
    IAsyncReadOnlyRepository<TwoFactorRecoveryCode> recoveryCodeReader,
    IAsyncRepository<TwoFactorRecoveryCode> recoveryCodeWriter,
    ITwoFactorFactorVerifier factorVerifier)
    : ICommandHandlerAsync<RegenerateRecoveryCodesCommand, RegenerateRecoveryCodesCommandOutput>
{
    private const int RecoveryCodeCount = 10;
    private const int RecoveryCodeSegmentLength = 4;

    public async Task<DataOutput<RegenerateRecoveryCodesCommandOutput?>> HandleAsync(
        RegenerateRecoveryCodesCommand command)
    {
        var output = DataOutput<RegenerateRecoveryCodesCommandOutput?>.New;

        var person = await personReader.Query()
            .FirstOrDefaultAsync(x => x.PublicId == command.ActingPersonId && !x.IsDeleted);

        var twoFactorAuth = person is null
            ? null
            : await twoFactorReader.Query()
                .FirstOrDefaultAsync(x => x.PersonId == person.Id && x.IsActive);

        // AF-40a: nothing to regenerate — whether because two-factor was never activated, or because
        // the caller could not be resolved as a live person at all (a Google User, or a bearer token
        // naming a person since hard deleted), neither of which could ever hold an active row.
        if (person is null || twoFactorAuth is null)
        {
            return output.WithError(TwoFactorMessages.NotActive);
        }

        // UC-40 step 2 (AF-40b): the same second-factor check UC-38 and UC-39 make.
        var verification = await factorVerifier.VerifyAsync(twoFactorAuth, command.Code, command.RecoveryCode);

        if (!verification.Matched)
        {
            return output.WithError(TwoFactorMessages.FactorInvalid);
        }

        // UC-40 step 3: every existing recovery code row is replaced, including any still unused.
        var existingRecoveryCodeIds = await recoveryCodeReader.Query()
            .Where(x => x.TwoFactorAuthId == twoFactorAuth.Id)
            .Select(x => x.Id)
            .ToListAsync();

        if (existingRecoveryCodeIds.Count > 0)
        {
            var deletion = await recoveryCodeWriter.DeleteRangeAsync(existingRecoveryCodeIds);

            if (!deletion.Success)
            {
                return output.WithErrors(deletion.Errors);
            }
        }

        // UC-40 steps 4-5 (FR-2F-12): ten fresh recovery codes, hashed at rest, returned once.
        var recoveryCodes = GenerateRecoveryCodes();

        foreach (var recoveryCode in recoveryCodes)
        {
            var creation = await recoveryCodeWriter.CreateAsync(new TwoFactorRecoveryCode
            {
                TwoFactorAuthId = twoFactorAuth.Id, CodeHash = HashRecoveryCode(recoveryCode), Used = false
            });

            if (!creation.Success)
            {
                return output.WithErrors(creation.Errors);
            }
        }

        return output
            .WithData(new RegenerateRecoveryCodesCommandOutput { RecoveryCodes = recoveryCodes })
            .WithMessage(TwoFactorMessages.RecoveryCodesRegenerated);
    }

    /// <summary>
    ///     Generates <see cref="RecoveryCodeCount" /> random, human-typeable recovery codes, formatted
    ///     exactly as <c>ConfirmTwoFactorAuthCommandHandler</c> formats the ones it issues — two
    ///     <see cref="RecoveryCodeSegmentLength" />-character uppercase alphanumeric segments separated
    ///     by a hyphen (e.g. <c>"A1B2-C3D4"</c>) — so a regenerated code is indistinguishable in shape
    ///     from one issued at confirmation (FR-2F-12).
    /// </summary>
    private static List<string> GenerateRecoveryCodes()
    {
        var codes = new List<string>(RecoveryCodeCount);

        for (var i = 0; i < RecoveryCodeCount; i++)
        {
            var raw = CustomRandom.Text(new RandomStringOptions
            {
                Length = RecoveryCodeSegmentLength * 2,
                IncludeDigits = true,
                IncludeUppercase = true,
                IncludeLowercase = false,
                IncludeSpecialCharacters = false
            });

            codes.Add($"{raw[..RecoveryCodeSegmentLength]}-{raw[RecoveryCodeSegmentLength..]}");
        }

        return codes;
    }

    /// <summary>
    ///     Hashes a recovery code for storage, identically to
    ///     <c>ConfirmTwoFactorAuthCommandHandler.HashRecoveryCode</c> — see its remarks for why a
    ///     plain SHA-256 digest, with no per-code salt column, is consistent with §4.10 of the System
    ///     Requirements Document.
    /// </summary>
    private static byte[] HashRecoveryCode(string recoveryCode) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(recoveryCode));
}
