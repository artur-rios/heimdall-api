using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Output;
using ArturRios.Heimdall.Command.Services;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Mediator.Command.Interfaces;
using ArturRios.Output;
using Microsoft.EntityFrameworkCore;
using Application = ArturRios.Heimdall.Domain.Entities.Application;

namespace ArturRios.Heimdall.Command.Handlers;

/// <summary>
///     Handles <see cref="ExportMyDataCommand" /> (UC-41): assembles everything held about the
///     authenticated caller into one document. All failures are returned as errors on the
///     <see cref="DataOutput{T}" /> rather than thrown.
/// </summary>
/// <remarks>
///     <para>
///         <b>The subject is the token's, never the request's.</b> Same shape as UC-42: no path
///         parameter and no body field names an identity, so there is no way to ask for somebody
///         else's data. The rule is enforced by the absence of the parameter rather than by a check.
///     </para>
///     <para>
///         <b>A deleted identity can still export.</b> The lookups omit the <c>!IsDeleted</c> filter
///         that most handlers apply. Somebody suspended pending erasure has more reason to want a
///         copy than anyone, and Art. 15 is not conditional on the account being in good standing.
///         An anonymised record is a different matter — there is nothing left that relates to a
///         person — but it also cannot authenticate, so it never reaches here.
///     </para>
///     <para>
///         <b>What is deliberately absent.</b> Audit entries whose attribution has been cleared
///         under NFR-21 do not appear, and cannot: once cleared, nothing connects them to this
///         person. Saying so on the document would itself be a re-identification, so the export
///         reports what it can still find and the retention section explains why older entries stop
///         being findable.
///     </para>
/// </remarks>
public class ExportMyDataCommandHandler(
    IAsyncReadOnlyRepository<Person> personReader,
    IAsyncReadOnlyRepository<GoogleUser> googleUserReader,
    IAsyncReadOnlyRepository<Scope> scopeReader,
    IAsyncReadOnlyRepository<Application> applicationReader,
    IAsyncReadOnlyRepository<TwoFactorAuth> twoFactorReader,
    IAsyncReadOnlyRepository<TwoFactorRecoveryCode> recoveryCodeReader,
    IAsyncReadOnlyRepository<AuditLog> auditReader)
    : ICommandHandlerAsync<ExportMyDataCommand, DataExportCommandOutput>
{
    public async Task<DataOutput<DataExportCommandOutput?>> HandleAsync(ExportMyDataCommand command)
    {
        var output = DataOutput<DataExportCommandOutput?>.New;

        var person = await personReader.Query()
            .Include(x => x.ScopeMembership)
            .Include(x => x.ScopeOwnerships)
            .FirstOrDefaultAsync(x => x.PublicId == command.ActingPersonId);

        if (person is not null)
        {
            return output
                .WithData(await ExportPersonAsync(person))
                .WithMessage(ErasureMessages.DataExported);
        }

        var googleUser = await googleUserReader.Query()
            .FirstOrDefaultAsync(x => x.PublicId == command.ActingPersonId);

        return googleUser is not null
            ? output.WithData(await ExportGoogleUserAsync(googleUser)).WithMessage(ErasureMessages.DataExported)
            : output.WithError(ErasureMessages.NotEligible);
    }

    private async Task<DataExportCommandOutput> ExportPersonAsync(Person person)
    {
        var twoFactor = await twoFactorReader.Query()
            .FirstOrDefaultAsync(x => x.PersonId == person.Id);

        var unusedRecoveryCodes = twoFactor is null
            ? 0
            : await recoveryCodeReader.Query()
                .CountAsync(x => x.TwoFactorAuthId == twoFactor.Id && !x.Used);

        var applications = await applicationReader.Query()
            .Where(x => x.OwnerId == person.Id)
            .Select(x => new DataExportApplication
            {
                Id = x.PublicId,
                Name = x.Name,
                ScopeId = x.Scope.PublicId,
                IsDeleted = x.IsDeleted
            })
            .ToListAsync();

        // Internal scope ids are translated to public ones: NFR-15 applies to an export as much as
        // to any other response.
        var scopeIds = person.ScopeOwnerships.Select(ownership => ownership.ScopeId).ToList();

        if (person.ScopeMembership is not null)
        {
            scopeIds.Add(person.ScopeMembership.ScopeId);
        }

        var publicScopeIds = await scopeReader.Query()
            .Where(scope => scopeIds.Contains(scope.Id))
            .Select(scope => new { scope.Id, scope.PublicId })
            .ToListAsync();

        Guid? Public(long internalId) =>
            publicScopeIds.FirstOrDefault(scope => scope.Id == internalId)?.PublicId;

        var (auditEntries, truncated) = await AuditEntriesAsync(person.PublicId);

        return new DataExportCommandOutput
        {
            ExportedAt = DateTime.UtcNow,
            Subject = new DataExportSubject
            {
                Id = person.PublicId,
                Kind = "Person",
                Name = person.Name,
                Email = person.Email,
                EmailVerified = person.EmailVerified,
                Role = (int)person.RoleId,
                IsDeleted = person.IsDeleted,
                CreatedAt = person.CreatedAt,
                UpdatedAt = person.UpdatedAt
            },
            Security = new DataExportSecurity
            {
                PasswordSet = person.PasswordHash.Length > 0,
                TwoFactorActive = twoFactor?.IsActive ?? false,
                TwoFactorAppEnabled = twoFactor?.AppEnabled ?? false,
                TwoFactorEmailEnabled = twoFactor?.EmailEnabled ?? false,
                UnusedRecoveryCodes = unusedRecoveryCodes,
                FailedLoginAttempts = person.FailedLoginAttempts,
                LockedOutUntil = person.LockedOutUntil
            },
            Processing = Processing(person.LegalBasis, person.PrivacyNoticeVersion, person.BasisRecordedAt),
            Erasure = new DataExportErasure
            {
                IsDeleted = person.IsDeleted,
                DeletedAt = person.DeletedAt,
                DeletionKind = person.DeletionKind,
                AnonymisedAt = person.AnonymisedAt,
                ErasureRequestedAt = person.ErasureRequestedAt,
                ErasureDueAt = person.ErasureDueAt,
                ErasureBlockedReason = person.ErasureBlockedReason
            },
            ScopeMemberships = person.ScopeMembership is null
                ? []
                : [Public(person.ScopeMembership.ScopeId) ?? Guid.Empty],
            ScopeOwnerships = person.ScopeOwnerships
                .Select(ownership => Public(ownership.ScopeId) ?? Guid.Empty)
                .ToList(),
            Applications = applications,
            AuditEntries = auditEntries,
            AuditEntriesTruncated = truncated,
            Withheld = DataExportDisclosure.Withheld,
            Recipients = DataExportDisclosure.Recipients,
            Retention = DataExportDisclosure.Retention
        };
    }

    private async Task<DataExportCommandOutput> ExportGoogleUserAsync(GoogleUser googleUser)
    {
        var scope = await scopeReader.Query()
            .FirstOrDefaultAsync(x => x.Id == googleUser.ScopeId);

        var (auditEntries, truncated) = await AuditEntriesAsync(googleUser.PublicId);

        return new DataExportCommandOutput
        {
            ExportedAt = DateTime.UtcNow,
            Subject = new DataExportSubject
            {
                Id = googleUser.PublicId,
                Kind = "GoogleUser",
                Name = googleUser.Name,
                Email = googleUser.Email,
                EmailVerified = googleUser.EmailVerified,
                // FR-GO-04 makes a Google User permanently User-equivalent, so there is no stored
                // role to report — reporting one would imply a choice that does not exist.
                Role = null,
                GoogleId = googleUser.GoogleId,
                ProfilePictureUrl = googleUser.ProfilePictureUrl,
                IsDeleted = googleUser.IsDeleted,
                CreatedAt = googleUser.CreatedAt,
                UpdatedAt = googleUser.UpdatedAt
            },
            // A Google User has no password and cannot enable two-factor (FR-2F-01), so every field
            // here is false by construction rather than by happening to be unset.
            Security = new DataExportSecurity { PasswordSet = false },
            Processing = Processing(
                googleUser.LegalBasis, googleUser.PrivacyNoticeVersion, googleUser.BasisRecordedAt),
            Erasure = new DataExportErasure
            {
                IsDeleted = googleUser.IsDeleted,
                DeletedAt = googleUser.DeletedAt,
                DeletionKind = googleUser.DeletionKind,
                AnonymisedAt = googleUser.AnonymisedAt,
                ErasureRequestedAt = googleUser.ErasureRequestedAt,
                ErasureDueAt = googleUser.ErasureDueAt,
                ErasureBlockedReason = googleUser.ErasureBlockedReason
            },
            ScopeMemberships = scope is null ? [] : [scope.PublicId],
            AuditEntries = auditEntries,
            AuditEntriesTruncated = truncated,
            Withheld = DataExportDisclosure.Withheld,
            Recipients = DataExportDisclosure.Recipients,
            Retention = DataExportDisclosure.Retention
        };
    }

    private async Task<(List<DataExportAuditEntry> Entries, bool Truncated)> AuditEntriesAsync(Guid subjectId)
    {
        // One more than the cap, so reaching it is detectable without a second count query.
        var entries = await auditReader.Query()
            .Where(entry => entry.ActorPersonId == subjectId)
            .OrderByDescending(entry => entry.CreatedAt)
            .Take(DataExportDisclosure.MaximumAuditEntries + 1)
            .Select(entry => new DataExportAuditEntry
            {
                Id = entry.PublicId,
                Action = entry.Action,
                TargetId = entry.TargetId,
                Succeeded = entry.Succeeded,
                FailureReason = entry.FailureReason,
                CreatedAt = entry.CreatedAt
            })
            .ToListAsync();

        var truncated = entries.Count > DataExportDisclosure.MaximumAuditEntries;

        return (truncated ? entries.Take(DataExportDisclosure.MaximumAuditEntries).ToList() : entries, truncated);
    }

    private static DataExportProcessing Processing(int basis, string? noticeVersion, DateTime? recordedAt) =>
        new()
        {
            LegalBasis = basis,
            LegalBasisName = Enum.IsDefined((LegalBases)basis)
                ? ((LegalBases)basis).ToString()
                : nameof(LegalBases.Unrecorded),
            PrivacyNoticeVersion = noticeVersion,
            RecordedAt = recordedAt
        };
}
