using System.ComponentModel;

namespace ArturRios.IdentityManager.Domain.Enums;

public enum Roles
{
    [Description("The system administrator, with full access")]
    SystemAdmin = 1,

    [Description("Scope administrator, with access for scopes they own")]
    ScopeAdmin = 2,

    [Description("Regular user")]
    User = 3
}
