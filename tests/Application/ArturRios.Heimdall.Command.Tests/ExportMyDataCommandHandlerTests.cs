using ArturRios.Heimdall.Command.Handlers;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Services;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Mock;
using Application = ArturRios.Heimdall.Domain.Entities.Application;

namespace ArturRios.Heimdall.Command.Tests;

// Unit tests for ExportMyDataCommandHandler (UC-41). The properties that matter: the subject is
// always the caller, the copy is complete, and the things it deliberately does not reproduce are
// named rather than silently missing.
public class ExportMyDataCommandHandlerTests
{
    private sealed record Fakes(
        AsyncFakeRepository<Person> Persons,
        AsyncFakeRepository<GoogleUser> GoogleUsers,
        AsyncFakeRepository<Scope> Scopes,
        AsyncFakeRepository<Application> Applications,
        AsyncFakeRepository<TwoFactorAuth> TwoFactors,
        AsyncFakeRepository<TwoFactorRecoveryCode> RecoveryCodes,
        AsyncFakeRepository<AuditLog> AuditEntries)
    {
        public static Fakes New() => new(new(), new(), new(), new(), new(), new(), new());

        public ExportMyDataCommandHandler Handler() =>
            new(Persons, GoogleUsers, Scopes, Applications, TwoFactors, RecoveryCodes, AuditEntries);
    }

    private static async Task<Person> SeedPersonAsync(Fakes fakes, bool isDeleted = false)
    {
        var person = new Person
        {
            PublicId = Guid.NewGuid(),
            Name = "Ada Lovelace",
            Email = "ada@analytical.engine",
            PasswordHash = [1, 2, 3],
            Salt = [4, 5, 6],
            RoleId = (long)Roles.User,
            EmailVerified = true,
            FailedLoginAttempts = 2,
            IsDeleted = isDeleted,
            LegalBasis = (int)LegalBases.ContractPerformance,
            PrivacyNoticeVersion = "1.0",
            BasisRecordedAt = DateTime.UtcNow.AddDays(-5)
        };

        await fakes.Persons.CreateAsync(person);

        return person;
    }

    private static ExportMyDataCommand Command(Guid actingPersonId) =>
        new() { ActingPersonId = actingPersonId, ActingRole = (int)Roles.User };

    [UnitFact]
    public async Task GivenAPerson_WhenExporting_ThenTheirIdentifyingValuesAreReturned()
    {
        var fakes = Fakes.New();
        var person = await SeedPersonAsync(fakes);

        var output = await fakes.Handler().HandleAsync(Command(person.PublicId));

        Assert.True(output.Success);
        Assert.Equal(ErasureMessages.DataExported, output.Messages.First());

        var subject = output.Data!.Subject;

        Assert.Equal(person.PublicId, subject.Id);
        Assert.Equal("Person", subject.Kind);
        Assert.Equal("Ada Lovelace", subject.Name);
        Assert.Equal("ada@analytical.engine", subject.Email);
        Assert.True(subject.EmailVerified);
    }

    [UnitFact]
    public async Task GivenAPerson_WhenExporting_ThenSecretsAreNamedButNotReproduced()
    {
        // Art. 15(4): a copy must not adversely affect others' rights, and reproducing the hash
        // would hand whoever holds this document the material to attack the password offline.
        var fakes = Fakes.New();
        var person = await SeedPersonAsync(fakes);

        var output = await fakes.Handler().HandleAsync(Command(person.PublicId));
        var document = output.Data!;

        // The state is disclosed
        Assert.True(document.Security.PasswordSet);

        // The material is not, and the subject is told it exists rather than left to guess
        Assert.NotEmpty(document.Withheld);
        Assert.Contains(document.Withheld, w => w.Value.Contains("Password hash"));
        Assert.All(document.Withheld, w => Assert.False(string.IsNullOrWhiteSpace(w.Reason)));
    }

    [UnitFact]
    public async Task GivenAPerson_WhenExporting_ThenTheContextArticle15RequiresIsIncluded()
    {
        // A dump of table rows satisfies the copy and none of the context — the purposes, the
        // recipients, the retention periods — that Art. 15 asks for in the same breath.
        var fakes = Fakes.New();
        var person = await SeedPersonAsync(fakes);

        var document = (await fakes.Handler().HandleAsync(Command(person.PublicId))).Data!;

        Assert.NotEmpty(document.Recipients);
        Assert.NotEmpty(document.Retention);
        Assert.Equal((int)LegalBases.ContractPerformance, document.Processing.LegalBasis);
        Assert.Equal(nameof(LegalBases.ContractPerformance), document.Processing.LegalBasisName);
        Assert.Equal("1.0", document.Processing.PrivacyNoticeVersion);
    }

    [UnitFact]
    public async Task GivenAPersonWithTwoFactorAndApplications_WhenExporting_ThenBothAppear()
    {
        var fakes = Fakes.New();
        var person = await SeedPersonAsync(fakes);
        var scope = new Scope { PublicId = Guid.NewGuid(), Name = "Acme" };
        await fakes.Scopes.CreateAsync(scope);

        var twoFactor = new TwoFactorAuth
        {
            PersonId = person.Id, AppEnabled = true, IsActive = true, TotpSecretEncrypted = [9]
        };
        await fakes.TwoFactors.CreateAsync(twoFactor);
        await fakes.RecoveryCodes.CreateAsync(new TwoFactorRecoveryCode
        {
            TwoFactorAuthId = twoFactor.Id, CodeHash = [1], Used = false
        });
        await fakes.RecoveryCodes.CreateAsync(new TwoFactorRecoveryCode
        {
            TwoFactorAuthId = twoFactor.Id, CodeHash = [2], Used = true
        });

        await fakes.Applications.CreateAsync(new Application
        {
            PublicId = Guid.NewGuid(), Name = "Billing", OwnerId = person.Id, ScopeId = scope.Id, Scope = scope
        });

        var document = (await fakes.Handler().HandleAsync(Command(person.PublicId))).Data!;

        Assert.True(document.Security.TwoFactorActive);
        Assert.True(document.Security.TwoFactorAppEnabled);
        Assert.Equal(1, document.Security.UnusedRecoveryCodes);
        Assert.Equal("Billing", Assert.Single(document.Applications).Name);
    }

