namespace ArturRios.Heimdall.Domain.Entities;

/// <summary>
///     Join row linking a <see cref="Scope" /> to a <see cref="Person" /> who belongs to it (a
///     person with the <c>User</c> role). <see cref="PersonId" /> is unique across this table,
///     enforcing that a User belongs to exactly one scope, while a scope may have many users. Not
///     an independently addressable resource, so it carries no <c>PublicId</c>.
/// </summary>
public class ScopeUser
{
    /// <summary>Foreign key to the <see cref="Scope" /> the person belongs to (internal Id). Required.</summary>
    public long ScopeId { get; set; }

    /// <summary>
    ///     Foreign key to the <see cref="Person" /> (internal Id), which must have the <c>User</c>
    ///     role. Required and unique across this table.
    /// </summary>
    public long PersonId { get; set; }

    // Navigation properties

    /// <summary>The scope the person belongs to.</summary>
    public Scope Scope { get; set; } = null!;

    /// <summary>The member person.</summary>
    public Person Person { get; set; } = null!;
}
