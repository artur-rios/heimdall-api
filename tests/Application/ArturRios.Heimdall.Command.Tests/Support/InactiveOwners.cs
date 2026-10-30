using ArturRios.Heimdall.Domain.Entities;

namespace ArturRios.Heimdall.Command.Tests.Support;

/// <summary>The two ways a person who is not logically deleted is still not active (<see cref="Person.Active" />).</summary>
public enum InactiveOwner
{
    /// <summary>Processing is restricted (UC-44).</summary>
    Restricted,

    /// <summary>An erasure was requested and is blocked, so the person is not yet suspended (UC-42).</summary>
    ErasureRequested
}

/// <summary>Puts a person in one of the <see cref="InactiveOwner" /> states, for the NFR-12 guard tests (#211).</summary>
public static class InactiveOwners
{
    public static Person Make(Person person, InactiveOwner how)
    {
        switch (how)
        {
            case InactiveOwner.Restricted:
                person.ProcessingRestrictedAt = DateTime.UtcNow.AddDays(-1);
                person.RestrictionGround = 1;
                break;
            case InactiveOwner.ErasureRequested:
                person.ErasureRequestedAt = DateTime.UtcNow.AddDays(-1);
                person.ErasureDueAt = DateTime.UtcNow.AddDays(29);
                person.ErasureBlockedReason = "blocked";
                break;
        }

        return person;
    }
}
