using System.Net;
using ArturRios.Configuration.Enums;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Output;
using ArturRios.Heimdall.Command.Services;
using ArturRios.Heimdall.Data.Persistence;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Domain.Persistence;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Heimdall.WebApi.Tests.Support;
using ArturRios.Output;
using ArturRios.Util.Http;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Functional;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Heimdall.WebApi.Tests;

// Functional tests for the per-person account-email budget (FR-PE-16) against PostgreSQL: the account
// emails a person's own requests can have sent — a resent verification link, a two-factor setup code,
// the notice an own email change sends to the address it leaves — at most one per cooldown and five a
// day. The reservation is one conditional UPDATE on the person row, so what matters is what a burst
// gets, what the daily cap answers, and that switching an address back and forth is bounded.
[Collection(nameof(FunctionalCollection))]
public class AccountEmailBudgetTests(PostgresFixture db) : WebApiTest<Program>(EnvironmentType.Local)
{
    private const string Password = Seeds.Password;

    private static string UniqueEmail(string prefix) => $"{prefix}-{Guid.NewGuid():N}@test.local";

    private async Task<Person> ReloadAsync(Person person)
    {
        await using var context = db.CreateContext();
        return await context.Persons.AsNoTracking().SingleAsync(x => x.Id == person.Id);
    }

    /// <summary>Writes the budget columns directly — the mapping ignores them on a whole-row save.</summary>
    private async Task SetBudgetAsync(Person person, int sent, DateTime? windowEndsAt, DateTime? lastSentAt)
    {
        await using var context = db.CreateContext();
        await context.Persons
            .Where(x => x.Id == person.Id)
            .ExecuteUpdateAsync(set => set
                .SetProperty(x => x.AccountEmailsSent, sent)
                .SetProperty(x => x.AccountEmailWindowEndsAt, windowEndsAt)
                .SetProperty(x => x.AccountEmailLastSentAt, lastSentAt));
    }

    private void ActAs(Person person)
    {
        Gateway.Client.DefaultRequestHeaders.Remove("Authorization");
        Authorize(TestTokens.For(person.PublicId, (int)person.RoleId));
    }

    private Task<HttpOutput<DataOutput<ResendVerificationEmailCommandOutput?>?>> ResendAsync() =>
        Gateway.PostAsync<DataOutput<ResendVerificationEmailCommandOutput?>>("/api/auth/resend-verification", new { });

    private Task<HttpOutput<DataOutput<UpdatePersonCommandOutput?>?>> ChangeEmailAsync(Person person, string email) =>
        Gateway.PutAsync<DataOutput<UpdatePersonCommandOutput?>>(
            $"/api/persons/{person.PublicId}",
            new UpdatePersonCommand { Name = person.Name, Email = email, CurrentPassword = Password });

    [FunctionalFact]
    public async Task GivenTenSimultaneousReservationsAndNoCooldown_WhenReserved_ThenExactlyTheDailyCapIsAdmitted()
    {
        // Given ten reservations released together, each on its own connection and its own copy of the
        // row, with the cooldown off so only the count can refuse them
        var person = await Seeds.PersonAsync(db, Roles.ScopeAdmin, new PersonOptions { EmailPrefix = "burst-cap", EmailVerified = false });
        var now = DateTime.UtcNow;
        using var start = new ManualResetEventSlim(false);

        var reservations = Enumerable.Range(0, 10)
            .Select(_ => Task.Run(async () =>
            {
                await using var context = db.CreateContext();
                var copy = await context.Persons.SingleAsync(x => x.Id == person.Id);
                start.Wait();

                return await new AtomicWrites(context).TryReserveAccountEmailAsync(
                    copy, AccountEmailBudget.MaxPerDay, now, now, now + AccountEmailBudget.Window);
            }))
            .ToList();

        // When
        start.Set();
        var outcomes = await Task.WhenAll(reservations);

        // Then — five, and the rest told why
        Assert.Equal(AccountEmailBudget.MaxPerDay, outcomes.Count(x => x == AccountEmailReservation.Reserved));
        Assert.All(outcomes.Where(x => x != AccountEmailReservation.Reserved),
            outcome => Assert.Equal(AccountEmailReservation.DailyLimitReached, outcome));
        Assert.Equal(AccountEmailBudget.MaxPerDay, (await ReloadAsync(person)).AccountEmailsSent);
    }

