using ArturRios.Heimdall.Command.Services;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Persistence;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Util.Test.Attributes;

namespace ArturRios.Heimdall.Command.Tests;

// Unit tests for AccountEmailBudget.RefusalOf (FR-PE-16): the message a refused reservation answers
// with, and the wait it advises through Retry-After. A send is accepted only once the window has
// room and the cooldown has ended, so the daily-limit refusal advises the later of the two.
public class AccountEmailBudgetTests
{
    private sealed class RecordingAdvisor : IRetryAfterAdvisor
    {
        public TimeSpan? Advised { get; private set; }

        public void Advise(TimeSpan wait) => Advised = wait;
    }

    private static AdminEmailOptions Cooldown(TimeSpan cooldown) => new() { Cooldown = cooldown };

    [UnitFact]
    public void GivenTheDailyLimitWithTheCooldownEndingFirst_WhenRefusing_ThenTheWaitIsUntilTheWindowEnds()
    {
        // Given a window ending in two hours, and a cooldown that ended long ago
        var now = DateTime.UtcNow;
        var person = new Person
        {
            AccountEmailsSent = AccountEmailBudget.MaxPerDay,
            AccountEmailWindowEndsAt = now.AddHours(2),
            AccountEmailLastSentAt = now.AddHours(-3)
        };
        var advisor = new RecordingAdvisor();

        // When
        var message = AccountEmailBudget.RefusalOf(
            AccountEmailReservation.DailyLimitReached, person, Cooldown(TimeSpan.FromMinutes(5)), advisor);

        // Then
        Assert.Equal(AuthMessages.AccountEmailLimitReached, message);
        Assert.NotNull(advisor.Advised);
        Assert.InRange(advisor.Advised!.Value, TimeSpan.FromHours(2) - TimeSpan.FromMinutes(1), TimeSpan.FromHours(2));
    }

    [UnitFact]
    public void GivenTheDailyLimitWithTheCooldownEndingAfterTheWindow_WhenRefusing_ThenTheWaitIsUntilTheCooldownEnds()
    {
        // Given the fifth send two minutes before the window ends, and a five-minute cooldown
        var now = DateTime.UtcNow;
        var person = new Person
        {
            AccountEmailsSent = AccountEmailBudget.MaxPerDay,
            AccountEmailWindowEndsAt = now.AddMinutes(2),
            AccountEmailLastSentAt = now
        };
        var advisor = new RecordingAdvisor();

        // When
        var message = AccountEmailBudget.RefusalOf(
            AccountEmailReservation.DailyLimitReached, person, Cooldown(TimeSpan.FromMinutes(5)), advisor);

        // Then — five minutes, not two: a retry after two would still meet the cooldown
        Assert.Equal(AuthMessages.AccountEmailLimitReached, message);
        Assert.NotNull(advisor.Advised);
        Assert.InRange(advisor.Advised!.Value, TimeSpan.FromMinutes(4), TimeSpan.FromMinutes(5));
    }

    [UnitFact]
    public void GivenTheCooldown_WhenRefusing_ThenTheWaitIsUntilTheCooldownEnds()
    {
        // Given a send one minute ago and a five-minute cooldown
        var now = DateTime.UtcNow;
        var person = new Person
        {
            AccountEmailsSent = 1,
            AccountEmailWindowEndsAt = now.AddHours(20),
            AccountEmailLastSentAt = now.AddMinutes(-1)
        };
        var advisor = new RecordingAdvisor();

        // When
        var message = AccountEmailBudget.RefusalOf(
            AccountEmailReservation.CoolingDown, person, Cooldown(TimeSpan.FromMinutes(5)), advisor);

        // Then
        Assert.Equal(AuthMessages.EmailSentRecently, message);
        Assert.NotNull(advisor.Advised);
        Assert.InRange(advisor.Advised!.Value, TimeSpan.FromMinutes(3), TimeSpan.FromMinutes(4));
    }

    [UnitFact]
    public void GivenAReservation_WhenRefusing_ThenThereIsNoRefusalAndNoAdvice()
    {
        // Given
        var advisor = new RecordingAdvisor();

        // When
        var message = AccountEmailBudget.RefusalOf(
            AccountEmailReservation.Reserved, new Person(), Cooldown(TimeSpan.FromMinutes(5)), advisor);

        // Then
        Assert.Null(message);
        Assert.Null(advisor.Advised);
    }
}