    [UnitFact]
    public async Task GivenAPersonsAuditEntries_WhenExporting_ThenOnlyTheirsAppear()
    {
        var fakes = Fakes.New();
        var person = await SeedPersonAsync(fakes);
        var somebodyElse = Guid.NewGuid();

        await fakes.AuditEntries.CreateAsync(new AuditLog
        {
            PublicId = Guid.NewGuid(), ActorPersonId = person.PublicId, Action = "Mine",
            Succeeded = true, CreatedAt = DateTime.UtcNow
        });
        await fakes.AuditEntries.CreateAsync(new AuditLog
        {
            PublicId = Guid.NewGuid(), ActorPersonId = somebodyElse, Action = "Theirs",
            Succeeded = true, CreatedAt = DateTime.UtcNow
        });
        // Attribution cleared under NFR-21: nothing connects it to anyone any more
        await fakes.AuditEntries.CreateAsync(new AuditLog
        {
            PublicId = Guid.NewGuid(), ActorPersonId = null, Action = "Cleared",
            Succeeded = true, CreatedAt = DateTime.UtcNow
        });

        var document = (await fakes.Handler().HandleAsync(Command(person.PublicId))).Data!;

        Assert.Equal("Mine", Assert.Single(document.AuditEntries).Action);
        Assert.False(document.AuditEntriesTruncated);
    }

    [UnitFact]
    public async Task GivenMoreAuditEntriesThanTheCap_WhenExporting_ThenTruncationIsDisclosed()
    {
        // A silently truncated copy is not the copy Art. 15 asks for
        var fakes = Fakes.New();
        var person = await SeedPersonAsync(fakes);

        for (var i = 0; i <= DataExportDisclosure.MaximumAuditEntries; i++)
        {
            await fakes.AuditEntries.CreateAsync(new AuditLog
            {
                PublicId = Guid.NewGuid(), ActorPersonId = person.PublicId, Action = "A",
                Succeeded = true, CreatedAt = DateTime.UtcNow.AddSeconds(-i)
            });
        }

        var document = (await fakes.Handler().HandleAsync(Command(person.PublicId))).Data!;

        Assert.True(document.AuditEntriesTruncated);
        Assert.Equal(DataExportDisclosure.MaximumAuditEntries, document.AuditEntries.Count());
    }

    [UnitFact]
    public async Task GivenASuspendedPerson_WhenExporting_ThenTheyStillGetTheirCopy()
    {
        // Somebody suspended pending erasure has more reason to want a copy than anyone, and Art. 15
        // is not conditional on the account being in good standing.
        var fakes = Fakes.New();
        var person = await SeedPersonAsync(fakes, isDeleted: true);

        var output = await fakes.Handler().HandleAsync(Command(person.PublicId));

        Assert.True(output.Success);
        Assert.True(output.Data!.Subject.IsDeleted);
    }

    [UnitFact]
    public async Task GivenAGoogleUser_WhenExporting_ThenGoogleHeldValuesAppear()
    {
        var fakes = Fakes.New();
        var scope = new Scope { PublicId = Guid.NewGuid(), Name = "Acme" };
        await fakes.Scopes.CreateAsync(scope);

        var googleUser = new GoogleUser
        {
            PublicId = Guid.NewGuid(),
            GoogleId = "108124442542040415000",
            Name = "Ada",
            Email = "ada@test.local",
            ProfilePictureUrl = "https://example.invalid/photo.jpg",
            ScopeId = scope.Id,
            LegalBasis = (int)LegalBases.ContractPerformance
        };
        await fakes.GoogleUsers.CreateAsync(googleUser);

        var document = (await fakes.Handler().HandleAsync(Command(googleUser.PublicId))).Data!;

        Assert.Equal("GoogleUser", document.Subject.Kind);
        Assert.Equal("108124442542040415000", document.Subject.GoogleId);
        Assert.Equal("https://example.invalid/photo.jpg", document.Subject.ProfilePictureUrl);

        // No password and no second factor, by construction rather than by happening to be unset
        Assert.False(document.Security.PasswordSet);
        Assert.False(document.Security.TwoFactorActive);
        Assert.Null(document.Subject.Role);
    }

    [UnitFact]
    public async Task GivenATokenNamingNobody_WhenExporting_ThenItIsRefused()
    {
        var fakes = Fakes.New();

        var output = await fakes.Handler().HandleAsync(Command(Guid.NewGuid()));

        Assert.False(output.Success);
        Assert.Contains(ErasureMessages.NotEligible, output.Errors);
    }

    [UnitFact]
    public async Task GivenTwoPeople_WhenOneExports_ThenTheOthersDataIsAbsent()
    {
        var fakes = Fakes.New();
        var person = await SeedPersonAsync(fakes);
        var other = new Person
        {
            PublicId = Guid.NewGuid(), Name = "Grace Hopper", Email = "grace@navy.mil",
            PasswordHash = [1], Salt = [2], RoleId = (long)Roles.User
        };
        await fakes.Persons.CreateAsync(other);

        var document = (await fakes.Handler().HandleAsync(Command(person.PublicId))).Data!;

        Assert.Equal(person.PublicId, document.Subject.Id);
        Assert.NotEqual("Grace Hopper", document.Subject.Name);
    }
}
