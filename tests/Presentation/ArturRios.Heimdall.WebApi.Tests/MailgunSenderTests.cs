using ArturRios.Heimdall.WebApi.Email;
using ArturRios.Messaging.Email;
using ArturRios.Output;
using ArturRios.Util.Test.Attributes;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArturRios.Heimdall.WebApi.Tests;

// Unit tests for the two Mailgun-backed senders (UC-12 FR-PR-02, UC-06 FR-EV-01/02) and the link
// building they share. The property that carries the most weight is the last one: a delivery
// failure must not propagate, because the endpoints that trigger these sends answer identically
// whether or not the address belongs to anyone. A thrown exception would become a 500, and a 500
// that only happens for real addresses is exactly the enumeration oracle AF-12a exists to deny.
public class MailgunSenderTests
{
    private const string Recipient = "person@test.local";
    private const string Token = "TOKEN123";

    /// <summary>
    ///     Records what it was asked to send, and can be told to fail either way a real service can:
    ///     by reporting errors on the output, or by throwing.
    /// </summary>
    private sealed class StubEmailService : IEmailService
    {
        public string? To { get; private set; }
        public string? Subject { get; private set; }
        public string? Body { get; private set; }
        public bool Called { get; private set; }

        public string? ErrorToReport { get; init; }
        public Exception? ExceptionToThrow { get; init; }

        public Task<ProcessOutput> SendEmailAsync(string to, string subject, string body)
        {
            Called = true;
            To = to;
            Subject = subject;
            Body = body;

            if (ExceptionToThrow is not null)
            {
                throw ExceptionToThrow;
            }

            var output = new ProcessOutput();

            if (ErrorToReport is not null)
            {
                output.AddError(ErrorToReport);
            }

            return Task.FromResult(output);
        }
    }

    private static MailgunPasswordResetSender ResetSender(
        IEmailService emailService, string resetUrl = "https://app.test/reset") =>
        new(emailService,
            new EmailDeliveryOptions { PasswordResetUrl = resetUrl },
            NullLogger<MailgunPasswordResetSender>.Instance);

    private static MailgunEmailVerificationSender VerificationSender(
        IEmailService emailService, string verificationUrl = "https://app.test/verify") =>
        new(emailService,
            new EmailDeliveryOptions { VerificationUrl = verificationUrl },
            NullLogger<MailgunEmailVerificationSender>.Instance);

    private static MailgunRegistrationLinkSender RegistrationSender(
        IEmailService emailService, string verificationUrl = "https://app.test/verify") =>
        new(emailService,
            new EmailDeliveryOptions { VerificationUrl = verificationUrl },
            NullLogger<MailgunRegistrationLinkSender>.Instance);

    [UnitFact]
    public async Task GivenConfiguredResetUrl_WhenSendingPasswordReset_ThenEmailCarriesTheLink()
    {
        // Given
        var emailService = new StubEmailService();

        // When
        await ResetSender(emailService).SendAsync(Recipient, Token, linkBase: null);

        // Then
        Assert.Equal(Recipient, emailService.To);
        Assert.Equal("Reset your password", emailService.Subject);
        Assert.Contains($"https://app.test/reset?token={Token}", emailService.Body);
    }

    [UnitFact]
    public async Task GivenConfiguredVerificationUrl_WhenSendingVerification_ThenEmailCarriesTheLink()
    {
        // Given
        var emailService = new StubEmailService();

        // When
        await VerificationSender(emailService).SendAsync(Recipient, Token, linkBase: null);

        // Then
        Assert.Equal(Recipient, emailService.To);
        Assert.Equal("Verify your email address", emailService.Subject);
        Assert.Contains($"https://app.test/verify?token={Token}", emailService.Body);
    }

    [UnitFact]
    public async Task GivenNoConfiguredUrl_WhenSendingPasswordReset_ThenEmailCarriesTheBareToken()
    {
        // Given no front end to link to — the token still has to reach the person, since UC-13 takes
        // the token and the link is only a convenience wrapper around it
        var emailService = new StubEmailService();

        // When
        await ResetSender(emailService, resetUrl: "").SendAsync(Recipient, Token, linkBase: null);

        // Then
        Assert.Contains(Token, emailService.Body);
        Assert.DoesNotContain("http", emailService.Body);
    }

