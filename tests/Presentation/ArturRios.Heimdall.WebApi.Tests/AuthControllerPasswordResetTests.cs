using System.Net;
using ArturRios.Configuration.Enums;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Output;
using ArturRios.Heimdall.Command.Services;
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

// Functional tests for POST /api/auth/password-reset (UC-13, FR-PR-03/04/08): the main flow for a
// User and for an admin, AF-13a…AF-13c (400, each named), AF-13d (400), AF-13e (400, as AF-13c), the
// conditional password write, and the anonymous access the endpoint depends on.
//
// The proof that the password really changed is a login: the response says only that it did, and the
// stored hash is meaningless on its own, so the tests reset a password and then authenticate with
// it — and confirm the old one is refused. Every token here is seeded, so the test holds the
// plaintext; since TH-14 one the API issued cannot be read back and spent.
[Collection(nameof(FunctionalCollection))]
public class AuthControllerPasswordResetTests(PostgresFixture db) : WebApiTest<Program>(EnvironmentType.Local)
{
    private const string OldPassword = Seeds.Password;
    private const string NewPassword = "Str0ng-Reset-Pass!";

    private static string UniqueEmail(string prefix) => $"{prefix}-{Guid.NewGuid():N}@test.local";

    private static string UniqueToken() => $"token-{Guid.NewGuid():N}";

    /// <summary>
    ///     Writes a reset token directly rather than going through UC-12's endpoint: these tests are
    ///     about consuming a token, and only a direct write can produce one that is already expired
    ///     or already used.
    /// </summary>
    private async Task<string> SeedTokenAsync(
        Person person, string? value = null, DateTime? expiresAt = null, bool used = false)
    {
        // Returns the token, stores its digest (TH-14). The caller gets what a person gets out of an
        // email, and the row holds what the row is allowed to hold.
        var plaintext = value ?? UniqueToken();

        await using var context = db.CreateContext();
        var token = new PasswordResetToken
        {
            PersonId = person.Id,
            TokenHash = SingleUseTokenHash.Of(plaintext),
            ExpiresAt = expiresAt ?? DateTime.UtcNow.AddHours(1),
            Used = used
        };
        context.PasswordResetTokens.Add(token);
        await context.SaveChangesAsync();
        return plaintext;
    }

    private Task<HttpOutput<DataOutput<ResetPasswordCommandOutput?>?>> ResetAsync(
        string token, string password = NewPassword) =>
        Gateway.PostAsync<DataOutput<ResetPasswordCommandOutput?>>(
            "/api/auth/password-reset",
            new ResetPasswordCommand { Token = token, NewPassword = password });

    private Task<HttpOutput<DataOutput<LoginCommandOutput?>?>> LoginAsync(
        string email, string password, Guid? scopeId = null) =>
        Gateway.PostAsync<DataOutput<LoginCommandOutput?>>(
            "/api/auth/login",
            new LoginCommand { Email = email, Password = password, ScopeId = scopeId });

    private async Task<List<PasswordResetToken>> TokensForAsync(Person person)
    {
        await using var context = db.CreateContext();

        return await context.PasswordResetTokens
            .Where(token => token.PersonId == person.Id)
            .ToListAsync();
    }

