using ArturRios.Heimdall.WebApi.Email;
using ArturRios.Messaging.Email;
using ArturRios.Output;
using ArturRios.Util.Test.Attributes;
using Microsoft.Extensions.Logging;

namespace ArturRios.Heimdall.WebApi.Tests;

// Unit tests for the three Mailgun senders MailgunSenderTests does not reach: the two-factor code
// (UC-36), the registration-attempt notice (UC-47 AF-47b) and the restriction-lift notification
// (GDPR Art. 18(3)). Each is pinned on what goes to Mailgun — recipient, fixed subject, body — and on
// what a failure does: the two that extend MailgunSender swallow it; the lift notifier reports it as
// false, because its caller must not mark the person notified when they were not. No log line may carry
// the address or the code (NFR-22).
public class MailgunNoticeSendersTests
{
    private const string Recipient = "person@test.local";

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

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Text)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception) + exception?.ToString()));
    }

    // MailgunTwoFactorEmailSender

    [UnitFact]
    public async Task GivenACode_WhenSendingTheTwoFactorEmail_ThenTheRecipientGetsTheFixedSubjectAndTheCode()
    {
        var emailService = new StubEmailService();
        var sender = new MailgunTwoFactorEmailSender(emailService, new RecordingLogger<MailgunTwoFactorEmailSender>());

        await sender.SendAsync(Recipient, "123456");

        Assert.Equal(Recipient, emailService.To);
        Assert.Equal("Your two-factor authentication code", emailService.Subject);
        Assert.Contains("123456", emailService.Body);
    }

    [UnitFact]
    public async Task GivenASuccessfulSend_WhenSendingTheTwoFactorEmail_ThenNeitherTheCodeNorTheAddressIsLogged()
    {
        var logger = new RecordingLogger<MailgunTwoFactorEmailSender>();

        await new MailgunTwoFactorEmailSender(new StubEmailService(), logger).SendAsync(Recipient, "123456");

        Assert.NotEmpty(logger.Entries);
        Assert.All(logger.Entries, entry =>
        {
            Assert.DoesNotContain("123456", entry.Text);
            Assert.DoesNotContain(Recipient, entry.Text);
        });
    }

    [UnitFact]
    public async Task GivenMailgunRefusesOrThrows_WhenSendingTheTwoFactorEmail_ThenItIsLoggedAndNothingEscapes()
    {
        var refused = new RecordingLogger<MailgunTwoFactorEmailSender>();
        var thrown = new RecordingLogger<MailgunTwoFactorEmailSender>();

        await new MailgunTwoFactorEmailSender(new StubEmailService { ErrorToReport = "401 Unauthorized" }, refused)
            .SendAsync(Recipient, "123456");
        await new MailgunTwoFactorEmailSender(
            new StubEmailService { ExceptionToThrow = new HttpRequestException("down") }, thrown)
            .SendAsync(Recipient, "123456");

        Assert.Contains(refused.Entries, entry => entry.Level == LogLevel.Error);
        Assert.Contains(thrown.Entries, entry => entry.Level == LogLevel.Error);
        Assert.All(refused.Entries.Concat(thrown.Entries), entry =>
        {
            Assert.DoesNotContain("123456", entry.Text);
            Assert.DoesNotContain(Recipient, entry.Text);
        });
    }

    // MailgunRegistrationAttemptSender

    [UnitTheory]
    [InlineData(false, "Forgotten your password?")]
    [InlineData(true, "Continue with Google")]
    public async Task GivenAnAccountKind_WhenSendingTheAttemptNotice_ThenTheBodyPointsTheWayBackIn(
        bool googleAccount, string expectedHint)
    {
        var emailService = new StubEmailService();
        var sender = new MailgunRegistrationAttemptSender(
            emailService, new RecordingLogger<MailgunRegistrationAttemptSender>());

        await sender.SendAsync(Recipient, googleAccount);

        Assert.Equal(Recipient, emailService.To);
        Assert.Equal("Someone tried to create an account with your address", emailService.Subject);
        Assert.Contains(expectedHint, emailService.Body);
    }

    [UnitFact]
    public async Task GivenMailgunRefusesOrThrows_WhenSendingTheAttemptNotice_ThenItIsLoggedWithoutTheAddress()
    {
        var refused = new RecordingLogger<MailgunRegistrationAttemptSender>();
        var thrown = new RecordingLogger<MailgunRegistrationAttemptSender>();

        await new MailgunRegistrationAttemptSender(new StubEmailService { ErrorToReport = "401" }, refused)
            .SendAsync(Recipient, false);
        await new MailgunRegistrationAttemptSender(
            new StubEmailService { ExceptionToThrow = new HttpRequestException("down") }, thrown)
            .SendAsync(Recipient, false);

        Assert.Contains(refused.Entries, entry => entry.Level == LogLevel.Error);
        Assert.Contains(thrown.Entries, entry => entry.Level == LogLevel.Error);
        Assert.All(refused.Entries.Concat(thrown.Entries),
            entry => Assert.DoesNotContain(Recipient, entry.Text));
    }

    // MailgunRestrictionLiftNotifier

    [UnitFact]
    public async Task GivenMailgunAccepts_WhenNotifyingTheLiftOfARestriction_ThenItReportsTrueAndSendsTheNotice()
    {
        var emailService = new StubEmailService();
        var notifier = new MailgunRestrictionLiftNotifier(
            emailService, new RecordingLogger<MailgunRestrictionLiftNotifier>());

        var accepted = await notifier.NotifyAsync(Recipient);

        Assert.True(accepted);
        Assert.Equal(Recipient, emailService.To);
        Assert.Equal("The restriction on your account is being lifted", emailService.Subject);
        Assert.Contains("restriction is", emailService.Body!.Replace("\r\n", " ").Replace("\n", " "));
    }

    [UnitFact]
    public async Task GivenMailgunRefuses_WhenNotifyingTheLiftOfARestriction_ThenItReportsFalseAndLogsWithoutTheAddress()
    {
        var logger = new RecordingLogger<MailgunRestrictionLiftNotifier>();
        var notifier = new MailgunRestrictionLiftNotifier(new StubEmailService { ErrorToReport = "401" }, logger);

        var accepted = await notifier.NotifyAsync(Recipient);

        Assert.False(accepted);
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Error);
        Assert.All(logger.Entries, entry => Assert.DoesNotContain(Recipient, entry.Text));
    }

    [UnitFact]
    public async Task GivenMailgunThrows_WhenNotifyingTheLiftOfARestriction_ThenItReportsFalseAndLogsWithoutTheAddress()
    {
        var logger = new RecordingLogger<MailgunRestrictionLiftNotifier>();
        var notifier = new MailgunRestrictionLiftNotifier(
            new StubEmailService { ExceptionToThrow = new HttpRequestException("down") }, logger);

        var accepted = await notifier.NotifyAsync(Recipient);

        Assert.False(accepted);
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Error);
        Assert.All(logger.Entries, entry => Assert.DoesNotContain(Recipient, entry.Text));
    }
}
