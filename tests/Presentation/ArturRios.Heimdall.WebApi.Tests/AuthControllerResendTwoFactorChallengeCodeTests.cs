using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using ArturRios.Configuration.Enums;
using ArturRios.Heimdall.Command.Handlers;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Output;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Heimdall.WebApi.Tests.Support;
using ArturRios.Output;
using ArturRios.Util.Hashing;
using ArturRios.Util.Http;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Functional;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Heimdall.WebApi.Tests;

// Functional tests for POST /api/auth/2fa/challenge/resend (UC-46, FR-2F-16): the main flow end to
// end from a real login, every alternative flow AF-46a…AF-46e, the reissue cap FR-2F-13 states, and
// the property the whole use case rests on — that the response does not vary.
[Collection(nameof(FunctionalCollection))]
public class AuthControllerResendTwoFactorChallengeCodeTests(PostgresFixture db)
    : WebApiTest<Program>(EnvironmentType.Local)
{
    private const string Password = "Str0ng-Resend-Pass!";

    private static string UniqueEmail(string prefix) => $"{prefix}-{Guid.NewGuid():N}@test.local";

    private async Task<Person> SeedPersonAsync(string email, bool restricted = false, bool deleted = false)
    {
        await using var context = db.CreateContext();

        var person = new Person
        {
            PublicId = Guid.NewGuid(),
            Name = "Resend",
            Email = email,
            PasswordHash = Hash.EncodeWithRandomSalt(Password, out var salt),
            Salt = salt,
            RoleId = (long)Roles.SystemAdmin,
            EmailVerified = true,
            IsDeleted = deleted,
            ProcessingRestrictedAt = restricted ? DateTime.UtcNow : null
        };

        context.Persons.Add(person);
        await context.SaveChangesAsync();

        return person;
    }

    private async Task<TwoFactorAuth> SeedActiveAsync(Person person, bool appEnabled, bool emailEnabled)
    {
        await using var context = db.CreateContext();

        var twoFactorAuth = new TwoFactorAuth
        {
            PersonId = person.Id, IsActive = true, AppEnabled = appEnabled, EmailEnabled = emailEnabled
        };

        context.TwoFactorAuths.Add(twoFactorAuth);
        await context.SaveChangesAsync();

        return twoFactorAuth;
    }

    private Task<HttpOutput<DataOutput<LoginCommandOutput?>?>> LoginAsync(string email) =>
        Gateway.PostAsync<DataOutput<LoginCommandOutput?>>(
            "/api/auth/login", new LoginCommand { Email = email, Password = Password });

    private Task<HttpOutput<DataOutput<ResendTwoFactorChallengeCodeCommandOutput?>?>> ResendAsync(
        string? challengeToken) =>
        Gateway.PostAsync<DataOutput<ResendTwoFactorChallengeCodeCommandOutput?>>(
            "/api/auth/2fa/challenge/resend",
            new ResendTwoFactorChallengeCodeCommand { ChallengeToken = challengeToken ?? string.Empty });

    private Task<HttpOutput<DataOutput<VerifyTwoFactorAuthCommandOutput?>?>> VerifyAsync(
        string challengeToken, string code) =>
        Gateway.PostAsync<DataOutput<VerifyTwoFactorAuthCommandOutput?>>(
            "/api/auth/2fa/verify",
            new VerifyTwoFactorAuthCommand { ChallengeToken = challengeToken, Code = code });

    /// <summary>
    ///     The whole of what a caller can observe of one response: its status and its body, rendered
    ///     back to JSON so a comparison is over every field rather than the handful a test thought to
    ///     name. The gateway's pinned version does not expose the bytes it read, so the body is
    ///     re-serialized from what it bound — which still catches a field appearing, disappearing or
    ///     changing value, and that is the whole of what the non-disclosure rule is about.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <c>Timestamp</c> is blanked. Every <c>DataOutput</c> this API returns carries one, and
    ///         it says when the response was built rather than anything about what was asked — two
    ///         identical requests a millisecond apart differ in it too. Leaving it in would compare
    ///         the clock and nothing else.
    ///     </para>
    ///     <para>
    ///         It is the <em>only</em> normalisation, deliberately: anything else that differed
    ///         between two of these responses would be a difference in what the endpoint said, which
    ///         is exactly what must not happen.
    ///     </para>
    /// </remarks>
    private static string Answer<TBody>(HttpOutput<TBody> response) =>
        Regex.Replace(
            $"{(int)response.StatusCode} {JsonSerializer.Serialize(response.Body)}",
            "\"Timestamp\":\"[^\"]*\"",
            "\"Timestamp\":\"<when>\"");

    private async Task<List<TwoFactorEmailCode>> CodesForAsync(long personId)
    {
        await using var context = db.CreateContext();

        return await context.TwoFactorEmailCodes
            .Where(code => code.TwoFactorAuth.PersonId == personId)
            .OrderBy(code => code.Id)
            .ToListAsync();
    }

    /// <summary>
    ///     Logs in for real so the challenge token, the first email code and the reissue budget are
    ///     all produced by the production path rather than seeded.
    /// </summary>
    private async Task<(Person Person, string ChallengeToken)> LoggedInChallengeAsync(string prefix)
    {
        var person = await SeedPersonAsync(UniqueEmail(prefix));
        await SeedActiveAsync(person, appEnabled: false, emailEnabled: true);

        var login = await LoginAsync(person.Email);

        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.True(login.Body!.Data!.RequiresTwoFactor);

        return (person, login.Body.Data.ChallengeToken!);
    }

    [FunctionalFact]
    public async Task GivenAnOutstandingChallenge_WhenPostResend_ThenTheOldCodeIsRetiredAndANewOneWorks()
    {
        // Given — UC-46 main flow, from a real login
        var (person, challengeToken) = await LoggedInChallengeAsync("resend-main");

        var beforeCodes = await CodesForAsync(person.Id);
        var original = Assert.Single(beforeCodes);

        // When
        var response = await ResendAsync(challengeToken);

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(TwoFactorMessages.ChallengeCodeResent, response.Body!.Messages);

        // Then — the login's code was retired and exactly one fresh code is live
        var afterCodes = await CodesForAsync(person.Id);

        Assert.Equal(2, afterCodes.Count);
        Assert.True(afterCodes.Single(code => code.Id == original.Id).Used);
        Assert.Single(afterCodes, code => !code.Used);
    }

    [FunctionalFact]
    public async Task GivenAResend_WhenTheResponseIsRead_ThenItCarriesNoTokenOfAnyKind()
    {
        // A new challenge token could only be returned for a challenge that was genuine, so
        // returning one would be exactly the oracle this endpoint must not be. The reissue therefore
        // lands inside the existing window and never lengthens it — asserted over the raw body, since
        // a token appearing under any field name would break the property.
        var (_, challengeToken) = await LoggedInChallengeAsync("resend-no-token");

        // When
        var response = await ResendAsync(challengeToken);

        // Then
        var answer = Answer(response);

        Assert.DoesNotContain("token", answer, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("expiresAt", answer, StringComparison.OrdinalIgnoreCase);
    }

    [FunctionalFact]
    public async Task GivenAResend_WhenThePreviousCodeIsPresented_ThenItNoLongerCompletesTheLogin()
    {
        // The retirement, end to end. A resend that left the old code working would mean two live
        // codes for one configuration, which is twice the guessing surface FR-2F-13 budgets for.
        //
        // The first code is seeded rather than taken from a login, because a mailed code's plaintext
        // is never stored — seeding is the only way this test can hold one to present afterwards.
        // The challenge token is minted the same way UC-11 would.
        const string FirstCode = "135790";

        var person = await SeedPersonAsync(UniqueEmail("resend-retires"));
        var twoFactorAuth = await SeedActiveAsync(person, appEnabled: false, emailEnabled: true);

        await using (var context = db.CreateContext())
        {
            context.TwoFactorEmailCodes.Add(new TwoFactorEmailCode
            {
                TwoFactorAuthId = twoFactorAuth.Id,
                CodeHash = Hash.EncodeWithRandomSalt(FirstCode, out var salt),
                Salt = salt,
                ExpiresAt = DateTime.UtcNow.AddMinutes(10),
                Used = false
            });
            await context.SaveChangesAsync();
        }

        var challengeToken = TestTokens.ForMfaPending(person.PublicId, (int)Roles.SystemAdmin);

        // Given the first code does complete the login before the resend, so what follows is the
        // resend's doing and not a code that never worked
        var beforeResend = await VerifyAsync(challengeToken, FirstCode);

        Assert.Equal(HttpStatusCode.OK, beforeResend.StatusCode);

        // Reinstate it, since redeeming it marked it used — the subject of this test is retirement
        // by reissue, not retirement by redemption
        await using (var context = db.CreateContext())
        {
            var redeemed = await context.TwoFactorEmailCodes
                .Where(code => code.TwoFactorAuth.PersonId == person.Id)
                .OrderBy(code => code.Id)
                .FirstAsync();

            redeemed.Used = false;
            await context.SaveChangesAsync();
        }

        // When
        var resend = await ResendAsync(challengeToken);

        Assert.Equal(HttpStatusCode.OK, resend.StatusCode);

        // Then — the code that worked a moment ago is refused, with UC-38's usual answer
        var afterResend = await VerifyAsync(challengeToken, FirstCode);

        Assert.Equal(HttpStatusCode.Unauthorized, afterResend.StatusCode);
        Assert.Contains(TwoFactorMessages.FactorInvalid, afterResend.Body!.Errors);

        // Then — and something did replace it, so the person is not left with nothing
        Assert.Single(await CodesForAsync(person.Id), code => !code.Used);
    }

    [FunctionalFact]
    public async Task GivenEveryAlternativeFlow_WhenPostResend_ThenTheResponseIsIndistinguishableFromTheMainFlow()
    {
        // The property most easily lost in a later refactor, and therefore the one asserted over the
        // whole response rather than over the fields a test remembered to name: a valid challenge, an
        // unknown person's, a forged token, an expired one, a full bearer token, and an App-only
        // person must all be answered the same.
        //
        // If any of these ever diverge, this endpoint becomes a way for an anonymous caller to ask
        // "is this address registered, and does it have email two-factor enabled?" — which is the
        // question UC-11 and UC-38 both refuse.
        //
        // What this does not cover is how long each answer took. The valid path does real work — a
        // hash and three round trips — and the others return immediately, so the two are separable
        // by a clock. That is deliberate rather than overlooked: reaching the valid path at all
        // requires a challenge token, which requires the password, and anyone holding one was
        // already told by UC-11's AF-11g response which methods the account has. The timing
        // therefore discloses nothing the caller was not already given, which is why this endpoint
        // needs no equivalent of UC-11's decoy hash.
        var (_, valid) = await LoggedInChallengeAsync("resend-same-valid");

        var appOnlyPerson = await SeedPersonAsync(UniqueEmail("resend-same-app"));
        await SeedActiveAsync(appOnlyPerson, appEnabled: true, emailEnabled: false);
        var appOnly = TestTokens.ForMfaPending(appOnlyPerson.PublicId, (int)Roles.SystemAdmin);

        var unknown = TestTokens.ForMfaPending(Guid.NewGuid(), (int)Roles.SystemAdmin);
        var fullBearer = TestTokens.For(Guid.NewGuid(), (int)Roles.SystemAdmin);

        var expired = TestTokens.ForMfaPending(
            appOnlyPerson.PublicId, (int)Roles.SystemAdmin, TimeSpan.FromSeconds(1));
        await Task.Delay(TimeSpan.FromSeconds(2));

        var cases = new (string Name, string Token)[]
        {
            ("a valid challenge", valid),
            ("a challenge naming an App-only person", appOnly),
            ("a challenge naming nobody", unknown),
            ("a forged token", "not-a-real-token"),
            ("an expired challenge", expired),
            ("a full bearer token", fullBearer)
        };

        var answers = new List<(string Name, string Answer)>();

        foreach (var (name, token) in cases)
        {
            var response = await ResendAsync(token);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            answers.Add((name, Answer(response)));
        }

        // Then — one answer between them, and the failure names which case diverged rather than
        // reporting only that something did
        var distinct = answers.Select(answer => answer.Answer).Distinct().ToList();

        Assert.True(
            distinct.Count == 1,
            "these cases must be indistinguishable but were not:\n" +
            string.Join("\n", answers.Select(answer => $"  {answer.Name}: {answer.Answer}")));
    }

    [FunctionalFact]
    public async Task GivenOneChallenge_WhenResendingPastTheCap_ThenOnlyTheAllowedCodesAreIssued()
    {
        // FR-2F-13's bound, exhausted through the endpoint. The login issues one code; the challenge
        // authorizes three reissues; a fourth request is answered identically and sends nothing. So
        // one authentication attempt is worth four codes, which at five guesses each is the twenty
        // the requirement states.
        var (person, challengeToken) = await LoggedInChallengeAsync("resend-cap");
        var cap = ResendTwoFactorChallengeCodeCommandHandler.MaxReissuesPerChallenge;

        var answers = new List<string>();

        // When — one more than the challenge may authorize
        for (var attempt = 0; attempt <= cap; attempt++)
        {
            var response = await ResendAsync(challengeToken);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            answers.Add(Answer(response));
        }

        // Then — the login's own code plus exactly `cap` reissues, and no more
        var codes = await CodesForAsync(person.Id);

        Assert.Equal(1 + cap, codes.Count);
        Assert.Single(codes, code => !code.Used);

        // Then — the refusal is invisible: the request that sent nothing looks like the ones that did
        Assert.Single(answers.Distinct());
    }

    [FunctionalFact]
    public async Task GivenAnExhaustedChallenge_WhenLoggingInAgain_ThenTheBudgetIsRestored()
    {
        // The cap is per authentication attempt, not per account (FR-2F-13): a fresh budget costs a
        // fresh password check, which is exactly what the requirement charged before a resend
        // existed. Without this, a person who legitimately needed three resends would be unable to
        // sign in at all until somebody cleared a counter.
        var (person, challengeToken) = await LoggedInChallengeAsync("resend-budget");
        var cap = ResendTwoFactorChallengeCodeCommandHandler.MaxReissuesPerChallenge;

        for (var attempt = 0; attempt < cap; attempt++)
        {
            await ResendAsync(challengeToken);
        }

        var spentCount = (await CodesForAsync(person.Id)).Count;

        // When — a second authentication
        var login = await LoginAsync(person.Email);

        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        var response = await ResendAsync(login.Body!.Data!.ChallengeToken!);

        // Then — the new challenge could reissue: one code from the login itself, one from the resend
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(spentCount + 2, (await CodesForAsync(person.Id)).Count);
    }

    [FunctionalFact]
    public async Task GivenAChallengeNamingARestrictedPerson_WhenPostResend_ThenNothingIsSent()
    {
        // AF-46b (NFR-24): mailing a restricted identity would be processing it. Reachable because
        // the restriction can be applied after UC-11 issued the challenge.
        var person = await SeedPersonAsync(UniqueEmail("resend-restricted"), restricted: true);
        await SeedActiveAsync(person, appEnabled: false, emailEnabled: true);

        var challengeToken = TestTokens.ForMfaPending(person.PublicId, (int)Roles.SystemAdmin);

        // When
        var response = await ResendAsync(challengeToken);

        // Then — answered like every other path, and nothing issued
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(await CodesForAsync(person.Id));
    }

    [FunctionalFact]
    public async Task GivenAChallengeTokenUsedAsABearerHeader_WhenCallingAnotherEndpoint_ThenStillRefused()
    {
        // FR-2F-10 is undisturbed by this endpoint existing: a challenge token is still refused
        // everywhere it is presented as a credential, and this route reads it from the body instead.
        var (person, challengeToken) = await LoggedInChallengeAsync("resend-guard");

        Authorize(challengeToken);

        var read = await Gateway.GetAsync<DataOutput<object?>>($"/api/persons/{person.PublicId}");

        Assert.Equal(HttpStatusCode.Unauthorized, read.StatusCode);
    }

    [FunctionalFact]
    public async Task GivenABearerTokenInsteadOfAChallengeToken_WhenPostResend_ThenNothingIsSent()
    {
        // AF-46e: this endpoint refuses a full authentication token in the challenge's place, the
        // mirror of the guard above — and refuses it the same silent way as everything else.
        var person = await SeedPersonAsync(UniqueEmail("resend-bearer"));
        await SeedActiveAsync(person, appEnabled: false, emailEnabled: true);

        var fullToken = TestTokens.For(person.PublicId, (int)Roles.SystemAdmin);

        // When
        var response = await ResendAsync(fullToken);

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(await CodesForAsync(person.Id));
    }
}