    [FunctionalFact]
    public async Task GivenTenSimultaneousResends_WhenPosted_ThenExactlyOneLinkIsSent()
    {
        // Given — a burst that every request of used to pass a read-first cooldown together
        var person = await Seeds.PersonAsync(db, Roles.ScopeAdmin, new PersonOptions { EmailPrefix = "burst-resend", EmailVerified = false });
        ActAs(person);

        // When
        var responses = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => ResendAsync()));

        // Then — one sent, nine refused by the cooldown, and one link issued
        Assert.Equal(1, responses.Count(x => x.StatusCode == HttpStatusCode.OK));
        Assert.All(responses.Where(x => x.StatusCode != HttpStatusCode.OK), response =>
        {
            Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
            Assert.Contains(AuthMessages.EmailSentRecently, response.Body!.Errors);
        });

        await using var context = db.CreateContext();
        Assert.Equal(1, await context.EmailVerificationTokens.CountAsync(x => x.PersonId == person.Id));
        Assert.Equal(1, (await ReloadAsync(person)).AccountEmailsSent);
    }

    [FunctionalFact]
    public async Task GivenTheDaysAccountEmailsAreSpent_WhenPostResendVerification_ThenTooManyRequestsWithTheDailyLimit()
    {
        // Given five sends in an open window, the last two hours ago — well out of the cooldown
        var person = await Seeds.PersonAsync(db, Roles.ScopeAdmin, new PersonOptions { EmailPrefix = "daily-cap", EmailVerified = false });
        await SetBudgetAsync(person, AccountEmailBudget.MaxPerDay, DateTime.UtcNow.AddHours(10), DateTime.UtcNow.AddHours(-2));
        ActAs(person);

        // When
        var response = await ResendAsync();

        // Then
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Contains(AuthMessages.AccountEmailLimitReached, response.Body!.Errors);
    }

    [FunctionalFact]
    public async Task GivenAnAddressSwitchedAway_WhenSwitchedBackAtOnce_ThenTooManyRequestsAndTheAddressStays()
    {
        // Given an own change away from the victim's address, which mails the victim a notice
        var person = await Seeds.PersonAsync(db, Roles.ScopeAdmin, new PersonOptions { EmailPrefix = "switch" });
        var victim = person.Email;
        var elsewhere = UniqueEmail("elsewhere");
        ActAs(person);

        var away = await ChangeEmailAsync(person, elsewhere);
        Assert.Equal(HttpStatusCode.OK, away.StatusCode);

        // When switching straight back — the change rotated the stamp, so with a fresh token
        ActAs(person);
        var back = await ChangeEmailAsync(person, victim);

        // Then — within the cooldown: refused, nothing changed, nobody mailed
        Assert.Equal(HttpStatusCode.TooManyRequests, back.StatusCode);
        Assert.Contains(AuthMessages.EmailSentRecently, back.Body!.Errors);
        Assert.Equal(elsewhere, (await ReloadAsync(person)).Email);
    }

    [FunctionalFact]
    public async Task GivenTheDaysAccountEmailsAreSpent_WhenChangingOwnEmail_ThenTooManyRequestsWithTheDailyLimit()
    {
        // Given — a day of switching already spent
        var person = await Seeds.PersonAsync(db, Roles.ScopeAdmin, new PersonOptions { EmailPrefix = "switch-cap" });
        await SetBudgetAsync(person, AccountEmailBudget.MaxPerDay, DateTime.UtcNow.AddHours(10), DateTime.UtcNow.AddHours(-2));
        ActAs(person);

        // When
        var response = await ChangeEmailAsync(person, UniqueEmail("sixth"));

        // Then
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Contains(AuthMessages.AccountEmailLimitReached, response.Body!.Errors);
        Assert.Equal(person.Email, (await ReloadAsync(person)).Email);
    }

    [FunctionalFact]
    public async Task GivenTheDaysAccountEmailsAreSpent_WhenPostResendVerification_ThenRetryAfterNamesTheWindowsEnd()
    {
        // Given a window ending in ten hours
        var person = await Seeds.PersonAsync(db, Roles.ScopeAdmin, new PersonOptions { EmailPrefix = "daily-cap-retry", EmailVerified = false });
        await SetBudgetAsync(person, AccountEmailBudget.MaxPerDay, DateTime.UtcNow.AddHours(10), DateTime.UtcNow.AddHours(-2));
        ActAs(person);

        // When
        using var response = await Gateway.Client.PostAsync(
            "/api/auth/resend-verification", System.Net.Http.Json.JsonContent.Create(new { }));

        // Then — the seconds until the window ends, so a client need not wait until "tomorrow"
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.NotNull(response.Headers.RetryAfter?.Delta);
        Assert.InRange(response.Headers.RetryAfter!.Delta!.Value.TotalSeconds, 10 * 3600 - 120, 10 * 3600);
    }

    [FunctionalFact]
    public async Task GivenThePersonVanishedBeforeTheReservation_WhenReserved_ThenItIsRefusedWithoutAnError()
    {
        // Given a copy of a person whose row is then hard deleted (UC-10) before the write
        var person = await Seeds.PersonAsync(db, Roles.ScopeAdmin, new PersonOptions { EmailPrefix = "vanished", EmailVerified = false });
        await using var context = db.CreateContext();
        var copy = await context.Persons.SingleAsync(x => x.Id == person.Id);

        await using (var other = db.CreateContext())
        {
            await other.Persons.Where(x => x.Id == person.Id).ExecuteDeleteAsync();
        }

        // When
        var now = DateTime.UtcNow;
        var outcome = await new AtomicWrites(context).TryReserveAccountEmailAsync(
            copy, AccountEmailBudget.MaxPerDay, now, now, now + AccountEmailBudget.Window);

        // Then — a refusal, never a send, and no exception for the caller to turn into a 500
        Assert.NotEqual(AccountEmailReservation.Reserved, outcome);
    }
}
