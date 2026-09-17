using ArturRios.Heimdall.Command.Services;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Shared.Security;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Mock;
using Moq;

namespace ArturRios.Heimdall.Command.Tests;

// Unit tests for TwoFactorEmailCodeIssuer (FR-2F-03, FR-2F-08), the retire-then-issue step UC-11's
// AF-11g, UC-36's Email method and UC-46's reissue all now share.
//
// The retirement is the half worth testing hardest. It is what makes an email code single-use in
// practice — only the newest code for a configuration can ever be redeemed — and it is the half
// that would be easy to lose in a fourth caller written later, which is why the three copies became
// one.
public class TwoFactorEmailCodeIssuerTests
{
    private const string Email = "person@test.local";

    private sealed record Fixture(
        AsyncFakeRepository<TwoFactorEmailCode> EmailCodes,
        Mock<ITwoFactorEmailSender> Sender,
        TwoFactorAuth TwoFactorAuth)
    {
        public TwoFactorEmailCodeIssuer Issuer() => new(EmailCodes, EmailCodes, Sender.Object);

        public List<TwoFactorEmailCode> Codes() => EmailCodes.Query().ToList();

        /// <summary>The codes sent, as the sender saw them — the plaintext never leaves this call.</summary>
        public List<string> Sent { get; } = [];
    }

    private static Fixture NewFixture()
    {
        var sender = new Mock<ITwoFactorEmailSender>();
        var fixture = new Fixture(
            new AsyncFakeRepository<TwoFactorEmailCode>(),
            sender,
            new TwoFactorAuth { Id = 7, PersonId = 10, IsActive = true, EmailEnabled = true });

        sender
            .Setup(s => s.SendAsync(It.IsAny<string>(), It.IsAny<string>()))
            .Callback<string, string>((_, code) => fixture.Sent.Add(code))
            .Returns(Task.CompletedTask);

        return fixture;
    }

    private static Task<long> SeedCodeAsync(
        Fixture fixture, bool used = false, TimeSpan? remainingLife = null) =>
        SeedAsync(fixture, new TwoFactorEmailCode
        {
            TwoFactorAuthId = fixture.TwoFactorAuth.Id,
            CodeHash = [9, 9, 9],
            Salt = [1, 1, 1],
            ExpiresAt = DateTime.UtcNow.Add(remainingLife ?? TimeSpan.FromMinutes(10)),
            Used = used
        });

    private static async Task<long> SeedAsync(Fixture fixture, TwoFactorEmailCode code)
    {
        await fixture.EmailCodes.CreateAsync(code);

        return code.Id;
    }

    [UnitFact]
    public async Task GivenNoOutstandingCode_WhenReissuing_ThenOneSixDigitCodeIsStoredHashedAndSent()
    {
        // Given
        var fixture = NewFixture();

        // When
        var errors = await fixture.Issuer().ReissueAsync(fixture.TwoFactorAuth, Email);

        // Then
        Assert.Null(errors);

        var code = Assert.Single(fixture.Codes());

        Assert.Equal(fixture.TwoFactorAuth.Id, code.TwoFactorAuthId);
        Assert.False(code.Used);
        Assert.NotEmpty(code.CodeHash);
        Assert.NotEmpty(code.Salt);

        // Then — six digits, and nothing else: a letter would not survive a caller typing it back
        var sent = Assert.Single(fixture.Sent);

        Assert.Equal(6, sent.Length);
        Assert.All(sent, character => Assert.True(char.IsAsciiDigit(character)));

        // Then — the plaintext is never stored, only its hash (NFR-16's reasoning, applied here)
        Assert.DoesNotContain(fixture.Codes(), stored => stored.CodeHash.Length == sent.Length &&
                                                         System.Text.Encoding.UTF8.GetString(stored.CodeHash) == sent);
    }

    [UnitFact]
    public async Task GivenTheIssuedCode_WhenReissuing_ThenItExpiresOnFR2F03Schedule()
    {
        // Given — the ten minutes FR-2F-03 promises, read from the one place that states it
        var fixture = NewFixture();
        var before = DateTime.UtcNow;

        // When
        await fixture.Issuer().ReissueAsync(fixture.TwoFactorAuth, Email);

        // Then — bracketed by the call rather than compared to a single instant, since the clock
        // moves between the two reads
        var code = Assert.Single(fixture.Codes());

        Assert.InRange(
            code.ExpiresAt,
            before.Add(TwoFactorLifetimes.EmailCode),
            DateTime.UtcNow.Add(TwoFactorLifetimes.EmailCode));
    }

