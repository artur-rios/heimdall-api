using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Persistence;

namespace ArturRios.Heimdall.Command.Services;

/// <summary>
///     FR-PE-16: the per-person budget on the account emails a person's own requests can have sent —
///     a verification link asked for again (UC-15), a two-factor setup code (UC-36, email method) and
///     the notice an own email change sends to the previous address (UC-08, FR-PE-15). At most one per
///     <see cref="AdminEmailOptions.Cooldown" />, and at most <see cref="MaxPerDay" /> in each day-long window, which
///     starts at its first send.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why it exists.</b> Each of these can be repeated with nothing but a bearer token (and, for
///         the email change, the account's password), and the address they go to is one the account
///         holder chose without proving it is theirs. A cooldown alone still let one account send an
///         address hundreds of mails a day, and an email change switched back and forth mails the
///         address it leaves every time. One budget across the three bounds the account, not each
///         endpoint.
///     </para>
///     <para>
///         <b>Atomic.</b> The send is reserved by one conditional write before anything is issued or
///         mailed, so a burst of simultaneous requests gets exactly what the budget has left — a
///         check that read the token rows first let every request of a burst through.
///     </para>
///     <para>
///         The window is fixed from its first send, so "a day" is a day-long window, not any 24 hours:
///         five just before a window ends and five just after it are ten within a day. A refusal says
///         when to come back through <see cref="IRetryAfterAdvisor" />. A send reserved and then not
///         made, because a later step failed, is not given back: the cost is one slot, and it fails
///         closed.
///     </para>
///     <para>
///         Administrator-initiated mail (UC-51, UC-52) is not charged here: it keeps
///         <see cref="AdminEmailOptions" />' own per-kind cooldown, and an administrator's mail must
///         stay possible when a person has spent their own day.
///     </para>
/// </remarks>
public static class AccountEmailBudget
{
    /// <summary>The self-service account emails one person may have sent in one <see cref="Window" />.</summary>
    public const int MaxPerDay = 5;

    /// <summary>How long a window lasts, from the first send in it.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromDays(1);

    /// <summary>
    ///     Reserves one send for <paramref name="person" />. Anything but
    ///     <see cref="AccountEmailReservation.Reserved" /> means nothing may be sent.
    /// </summary>
    public static Task<AccountEmailReservation> TryReserveAsync(
        IAtomicWrites atomicWrites, Person person, AdminEmailOptions options)
    {
        var now = DateTime.UtcNow;

        return atomicWrites.TryReserveAccountEmailAsync(person, MaxPerDay, now, now - options.Cooldown, now + Window);
    }

    /// <summary>
    ///     What a reservation would answer, read from the instance the caller holds, without charging
    ///     anything — so a request that could not send is refused before it pays for a password check.
    ///     Advisory only: the reservation is what decides.
    /// </summary>
    public static AccountEmailReservation Peek(Person person, AdminEmailOptions options)
    {
        var now = DateTime.UtcNow;

        if (person.AccountEmailWindowEndsAt > now && person.AccountEmailsSent >= MaxPerDay)
        {
            return AccountEmailReservation.DailyLimitReached;
        }

        return person.AccountEmailLastSentAt > now - options.Cooldown
            ? AccountEmailReservation.CoolingDown
            : AccountEmailReservation.Reserved;
    }

    /// <summary>
    ///     The refusal a reservation answers with, or <c>null</c> when it was reserved. A refusal also
    ///     advises how long to wait: until the cooldown ends for the cooldown, and for the daily limit
    ///     until the window ends or the cooldown does, whichever is later, since a send is accepted
    ///     only once both have — read from <paramref name="person" />, which a refused reservation has
    ///     brought in line with the stored row.
    /// </summary>
    public static string? RefusalOf(
        AccountEmailReservation reservation, Person person, AdminEmailOptions options, IRetryAfterAdvisor retryAfter)
    {
        var retryAt = reservation switch
        {
            AccountEmailReservation.DailyLimitReached => Later(
                person.AccountEmailWindowEndsAt, person.AccountEmailLastSentAt + options.Cooldown),
            AccountEmailReservation.CoolingDown => person.AccountEmailLastSentAt + options.Cooldown,
            _ => null
        };

        if (retryAt is { } at)
        {
            retryAfter.Advise(at - DateTime.UtcNow);
        }

        return reservation switch
        {
            AccountEmailReservation.CoolingDown => Shared.Messages.AuthMessages.EmailSentRecently,
            AccountEmailReservation.DailyLimitReached => Shared.Messages.AuthMessages.AccountEmailLimitReached,
            _ => null
        };
    }

    private static DateTime? Later(DateTime? first, DateTime? second) =>
        first is null ? second : second is null ? first : first > second ? first : second;
}
