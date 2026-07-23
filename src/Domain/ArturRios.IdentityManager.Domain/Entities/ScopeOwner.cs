namespace ArturRios.IdentityManager.Domain.Entities;

/// <summary>
///     Join row linking a <see cref="Scope" /> to a <see cref="Person" /> who owns it (a person
///     with the <c>ScopeAdmin</c> role). A scope may have many owners and a Scope Admin may own
///     many scopes. Not an independently addressable resource, so it carries no <c>PublicId</c>;
///     its composite key is <c>(ScopeId, PersonId)</c>, configured in the persistence layer.
/// </summary>
public class ScopeOwner
{
    /// <summary>Foreign key to the owned <see cref="Scope" /> (internal Id). Part of the composite key.</summary>
    public long ScopeId { get; set; }

    /// <summary>
    ///     Foreign key to the owning <see cref="Person" /> (internal Id), which must have the
    ///     <c>ScopeAdmin</c> role. Part of the composite key.
    /// </summary>
    public long PersonId { get; set; }

    // Navigation properties

    /// <summary>The owned scope.</summary>
    public Scope Scope { get; set; } = null!;

    /// <summary>The owning person.</summary>
    public Person Person { get; set; } = null!;
}