    [UnitFact]
    public async Task GivenOutstandingCodes_WhenReissuing_ThenEveryOneIsRetiredFirst()
    {
        // Given — two live codes, which should not happen but must not survive if it does: the
        // retirement is what leaves exactly one redeemable code per configuration
        var fixture = NewFixture();
        await SeedCodeAsync(fixture);
        await SeedCodeAsync(fixture);

        // When
        var errors = await fixture.Issuer().ReissueAsync(fixture.TwoFactorAuth, Email);

        // Then
        Assert.Null(errors);
        Assert.Equal(3, fixture.Codes().Count);
        Assert.Single(fixture.Codes(), code => !code.Used);
    }

    [UnitFact]
    public async Task GivenAnExpiredOrUsedCode_WhenReissuing_ThenNeitherIsDisturbed()
    {
        // Given — a code already spent and one already lapsed. Neither is outstanding, so neither is
        // the retirement's business; rewriting them would be a write for nothing.
        var fixture = NewFixture();
        var usedId = await SeedCodeAsync(fixture, used: true);
        var expiredId = await SeedCodeAsync(fixture, remainingLife: TimeSpan.FromMinutes(-1));

        // When
        await fixture.Issuer().ReissueAsync(fixture.TwoFactorAuth, Email);

        // Then — the used one is still used, the expired one still unused, and a third was added
        var codes = fixture.Codes();

        Assert.Equal(3, codes.Count);
        Assert.True(codes.Single(code => code.Id == usedId).Used);
        Assert.False(codes.Single(code => code.Id == expiredId).Used);
    }

    [UnitFact]
    public async Task GivenOutstandingCodes_WhenRetiringWithoutReissuing_ThenNoNewCodeIsIssuedOrSent()
    {
        // Given — UC-36 deselecting the Email method: the outstanding code should stop working, and
        // nothing should replace it
        var fixture = NewFixture();
        await SeedCodeAsync(fixture);

        // When
        var errors = await fixture.Issuer().RetireOutstandingAsync(fixture.TwoFactorAuth.Id);

        // Then
        Assert.Null(errors);
        Assert.All(fixture.Codes(), code => Assert.True(code.Used));
        Assert.Empty(fixture.Sent);
    }

    [UnitFact]
    public async Task GivenAnotherConfigurationsCode_WhenReissuing_ThenItIsLeftAlone()
    {
        // Given — retirement is scoped to the configuration, not the table. A shared mailbox or a
        // reused address must not cost somebody else their live code.
        var fixture = NewFixture();
        var otherId = await SeedAsync(fixture, new TwoFactorEmailCode
        {
            TwoFactorAuthId = fixture.TwoFactorAuth.Id + 1,
            CodeHash = [8, 8, 8],
            Salt = [2, 2, 2],
            ExpiresAt = DateTime.UtcNow.AddMinutes(10),
            Used = false
        });

        // When
        await fixture.Issuer().ReissueAsync(fixture.TwoFactorAuth, Email);

        // Then
        Assert.False(fixture.Codes().Single(code => code.Id == otherId).Used);
    }

    [UnitFact]
    public async Task GivenTwoReissues_WhenTheCodesAreCompared_ThenTheSecondIsNotTheFirst()
    {
        // Given — a reissue that returned the same digits would make "ask for another" useless to
        // the person it exists for, and would not retire anything meaningful
        var fixture = NewFixture();

        // When
        await fixture.Issuer().ReissueAsync(fixture.TwoFactorAuth, Email);
        await fixture.Issuer().ReissueAsync(fixture.TwoFactorAuth, Email);

        // Then
        Assert.Equal(2, fixture.Sent.Count);
        Assert.NotEqual(fixture.Sent[0], fixture.Sent[1]);

        // Then — and only the newer one is still redeemable
        Assert.Single(fixture.Codes(), code => !code.Used);
    }

    [UnitFact]
    public async Task GivenTheAddressPassedIn_WhenReissuing_ThenThatIsWhereTheCodeGoes()
    {
        // The address is the caller of this service's to supply, and every caller supplies the
        // person's stored one. Pinned here so the parameter cannot quietly become something else.
        var fixture = NewFixture();

        // When
        await fixture.Issuer().ReissueAsync(fixture.TwoFactorAuth, Email);

        // Then
        fixture.Sender.Verify(
            sender => sender.SendAsync(Email, It.IsAny<string>()), Times.Once);
    }
}
