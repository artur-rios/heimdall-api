using ArturRios.Heimdall.Command.Services;
using ArturRios.Heimdall.WebApi.Email;
using ArturRios.Messaging.Email;
using ArturRios.Output;
using ArturRios.Util.Test.Attributes;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArturRios.Heimdall.WebApi.Tests;

// Unit tests for MailgunScopeInvitationNoticeSender (UC-21, UC-60, UC-61): each notice goes to the right
// person with a fixed subject and ScopeInvitationNoticeText's body, and a delivery failure never reaches the
// caller — the invitation or the answer has already been stored.
public class MailgunScopeInvitationNoticeSenderTests
{
    private sealed class StubEmailService : IEmailService
    {
        public string? To { get; private set; }
        public string? Subject { get; private set; }
        public string? Body { get; private set; }
        public Exception? ExceptionToThrow { get; init; }

        public Task<ProcessOutput> SendEmailAsync(string to, string subject, string body)
        {
            To = to;
            Subject = subject;
            Body = body;

            if (ExceptionToThrow is not null)
            {
                throw ExceptionToThrow;
            }

            return Task.FromResult(new ProcessOutput());
        }
    }

    private static readonly DateTime ExpiresAt = new(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc);

    private static MailgunScopeInvitationNoticeSender Sender(IEmailService emailService) =>
        new(emailService, NullLogger<MailgunScopeInvitationNoticeSender>.Instance);

    [UnitFact]
    public async Task GivenAnInvitation_WhenSent_ThenTheInviteeGetsTheFixedSubjectAndTheSentence()
    {
        // Given
        var emailService = new StubEmailService();

        // When
        await Sender(emailService).SendInvitationAsync("grace@test.local", "Ada", "Acme", ExpiresAt);

        // Then
        Assert.Equal("grace@test.local", emailService.To);
        Assert.Equal(ScopeInvitationNoticeText.InvitationSubject, emailService.Subject);
        Assert.Equal(ScopeInvitationNoticeText.Invitation("Ada", "Acme", ExpiresAt), emailService.Body);
    }

    [UnitFact]
    public async Task GivenANameWithALineBreak_WhenSent_ThenTheSubjectIsUntouched()
    {
        // Given — Review Focus 5
        var emailService = new StubEmailService();

        // When
        await Sender(emailService).SendInvitationAsync("grace@test.local", "Ada\r\nBcc: x@evil.example", "Acme", ExpiresAt);

        // Then
        Assert.Equal(ScopeInvitationNoticeText.InvitationSubject, emailService.Subject);
        Assert.DoesNotContain("\n", emailService.Body!, StringComparison.Ordinal);
    }

    [UnitFact]
    public async Task GivenANameWithLineSeparators_WhenAnAnswerIsSent_ThenTheSubjectIsUntouchedAndTheBodyIsOneLine()
    {
        // Given
        var emailService = new StubEmailService();

        // When
        await Sender(emailService).SendAnswerAsync("ada@test.local", "Grace\u2028\r\nBcc: x@evil.example", "Acme\u2029", accepted: true);

        // Then
        Assert.Equal(ScopeInvitationNoticeText.AnswerSubject, emailService.Subject);
        Assert.DoesNotContain("\n", emailService.Body!, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", emailService.Body!, StringComparison.Ordinal);
        Assert.DoesNotContain("\u2028", emailService.Body!, StringComparison.Ordinal);
        Assert.DoesNotContain("\u2029", emailService.Body!, StringComparison.Ordinal);
    }

    [UnitFact]
    public async Task GivenAnAnswer_WhenSent_ThenTheInviterIsTold()
    {
        // Given
        var emailService = new StubEmailService();

        // When
        await Sender(emailService).SendAnswerAsync("ada@test.local", "Grace", "Acme", accepted: false);

        // Then
        Assert.Equal("ada@test.local", emailService.To);
        Assert.Equal(ScopeInvitationNoticeText.AnswerSubject, emailService.Subject);
        Assert.Equal("Grace declined your invitation to co-own the scope Acme.", emailService.Body);
    }

    [UnitFact]
    public async Task GivenMailgunThrows_WhenSending_ThenNothingEscapes()
    {
        // Given
        var emailService = new StubEmailService { ExceptionToThrow = new HttpRequestException("down") };

        // When
        var failure = await Record.ExceptionAsync(() =>
            Sender(emailService).SendInvitationAsync("grace@test.local", "Ada", "Acme", ExpiresAt));

        // Then
        Assert.Null(failure);
    }
}
