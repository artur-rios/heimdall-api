using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Util.Test.Mock;

namespace ArturRios.Heimdall.Command.Tests.Support;

/// <summary>
///     The scopes, persons and invitations the invitation handlers read, over fakes that share their instances
///     with <see cref="Store" />, plus a notice sender that records instead of sending.
/// </summary>
public sealed class InvitationWorld
{
    public InvitationWorld(Action<ScopeOwnerInvitation>? beforeConditionalWrite = null) =>
        Store = new InMemoryScopeOwnerInvitations(Invitations, Persons, Scopes)
        {
            BeforeConditionalWrite = beforeConditionalWrite
        };

    public AsyncFakeRepository<Scope, long> Scopes { get; } = new();

    public AsyncFakeRepository<Person, long> Persons { get; } = new();

    public AsyncFakeRepository<ScopeOwnerInvitation, long> Invitations { get; } = new();

    public RecordingScopeInvitationNoticeSender Notices { get; } = new();

    public InMemoryScopeOwnerInvitations Store { get; }

    public async Task<Scope> ScopeAsync(string name = "Acme", bool isDeleted = false)
    {
        var scope = new Scope { PublicId = Guid.NewGuid(), Name = name, IsDeleted = isDeleted };
        await Scopes.CreateAsync(scope);
        return scope;
    }

    public async Task<Person> PersonAsync(
        string name, Roles role = Roles.ScopeAdmin, bool isDeleted = false, params Scope[] owns)
    {
        var person = new Person
        {
            PublicId = Guid.NewGuid(),
            Name = name,
            Email = $"{name.ToLowerInvariant()}-{Guid.NewGuid():N}@test.local",
            RoleId = (long)role,
            IsDeleted = isDeleted,
            ScopeOwnerships = [.. owns.Select(scope => new ScopeOwner { ScopeId = scope.Id })]
        };
        await Persons.CreateAsync(person);
        return person;
    }

    public async Task<ScopeOwnerInvitation> InvitationAsync(
        Scope scope, Person invitee, Person? invitedBy, DateTime? createdAt = null, DateTime? expiresAt = null,
        DateTime? endedAt = null, ScopeOwnerInvitationOutcomes? outcome = null)
    {
        var now = DateTime.UtcNow;
        var invitation = new ScopeOwnerInvitation
        {
            PublicId = Guid.NewGuid(),
            ScopeId = scope.Id,
            Scope = scope,
            InviteePersonId = invitee.Id,
            Invitee = invitee,
            InvitedByPersonId = invitedBy?.Id,
            InvitedBy = invitedBy,
            CreatedAt = createdAt ?? now.AddMinutes(-10),
            ExpiresAt = expiresAt ?? now.AddDays(7),
            EndedAt = endedAt,
            Outcome = outcome
        };
        await Invitations.CreateAsync(invitation);
        return invitation;
    }
}
