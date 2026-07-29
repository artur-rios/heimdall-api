using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.IdentityManager.Domain.Entities;
using ArturRios.IdentityManager.Domain.Enums;
using ArturRios.IdentityManager.Query.Input;
using ArturRios.IdentityManager.Query.Output;
using ArturRios.IdentityManager.Shared.Messages;
using ArturRios.Mediator.Query.Interfaces;
using ArturRios.Output;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.IdentityManager.Query.Handlers;

/// <summary>
///     Handles <see cref="GetPersonByIdQuery" /> (UC-07, FR-PE-03): retrieves a person by their
///     <c>PublicId</c>, excluding logically deleted persons unless explicitly requested (FR-PE-08),
///     then applies the use case's per-actor visibility rule. A missing person is AF-07a
///     (<c>PersonNotFound</c>); a person the caller may not see is AF-07b
///     (<c>NotAuthorizedToViewPerson</c>). Both are returned as errors rather than thrown.
/// </summary>
public class GetPersonByIdQueryHandler(IAsyncReadOnlyRepository<Person> personReader)
    : IQueryHandlerAsync<GetPersonByIdQuery, PersonOutput>
{
    /// <summary>
    ///     The person plus the internal ids the visibility rule needs. Internal ids never reach the
    ///     caller — only <see cref="Output" /> is returned.
    /// </summary>
    private sealed class PersonProjection
    {
        public long Id { get; init; }

        public long RoleId { get; init; }

        public long? MembershipScopeId { get; init; }

        public List<long> OwnedScopeInternalIds { get; init; } = [];

        public PersonOutput Output { get; init; } = null!;
    }

    public async Task<DataOutput<PersonOutput?>> HandleAsync(GetPersonByIdQuery query)
    {
        var output = DataOutput<PersonOutput?>.New;

        var person = await personReader.Query()
            .Where(x => x.PublicId == query.Id && (query.IncludeDeleted || !x.IsDeleted))
            .Select(x => new PersonProjection
            {
                Id = x.Id,
                RoleId = x.RoleId,
                MembershipScopeId = x.ScopeMembership != null ? x.ScopeMembership.ScopeId : null,
                OwnedScopeInternalIds = x.ScopeOwnerships.Select(ownership => ownership.ScopeId).ToList(),
                Output = new PersonOutput
                {
                    Id = x.PublicId,
                    Name = x.Name,
                    Email = x.Email,
                    Role = (int)x.RoleId,
                    EmailVerified = x.EmailVerified,
                    IsDeleted = x.IsDeleted,
                    ScopeId = x.ScopeMembership != null ? x.ScopeMembership.Scope.PublicId : null,
                    OwnedScopeIds = x.ScopeOwnerships.Select(ownership => ownership.Scope.PublicId).ToList(),
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

        // AF-07b: the caller is not allowed to see this person.
        if (!await MayViewAsync(query, person))
        {
            return output.WithError(PersonMessages.NotAuthorizedToViewPerson);
        }

        return output
            .WithData(person.Output)
            .WithMessage(PersonMessages.PersonRetrievedSuccessfully);
    }

    /// <summary>
    ///     UC-07 step 2: a System Admin sees anyone; anyone sees themselves; a Scope Admin sees the
    ///     Users of the scopes they own and the Scope Admins co-owning those scopes. Everything else
    ///     is denied — in particular a User seeing another person, and a Scope Admin seeing a System
    ///     Admin or an unrelated Scope Admin.
    /// </summary>
    private async Task<bool> MayViewAsync(GetPersonByIdQuery query, PersonProjection person)
    {
        if (query.ActingRole == (int)Roles.SystemAdmin || query.ActingPersonId == person.Id)
        {
            return true;
        }

        if (query.ActingRole != (int)Roles.ScopeAdmin)
        {
            return false;
        }

        var ownedScopeIds = await personReader.Query()
            .Where(x => x.Id == query.ActingPersonId)
            .SelectMany(x => x.ScopeOwnerships.Select(ownership => ownership.ScopeId))
            .ToListAsync();

        if (person.RoleId == (long)Roles.User)
        {
            return person.MembershipScopeId is not null && ownedScopeIds.Contains(person.MembershipScopeId.Value);
        }

        return person.RoleId == (long)Roles.ScopeAdmin &&
               person.OwnedScopeInternalIds.Any(ownedScopeIds.Contains);
    }
}