    [UnitFact]
    public async Task GivenBaseUrlWithQueryString_WhenBuildingLink_ThenTokenIsAppendedWithAmpersand()
    {
        // Given a front-end link that already carries a parameter
        var emailService = new StubEmailService();

        // When
        await ResetSender(emailService, "https://app.test/reset?lang=en").SendAsync(Recipient, Token, linkBase: null);

        // Then
        Assert.Contains($"https://app.test/reset?lang=en&token={Token}", emailService.Body);
    }

    [UnitFact]
    public void GivenTokenNeedingEscaping_WhenBuildingLink_ThenItIsEscaped()
    {
        // Given / When — issued tokens are alphanumeric, so this guards the helper rather than
        // today's caller: a token that ever gains a reserved character must not break the URL
        var link = EmailDeliveryOptions.BuildLink("https://app.test/reset", "a+b/c=d");

        // Then
        Assert.Equal("https://app.test/reset?token=a%2Bb%2Fc%3Dd", link);
    }

    [UnitFact]
    public void GivenNoBaseUrl_WhenBuildingLink_ThenResultIsEmpty()
    {
        // Given / When
        // Then — the empty string is the signal the senders read to write the bare token instead
        Assert.Equal(string.Empty, EmailDeliveryOptions.BuildLink("", Token));
        Assert.Equal(string.Empty, EmailDeliveryOptions.BuildLink("   ", Token));
    }

    [UnitFact]
    public async Task GivenMailgunReportsFailure_WhenSendingPasswordReset_ThenNothingIsThrown()
    {
        // Given Mailgun refusing the message — a bad key, a domain that is not verified
        var emailService = new StubEmailService { ErrorToReport = "401 Unauthorized" };

        // When / Then — the caller's response must not vary with this
        await ResetSender(emailService).SendAsync(Recipient, Token, linkBase: null);

        Assert.True(emailService.Called);
    }

    [UnitFact]
    public async Task GivenMailgunThrows_WhenSendingPasswordReset_ThenNothingIsThrown()
    {
        // Given the network failing outright — a timeout, a refused connection, DNS
        var emailService = new StubEmailService { ExceptionToThrow = new HttpRequestException("down") };

        // When / Then
        await ResetSender(emailService).SendAsync(Recipient, Token, linkBase: null);

        Assert.True(emailService.Called);
    }

    [UnitFact]
    public async Task GivenMailgunThrows_WhenSendingVerification_ThenNothingIsThrown()
    {
        // Given the same failure on the other sender: person creation (UC-06) must not fail because
        // the welcome email could not go out — the token is persisted and UC-15 can resend it
        var emailService = new StubEmailService { ExceptionToThrow = new HttpRequestException("down") };

        // When / Then
        await VerificationSender(emailService).SendAsync(Recipient, Token, linkBase: null);

        Assert.True(emailService.Called);
    }

    // A scope's link base replaces the global one for that scope's users, and only when it is given:
    // null (or empty) means the scope has none and the deployment-wide setting still applies.

    [UnitFact]
    public async Task GivenAScopeLinkBase_WhenSendingPasswordReset_ThenTheLinkUsesItInsteadOfTheGlobalOne()
    {
        var emailService = new StubEmailService();

        await ResetSender(emailService).SendAsync(Recipient, Token, "https://fortuna.test/password-reset");

        Assert.Contains($"https://fortuna.test/password-reset?token={Token}", emailService.Body);
        Assert.DoesNotContain("app.test", emailService.Body);
    }

