using ArturRios.Heimdall.Command.Services;
using ArturRios.Util.Test.Attributes;

namespace ArturRios.Heimdall.Command.Tests;

// Unit tests for ScopeInvitationNoticeText (UC-21, UC-60, UC-61): the invitation notice is the sentence the
// specification fixes, carries no token and no link, and folds names an administrator controls onto one
// line before they reach a third party's mailbox (Review Focus 5).
public class ScopeInvitationNoticeTextTests
{
    private static readonly DateTime ExpiresAt = new(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc);

    [UnitFact]
    public void GivenAnInvitation_WhenWritten_ThenItIsTheSpecifiedSentence()
    {
        // Given
        // When
        var body = ScopeInvitationNoticeText.Invitation("Ada Lovelace", "Acme", ExpiresAt);

        // Then
        Assert.Equal(
            "Ada Lovelace invited you to co-own the scope Acme. Sign in to Heimdall to accept or decline. " +
            "The invitation expires on 2026-10-23 12:00 UTC.",
            body);
    }

    [UnitFact]
    public void GivenAnInvitation_WhenWritten_ThenItCarriesNoLink()
    {
        // Given
        // When
        var body = ScopeInvitationNoticeText.Invitation("Ada Lovelace", "Acme", ExpiresAt);

        // Then
        Assert.DoesNotContain("http", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", body, StringComparison.OrdinalIgnoreCase);
    }

    [UnitTheory]
    [InlineData(true, "Grace Hopper accepted your invitation to co-own the scope Acme.")]
    [InlineData(false, "Grace Hopper declined your invitation to co-own the scope Acme.")]
    public void GivenAnAnswer_WhenWritten_ThenItNamesTheInviteeAndTheScope(bool accepted, string expected)
    {
        // Given
        // When
        var body = ScopeInvitationNoticeText.Answer("Grace Hopper", "Acme", accepted);

        // Then
        Assert.Equal(expected, body);
    }

    [UnitFact]
    public void GivenANameWithLineBreaksAndControlCharacters_WhenWritten_ThenItReachesTheBodyAsOneLine()
    {
        // Given — Review Focus 5: a Scope Admin renamed themselves to smuggle lines into the notice
        const string name = "Ada\r\nSupport desk:\tcall now\u0007\u2028 ";

        // When
        var body = ScopeInvitationNoticeText.Invitation(name, "Acme", ExpiresAt);

        // Then
        Assert.StartsWith("Ada Support desk: call now invited you", body);
        Assert.DoesNotContain("\n", body, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", body, StringComparison.Ordinal);
        Assert.DoesNotContain("\t", body, StringComparison.Ordinal);
        Assert.DoesNotContain("\u0007", body, StringComparison.Ordinal);
    }

    [UnitFact]
    public void GivenTheSubjects_WhenRead_ThenNeitherNamesAnybody()
    {
        // Given
        // When
        // Then — nothing a caller controls goes into a header
        Assert.Equal("You were invited to co-own a scope", ScopeInvitationNoticeText.InvitationSubject);
        Assert.Equal("Your scope invitation was answered", ScopeInvitationNoticeText.AnswerSubject);
    }

    [UnitTheory]
    [InlineData("Ada\u2028Support", "Ada Support")]
    [InlineData("Ada\u2029Support", "Ada Support")]
    [InlineData("Ada\rSupport", "Ada Support")]
    [InlineData("Ada\nSupport", "Ada Support")]
    [InlineData("Ada\u0085Support", "Ada Support")]
    [InlineData("Ada\u0000\u001b[31mSupport", "Ada [31mSupport")]
    [InlineData("\r\n  ", "")]
    public void GivenAValueWithSeparatorsOrControlCharacters_WhenFolded_ThenItIsOneLine(string value, string expected)
    {
        // Given
        // When
        var folded = ScopeInvitationNoticeText.OneLine(value);

        // Then
        Assert.Equal(expected, folded);
    }

    [UnitFact]
    public void GivenAScopeNameWithLineBreaks_WhenAnAnswerIsWritten_ThenItReachesTheBodyAsOneLine()
    {
        // Given
        const string scope = "Acme\r\n\u2028Bcc: x@evil.example";

        // When
        var body = ScopeInvitationNoticeText.Answer("Grace\nHopper", scope, accepted: true);

        // Then
        Assert.Equal("Grace Hopper accepted your invitation to co-own the scope Acme Bcc: x@evil.example.", body);
    }
}
