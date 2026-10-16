namespace ArturRios.Heimdall.Domain.Persistence;

/// <summary>
///     The outcome of reserving one self-service account email (FR-PE-16) — see
///     <see cref="IAtomicWrites.TryReserveAccountEmailAsync" />.
/// </summary>
public enum AccountEmailReservation
{
    /// <summary>The send is reserved and may go out.</summary>
    Reserved,

    /// <summary>One was sent within the cooldown; nothing may be sent yet.</summary>
    CoolingDown,

    /// <summary>The day's allowance is spent; nothing may be sent until the window ends.</summary>
    DailyLimitReached
}
