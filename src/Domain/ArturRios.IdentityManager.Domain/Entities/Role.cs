using System.ComponentModel.DataAnnotations;
using ArturRios.Data.Relational.Core.Entities;

namespace ArturRios.IdentityManager.Domain.Entities;

/// <summary>
///     A named permission level referenced by a <see cref="Person" /> via its <c>RoleId</c>.
///     The name is one of <c>User</c>, <c>ScopeAdmin</c>, or <c>SystemAdmin</c>.
/// </summary>
public class Role : Entity
{
    /// <summary>
    ///     External identifier, generated on creation and used everywhere the role is addressed
    ///     from outside the database. The internal <see cref="Entity.Id" /> is never exposed to callers.
    /// </summary>
    public Guid PublicId { get; set; } = Guid.NewGuid();

    /// <summary>Role name. Required, unique — <c>User</c>, <c>ScopeAdmin</c>, or <c>SystemAdmin</c>.</summary>
    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    /// <summary>Optional human-readable explanation of the role's purpose and permissions.</summary>
    [MaxLength(500)]
    public string? Description { get; set; }

    // Navigation properties

    /// <summary>Persons classified by this role.</summary>
    public ICollection<Person> Persons { get; set; } = new List<Person>();
}
