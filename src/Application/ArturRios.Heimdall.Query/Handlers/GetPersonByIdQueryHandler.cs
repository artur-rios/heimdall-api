using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Query.Input;
using ArturRios.Heimdall.Query.Output;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Heimdall.Shared.Services;
using ArturRios.Mediator.Query.Interfaces;
using ArturRios.Output;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Heimdall.Query.Handlers;

/// <summary>
///     Handles <see cref="GetPersonByIdQuery" /> (UC-07, FR-PE-03): retrieves a person by their
///     <c>PublicId</c>, excluding logically deleted persons unless explicitly requested (FR-PE-08),
///     then applies the use case's per-actor visibility rule. A missing person is AF-07a
///     (<c>PersonNotFound</c>); a person the caller may not see is AF-07b
///     (<c>NotAuthorizedToViewPerson</c>). Both are returned as errors rather than thrown. A Scope
///     Admin reading somebody else sees only the ownerships of scopes they own too (FR-PE-19).
/// </summary>
public class GetPersonByIdQueryHandler(IAsyncReadOnlyRepository<Person, long> personReader)
    : IQueryHandlerAsync<GetPersonByIdQuery, PersonOutput>
{
    /// <summary>
    ///     The person plus the internal ids the visibility rule needs. Internal ids never reach the
    ///     caller — only <see cref="Output" /> is returned.
    /// </summary>
    private sealed class PersonProjection
    {
        public Guid PublicId { get; init; }

        public long RoleId { get; init; }

        public long? MembershipScopeId { get; init; }

        public List<OwnedScope> OwnedScopes { get; init; } = [];

        public PersonOutput Output { get; init; } = null!;
    }

    /// <summary>
    ///     One scope the person owns, by both ids: the internal one to compare, the public one to
    ///     report.
    /// </summary>
    private sealed class OwnedScope
    {
        public long ScopeId { get; init; }

        public Guid PublicId { get; init; }
    }

    public async Task<DataOutput<PersonOutput?>> HandleAsync(GetPersonByIdQuery query, CancellationToken cancellationToken = default)
    {
        var output = DataOutput<PersonOutput?>.New;

        var person = await personReader.Query()
            .Where(x => x.PublicId == query.Id && (query.IncludeDeleted || !x.IsDeleted))
            .Select(x => new PersonProjection
            {
                PublicId = x.PublicId,
                RoleId = x.RoleId,
                MembershipScopeId = x.ScopeMembership != null ? x.ScopeMembership.ScopeId : null,
                OwnedScopes = x.ScopeOwnerships
                    .Select(ownership => new OwnedScope { ScopeId = ownership.ScopeId, PublicId = ownership.Scope.PublicId })
                    .ToList(),
                Output = new PersonOutput
                {
                    Id = x.PublicId,
                    Name = x.Name,
                    Email = x.Email,
                    Role = (int)x.RoleId,
                    EmailVerified = x.EmailVerified,
                    TwoFactorEnabled = x.TwoFactorAuth != null && x.TwoFactorAuth.IsActive,
                    IsDeleted = x.IsDeleted,
                    ScopeId = x.ScopeMembership != null ? x.ScopeMembership.Scope.PublicId : null,
                    CreatedAt = x.CreatedAt,
                    UpdatedAt = x.UpdatedAt
                }
            })
            .FirstOrDefaultAsync();

        // AF-07a: no such person (or it is logically deleted and was not explicitly requested).
        if (person is null)
        {
            return output.WithError(PersonMessages.PersonNotFound);
        }

        // A System Admin and the person themselves see every ownership; anyone else only those of
        // the scopes they own (FR-PE-19).
        var visibleScopeIds = query.ActingPersonId == person.PublicId
            ? null
            : await OwnedScopeVisibility.ScopesVisibleToAsync(personReader, query.ActingRole, query.ActingPersonId);

        // AF-07b: the caller is not allowed to see this person.
        if (!MayView(query, person, visibleScopeIds))
        {
            return output.WithError(PersonMessages.NotAuthorizedToViewPerson);
        }

        person.Output.OwnedScopeIds = person.OwnedScopes
            .Where(scope => visibleScopeIds is null || visibleScopeIds.Contains(scope.ScopeId))
            .Select(scope => scope.PublicId)
            .ToList();

        return output
            .WithData(person.Output)
            .WithMessage(PersonMessages.PersonRetrievedSuccessfully);
    }

    /// <summary>
    ///     UC-07 step 2: a System Admin sees anyone; anyone sees themselves; a Scope Admin sees the
    ///     Users of the scopes they own and the Scope Admins co-owning those scopes. Everything else
    ///     is denied — in particular a User seeing another person, and a Scope Admin seeing a System
    ///     Admin or an unrelated Scope Admin. <paramref name="ownedScopeIds" /> are the scopes the
    ///     actor owns, or <c>null</c> for a System Admin or a self read.
    /// </summary>
    private static bool MayView(GetPersonByIdQuery query, PersonProjection person, IReadOnlyCollection<long>? ownedScopeIds)
    {
        if (query.ActingRole == (int)Roles.SystemAdmin || query.ActingPersonId == person.PublicId)
        {
            return true;
        }

        if (query.ActingRole != (int)Roles.ScopeAdmin || ownedScopeIds is null)
        {
            return false;
        }

        // ownedScopeIds are the actor's own ownerships. A logically deleted actor owns nothing: they
        // can no longer authenticate (UC-11 AF-11c), so a token issued before their deletion must
        // not keep granting reads until it expires.
        if (person.RoleId == (long)Roles.User)
        {
            return person.MembershipScopeId is not null && ownedScopeIds.Contains(person.MembershipScopeId.Value);
        }

        return person.RoleId == (long)Roles.ScopeAdmin &&
               person.OwnedScopes.Any(scope => ownedScopeIds.Contains(scope.ScopeId));
    }
}