    [FunctionalFact]
    public async Task GivenLiveToken_WhenPostPasswordReset_ThenPasswordIsChangedAndTokenConsumed()
    {
        // Given an admin holding a reset token
        var email = UniqueEmail("admin");
        var person = await Seeds.PersonAsync(db, Roles.SystemAdmin, new PersonOptions { Email = email });
        var token = await SeedTokenAsync(person);

        // When
        var response = await ResetAsync(token);

        // Then — response
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(AuthMessages.PasswordResetSuccessful, response.Body!.Messages);
        Assert.Empty(response.Body.Errors);

        // Then — database state: the token is spent (UC-13 step 4)
        Assert.True(Assert.Single(await TokensForAsync(person)).Used);

        // Then — the password really changed: the new one authenticates, the old one does not
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(email, NewPassword)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(email, OldPassword)).StatusCode);
    }

    [FunctionalFact]
    public async Task GivenUserOfAScope_WhenPostPasswordReset_ThenPasswordIsChanged()
    {
        // Given a User, whose login needs a scope id but whose reset does not — the token identifies
        // them on its own, which is the whole reason UC-12 made it long and random
        var scope = await Seeds.ScopeAsync(db);
        var email = UniqueEmail("user");
        var person = await Seeds.PersonAsync(db, Roles.User, new PersonOptions { Email = email, MemberOf = scope });
        var token = await SeedTokenAsync(person);

        // When
        var response = await ResetAsync(token);

        // Then
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            HttpStatusCode.OK,
            (await LoginAsync(email, NewPassword, scope.PublicId)).StatusCode);
    }

    [FunctionalFact]
    public async Task GivenSaltIsRegenerated_WhenPostPasswordReset_ThenStoredSaltAndHashBothChange()
    {
        // Given — UC-13 step 3 asks for a new random salt, not a re-use of the stored one, so the new
        // hash shares nothing with the one it replaces (FR-RO-04, NFR-02)
        var person = await Seeds.PersonAsync(db, Roles.SystemAdmin, new PersonOptions { Email = UniqueEmail("admin") });
        var originalHash = person.PasswordHash;
        var originalSalt = person.Salt;
        var token = await SeedTokenAsync(person);

        // When
        await ResetAsync(token);

        // Then
        await using var context = db.CreateContext();
        var stored = await context.Persons.SingleAsync(x => x.Id == person.Id);
        Assert.NotEqual(originalSalt, stored.Salt);
        Assert.NotEqual(originalHash, stored.PasswordHash);
        Assert.True(stored.UpdatedAt >= stored.CreatedAt);
    }

    [FunctionalFact]
    public async Task GivenTwoLiveTokens_WhenPostPasswordReset_ThenBothAreConsumed()
    {
        // Given someone who clicked "forgot password" twice, so UC-12 issued two working links.
        // UC-12's own tests leave both live and say UC-13 decides; this is that decision.
        var email = UniqueEmail("admin");
        var person = await Seeds.PersonAsync(db, Roles.SystemAdmin, new PersonOptions { Email = email });
        var first = await SeedTokenAsync(person);
        var second = await SeedTokenAsync(person);

        // When the second link is the one clicked
        var response = await ResetAsync(second);

        // Then — both rows are spent
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.All(await TokensForAsync(person), token => Assert.True(token.Used));

        // Then — and the first link cannot change the password a second time (AF-13b)
        var replay = await ResetAsync(first, "An0ther-Pass!");
        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
        Assert.Contains(AuthMessages.TokenAlreadyUsed, replay.Body!.Errors);
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(email, NewPassword)).StatusCode);
    }

    [FunctionalFact]
    public async Task GivenAnotherPersonHoldsALiveToken_WhenPostPasswordReset_ThenTheirTokenSurvives()
    {
        // Given the boundary of the rule above: invalidation reaches the person's own tokens and stops
        var person = await Seeds.PersonAsync(db, Roles.SystemAdmin, new PersonOptions { Email = UniqueEmail("admin") });
        var other = await Seeds.PersonAsync(db, Roles.SystemAdmin, new PersonOptions { Email = UniqueEmail("other") });
        var theirToken = await SeedTokenAsync(other);

        // When
        await ResetAsync(await SeedTokenAsync(person));

        // Then
        Assert.False(Assert.Single(await TokensForAsync(other)).Used);
        Assert.Equal(
            SingleUseTokenHash.Of(theirToken), (await TokensForAsync(other)).Single().TokenHash);
    }

    [FunctionalFact]
    public async Task GivenExpiredToken_WhenPostPasswordReset_ThenBadRequestAndPasswordIsUnchanged()
    {
        // Given — AF-13a (FR-PR-04)
        var email = UniqueEmail("admin");
        var person = await Seeds.PersonAsync(db, Roles.SystemAdmin, new PersonOptions { Email = email });
        var token = await SeedTokenAsync(person, expiresAt: DateTime.UtcNow.AddHours(-1));

        // When
        var response = await ResetAsync(token);

        // Then — response
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(AuthMessages.TokenExpired, response.Body!.Errors);

        // Then — nothing changed: the old password still works and the token is still unused
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(email, OldPassword)).StatusCode);
        Assert.False(Assert.Single(await TokensForAsync(person)).Used);
    }

    [FunctionalFact]
    public async Task GivenUsedToken_WhenPostPasswordReset_ThenBadRequestAndPasswordIsUnchanged()
    {
        // Given — AF-13b
        var email = UniqueEmail("admin");
        var person = await Seeds.PersonAsync(db, Roles.SystemAdmin, new PersonOptions { Email = email });
        var token = await SeedTokenAsync(person, used: true);

        // When
        var response = await ResetAsync(token);

        // Then
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(AuthMessages.TokenAlreadyUsed, response.Body!.Errors);
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(email, OldPassword)).StatusCode);
    }

    [FunctionalFact]
    public async Task GivenUnknownToken_WhenPostPasswordReset_ThenBadRequest()
    {
        // Given — AF-13c: nothing matches what the caller presented
        // When
        var response = await ResetAsync(UniqueToken());

        // Then
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(AuthMessages.TokenInvalid, response.Body!.Errors);
    }

    [FunctionalFact]
    public async Task GivenTokenDifferingOnlyInCase_WhenPostPasswordReset_ThenBadRequest()
    {
        // Given — AF-13c. The lookup is case-sensitive, unlike every email comparison in this system:
        // the token is a random secret, and folding its case would throw away part of its alphabet.
        var email = UniqueEmail("admin");
        var person = await Seeds.PersonAsync(db, Roles.SystemAdmin, new PersonOptions { Email = email });
        var token = await SeedTokenAsync(person, $"MiXeD-{Guid.NewGuid():N}".ToUpper());

        // When
        var response = await ResetAsync(token.ToLower());

        // Then
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(AuthMessages.TokenInvalid, response.Body!.Errors);
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(email, OldPassword)).StatusCode);
    }

    [FunctionalTheory]
    [InlineData("", NewPassword)]
    [InlineData("some-token", "")]
    [InlineData("some-token", "short")]
    public async Task GivenMalformedRequest_WhenPostPasswordReset_ThenBadRequest(string token, string password)
    {
        // Given — AF-13d: a missing token, a missing password, and one below UC-06's eight-character
        // floor, which a reset must not be a way around
        // When
        var response = await ResetAsync(token, password);

        // Then
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.NotEmpty(response.Body!.Errors);
    }

    [FunctionalFact]
    public async Task GivenNoBearerToken_WhenPostPasswordReset_ThenEndpointAnswersAnonymously()
    {
        // Given no bearer token on the gateway: someone resetting a password they have lost cannot
        // hold one, so the authentication middleware must let the request through ([AllowAnonymous]).
        // An unknown reset token reaches the handler and is refused there, on its merits — a 401 here
        // would mean the endpoint never ran at all.
        var response = await ResetAsync(UniqueToken());

        // Then
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(AuthMessages.TokenInvalid, response.Body!.Errors);
    }

    [FunctionalFact]
    public async Task GivenRecoveryRequested_WhenInspectingWhatWasStored_ThenOnlyADigestIs()
    {
        // This test used to join UC-12 to UC-13 — request a recovery, read the issued token back out
        // of the row, spend it — and it cannot any more, which is the point of TH-14: the token is
        // mailed and nowhere else, and this suite has no Mailgun credentials and no seam to
        // substitute a sender through, so there is no inbox for it to read.
        //
        // What it asserts instead is the property that replaced the one it lost: UC-12 wrote a live,
        // unused token, and what it wrote is a 32-byte SHA-256 rather than anything a caller could
        // present back. The spending half is covered where the plaintext is still reachable —
        // PasswordResetServiceTests captures it from the sender and pins the stored digest to it,
        // and ResetPasswordCommandHandlerTests seeds through the same helper and spends it — so both
        // halves go through SingleUseTokenHash and would fail together if it changed.
        var email = UniqueEmail("admin");
        await Seeds.PersonAsync(db, Roles.SystemAdmin, new PersonOptions { Email = email });

        // When
        var recovery = await Gateway.PostAsync<DataOutput<PasswordRecoveryCommandOutput?>>(
            "/api/auth/password-recovery", new PasswordRecoveryCommand { Email = email });

        // Then
        Assert.Equal(HttpStatusCode.OK, recovery.StatusCode);

        // The token is issued after the answer, by the recovery dispatcher (AF-12a), so wait for it
        PasswordResetToken? issued = null;

        await Poll.UntilAsync(
            async () =>
            {
                await using var context = db.CreateContext();
                issued = await context.PasswordResetTokens
                    .Include(token => token.Person)
                    .SingleOrDefaultAsync(token => token.Person.Email == email);

                return issued is not null;
            },
            "No reset token was issued in time.");

        Assert.False(issued!.Used);
        Assert.True(issued.ExpiresAt > DateTime.UtcNow);
        Assert.Equal(SingleUseTokenHash.Length, issued.TokenHash.Length);
    }

    // AF-13e (FR-PR-08): the account is checked again when the link is followed.

    private async Task AssertPasswordUnchangedAndTokenUnspentAsync(Person person)
    {
        await using var context = db.CreateContext();
        var stored = await context.Persons.SingleAsync(x => x.Id == person.Id);

        Assert.True(Hash.TextMatches(OldPassword, stored.PasswordHash, stored.Salt));
        Assert.Equal(person.SecurityStamp, stored.SecurityStamp);
        Assert.All(await context.PasswordResetTokens.Where(x => x.PersonId == person.Id).ToListAsync(),
            token => Assert.False(token.Used));
    }

    [FunctionalFact]
    public async Task GivenAPersonRestrictedSinceTheLinkWasMailed_WhenPostPasswordReset_ThenInvalidTokenAndTheRecordIsKept()
    {
        // Given
        var person = await Seeds.PersonAsync(db, Roles.SystemAdmin, new PersonOptions { Email = UniqueEmail("restricted") });
        var token = await SeedTokenAsync(person);

        await using (var context = db.CreateContext())
        {
            await context.Persons.Where(x => x.Id == person.Id)
                .ExecuteUpdateAsync(set => set.SetProperty(x => x.ProcessingRestrictedAt, DateTime.UtcNow));
        }

        // When
        var response = await ResetAsync(token);

        // Then — exactly what an unknown token gets (AF-13c), and nothing written (NFR-24)
        var unknown = await ResetAsync(UniqueToken());
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(unknown.StatusCode, response.StatusCode);
        Assert.Equal(["Invalid token."], response.Body!.Errors);
        Assert.Equal(unknown.Body!.Errors, response.Body.Errors);
        await AssertPasswordUnchangedAndTokenUnspentAsync(person);
    }

    [FunctionalFact]
    public async Task GivenAPersonDeletedSinceTheLinkWasMailed_WhenPostPasswordReset_ThenInvalidTokenAndTheRecordIsKept()
    {
        // Given
        var person = await Seeds.PersonAsync(db, Roles.SystemAdmin, new PersonOptions { Email = UniqueEmail("deleted"), IsDeleted = true });
        var token = await SeedTokenAsync(person);

        // When
        var response = await ResetAsync(token);

        // Then
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal([AuthMessages.TokenInvalid], response.Body!.Errors);
        await AssertPasswordUnchangedAndTokenUnspentAsync(person);
    }

    [FunctionalFact]
    public async Task GivenAUserWhoseScopeWasDeletedSinceTheLinkWasMailed_WhenPostPasswordReset_ThenInvalidToken()
    {
        // Given
        var scope = await Seeds.ScopeAsync(db);
        var person = await Seeds.PersonAsync(db, Roles.User, new PersonOptions { Email = UniqueEmail("scope-gone"), MemberOf = scope });
        var token = await SeedTokenAsync(person);

        await using (var context = db.CreateContext())
        {
            await context.Scopes.Where(x => x.Id == scope.Id)
                .ExecuteUpdateAsync(set => set.SetProperty(x => x.IsDeleted, true));
        }

        // When
        var response = await ResetAsync(token);

        // Then
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal([AuthMessages.TokenInvalid], response.Body!.Errors);
        await AssertPasswordUnchangedAndTokenUnspentAsync(person);
    }

    [FunctionalFact]
    public async Task GivenTheAddressChangedAfterThePersonWasRead_WhenThePasswordIsWritten_ThenNothingIsWritten()
    {
        // Given a person read, as UC-13 reads them with the token, and an address change saved after
        var person = await Seeds.PersonAsync(db, Roles.SystemAdmin, new PersonOptions { Email = UniqueEmail("raced") });

        await using var reading = db.CreateContext();
        var read = await reading.Persons.SingleAsync(x => x.Id == person.Id);

        await using (var changing = db.CreateContext())
        {
            await changing.Persons.Where(x => x.Id == person.Id)
                .ExecuteUpdateAsync(set => set.SetProperty(x => x.Email, UniqueEmail("moved")));
        }

        // When
        var hash = Hash.EncodeWithRandomSalt(NewPassword, out var salt);
        var written = await new ArturRios.Heimdall.Data.Persistence.AtomicWrites(reading)
            .TrySetPasswordAsync(read, hash, salt, DateTime.UtcNow);

        // Then — the change wins (FR-PE-17), and the old password stands
        Assert.False(written);
        await using var checking = db.CreateContext();
        var stored = await checking.Persons.SingleAsync(x => x.Id == person.Id);
        Assert.True(Hash.TextMatches(OldPassword, stored.PasswordHash, stored.Salt));
    }

    [FunctionalFact]
    public async Task GivenThePersonUnchangedSinceTheyWereRead_WhenThePasswordIsWritten_ThenThePasswordColumnsAndTheStampChange()
    {
        // Given the control for the test above
        var person = await Seeds.PersonAsync(db, Roles.SystemAdmin, new PersonOptions { Email = UniqueEmail("unraced") });

        await using var reading = db.CreateContext();
        var read = await reading.Persons.SingleAsync(x => x.Id == person.Id);

        // When
        var hash = Hash.EncodeWithRandomSalt(NewPassword, out var salt);
        var written = await new ArturRios.Heimdall.Data.Persistence.AtomicWrites(reading)
            .TrySetPasswordAsync(read, hash, salt, DateTime.UtcNow);

        // Then
        Assert.True(written);
        await using var checking = db.CreateContext();
        var stored = await checking.Persons.SingleAsync(x => x.Id == person.Id);
        Assert.True(Hash.TextMatches(NewPassword, stored.PasswordHash, stored.Salt));
        Assert.Equal(person.Email, stored.Email);
        Assert.NotEqual(person.SecurityStamp, stored.SecurityStamp);
        Assert.Equal(read.SecurityStamp, stored.SecurityStamp);
    }

    // FR-PR-08: once the row exists, only targeted writes reach the password columns.

    [FunctionalFact]
    public async Task GivenACopyReadBeforeAReset_WhenSavedAsAWholeRow_ThenTheResetPasswordStands()
    {
        // Given one request holding the person as it was — an administrator's edit, say — while the
        // person completes a reset
        var email = UniqueEmail("whole-row");
        var person = await Seeds.PersonAsync(db, Roles.SystemAdmin, new PersonOptions { Email = email });

        await using var stale = db.CreateContext();
        var staleCopy = await stale.Persons.SingleAsync(x => x.Id == person.Id);

        var reset = await ResetAsync(await SeedTokenAsync(person));
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);

        // When the first request saves its copy the way the repositories do — every column
        staleCopy.Name = "Renamed meanwhile";
        stale.Persons.Update(staleCopy);
        await stale.SaveChangesAsync();

        // Then the reset password still verifies, the old one does not, and the rename landed
        await using var checking = db.CreateContext();
        var stored = await checking.Persons.SingleAsync(x => x.Id == person.Id);
        Assert.True(Hash.TextMatches(NewPassword, stored.PasswordHash, stored.Salt));
        Assert.False(Hash.TextMatches(OldPassword, stored.PasswordHash, stored.Salt));
        Assert.Equal("Renamed meanwhile", stored.Name);
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(email, NewPassword)).StatusCode);
    }

    [FunctionalTheory]
    [InlineData("restricted")]
    [InlineData("deleted")]
    public async Task GivenAnAccountChangeAfterThePersonWasRead_WhenThePasswordIsWritten_ThenNothingIsWrittenTheStampIncluded(
        string state)
    {
        // Given a person read, as UC-13 reads them with the token, and then restricted or deleted
        var person = await Seeds.PersonAsync(db, Roles.SystemAdmin, new PersonOptions { Email = UniqueEmail($"raced-{state}") });

        await using var reading = db.CreateContext();
        var read = await reading.Persons.SingleAsync(x => x.Id == person.Id);

        await using (var changing = db.CreateContext())
        {
            var rows = changing.Persons.Where(x => x.Id == person.Id);

            if (state == "restricted")
            {
                await rows.ExecuteUpdateAsync(set => set.SetProperty(x => x.ProcessingRestrictedAt, DateTime.UtcNow));
            }
            else
            {
                await rows.ExecuteUpdateAsync(set => set.SetProperty(x => x.IsDeleted, true));
            }
        }

        // When
        var hash = Hash.EncodeWithRandomSalt(NewPassword, out var salt);
        var written = await new ArturRios.Heimdall.Data.Persistence.AtomicWrites(reading)
            .TrySetPasswordAsync(read, hash, salt, DateTime.UtcNow);

        // Then — refused, and the record keeps its password and its stamp (NFR-24)
        Assert.False(written);
        await using var checking = db.CreateContext();
        var stored = await checking.Persons.SingleAsync(x => x.Id == person.Id);
        Assert.True(Hash.TextMatches(OldPassword, stored.PasswordHash, stored.Salt));
        Assert.Equal(person.SecurityStamp, stored.SecurityStamp);
    }
}