    [UnitTheory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task GivenNoScopeLinkBase_WhenSendingPasswordReset_ThenTheGlobalLinkIsUsed(string? linkBase)
    {
        var emailService = new StubEmailService();

        await ResetSender(emailService).SendAsync(Recipient, Token, linkBase);

        Assert.Contains($"https://app.test/reset?token={Token}", emailService.Body);
    }

    [UnitFact]
    public async Task GivenAScopeLinkBaseAndNoGlobalUrl_WhenSendingPasswordReset_ThenTheLinkUsesTheScopeOne()
    {
        var emailService = new StubEmailService();

        await ResetSender(emailService, resetUrl: "")
            .SendAsync(Recipient, Token, "https://fortuna.test/password-reset");

        Assert.Contains($"https://fortuna.test/password-reset?token={Token}", emailService.Body);
    }

    [UnitFact]
    public async Task GivenNeitherScopeNorGlobalLink_WhenSendingPasswordReset_ThenEmailCarriesTheBareToken()
    {
        var emailService = new StubEmailService();

        await ResetSender(emailService, resetUrl: "").SendAsync(Recipient, Token, linkBase: null);

        Assert.Contains(Token, emailService.Body);
        Assert.DoesNotContain("http", emailService.Body);
    }

    [UnitFact]
    public async Task GivenAScopeLinkBaseWithAQuery_WhenSendingPasswordReset_ThenTheTokenIsAppendedWithAmpersand()
    {
        var emailService = new StubEmailService();

        await ResetSender(emailService).SendAsync(Recipient, Token, "https://fortuna.test/reset?lang=pt");

        Assert.Contains($"https://fortuna.test/reset?lang=pt&token={Token}", emailService.Body);
    }

    [UnitFact]
    public async Task GivenAScopeLinkBase_WhenSendingVerification_ThenTheLinkUsesItInsteadOfTheGlobalOne()
    {
        var emailService = new StubEmailService();

        await VerificationSender(emailService).SendAsync(Recipient, Token, "https://fortuna.test/verify-email");

        Assert.Contains($"https://fortuna.test/verify-email?token={Token}", emailService.Body);
        Assert.DoesNotContain("app.test", emailService.Body);
    }

    [UnitTheory]
    [InlineData(null)]
    [InlineData("")]
    public async Task GivenNoScopeLinkBase_WhenSendingVerification_ThenTheGlobalLinkIsUsed(string? linkBase)
    {
        var emailService = new StubEmailService();

        await VerificationSender(emailService).SendAsync(Recipient, Token, linkBase);

        Assert.Contains($"https://app.test/verify?token={Token}", emailService.Body);
    }

    [UnitFact]
    public async Task GivenNeitherScopeNorGlobalLink_WhenSendingVerification_ThenEmailCarriesTheBareToken()
    {
        var emailService = new StubEmailService();

        await VerificationSender(emailService, verificationUrl: "").SendAsync(Recipient, Token, linkBase: null);

        Assert.Contains(Token, emailService.Body);
        Assert.DoesNotContain("http", emailService.Body);
    }

    [UnitFact]
    public async Task GivenAScopeLinkBase_WhenSendingRegistrationLink_ThenTheLinkUsesItInsteadOfTheGlobalOne()
    {
        var emailService = new StubEmailService();

        await RegistrationSender(emailService).SendAsync(Recipient, Token, "https://fortuna.test/verify-email");

        Assert.Contains($"https://fortuna.test/verify-email?token={Token}", emailService.Body);
        Assert.DoesNotContain("app.test", emailService.Body);
    }

    [UnitTheory]
    [InlineData(null)]
    [InlineData("")]
    public async Task GivenNoScopeLinkBase_WhenSendingRegistrationLink_ThenTheGlobalVerificationLinkIsUsed(
        string? linkBase)
    {
        var emailService = new StubEmailService();

        await RegistrationSender(emailService).SendAsync(Recipient, Token, linkBase);

        Assert.Contains($"https://app.test/verify?token={Token}", emailService.Body);
    }

    [UnitFact]
    public async Task GivenNeitherScopeNorGlobalLink_WhenSendingRegistrationLink_ThenEmailCarriesTheBareToken()
    {
        var emailService = new StubEmailService();

        await RegistrationSender(emailService, verificationUrl: "").SendAsync(Recipient, Token, linkBase: null);

        Assert.Contains(Token, emailService.Body);
        Assert.DoesNotContain("http", emailService.Body);
    }
}
