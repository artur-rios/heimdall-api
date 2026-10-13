using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Input.Validation;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Util.Test.Attributes;

namespace ArturRios.Heimdall.Command.Tests;

// Unit tests for SetScopeEmailLinksCommandValidator: each link, when present, must be an absolute
// http(s) URL that fits its column. The rule is the privacy-notice rule of UpdateScopeCommandValidator,
// including its Unix quirk: "/some/path" parses as an absolute file:// URI there, so the scheme is
// checked and not only the absoluteness.
public class SetScopeEmailLinksCommandValidatorTests
{
    private static readonly string[] LinkFields = ["verification", "reset"];

    private static SetScopeEmailLinksCommand Command(string field, string? url) => field == "verification"
        ? new SetScopeEmailLinksCommand { EmailVerificationUrl = url }
        : new SetScopeEmailLinksCommand { PasswordResetUrl = url };

    public static TheoryData<string, string> Accepted()
    {
        var data = new TheoryData<string, string>();

        foreach (var field in LinkFields)
        {
            data.Add(field, "https://app.test/verify-email");
            data.Add(field, "http://localhost:3000/verify-email");
            data.Add(field, "https://app.test/verify-email?lang=en");
        }

        return data;
    }

    public static TheoryData<string, string> Refused()
    {
        var data = new TheoryData<string, string>();

        foreach (var field in LinkFields)
        {
            data.Add(field, "/unix/path");
            data.Add(field, "relative/page");
            data.Add(field, "file:///etc/passwd");
            data.Add(field, "ftp://app.test/verify");
            data.Add(field, "javascript:alert(1)");
        }

        return data;
    }

    [UnitTheory]
    [MemberData(nameof(Accepted))]
    public void GivenAnAbsoluteHttpOrHttpsUrl_WhenValidated_ThenItIsAccepted(string field, string url)
    {
        var result = new SetScopeEmailLinksCommandValidator().Validate(Command(field, url));

        Assert.True(result.IsValid);
    }

    [UnitTheory]
    [MemberData(nameof(Refused))]
    public void GivenAnUnusableUrl_WhenValidated_ThenEmailLinkInvalid(string field, string url)
    {
        var result = new SetScopeEmailLinksCommandValidator().Validate(Command(field, url));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.ErrorMessage == ScopeMessages.EmailLinkInvalid);
    }

    [UnitTheory]
    [InlineData("verification")]
    [InlineData("reset")]
    public void GivenAUrlOfExactlyTheColumnLength_WhenValidated_ThenItIsAccepted(string field)
    {
        var url = "https://app.test/" + new string('a', 2048 - "https://app.test/".Length);

        var result = new SetScopeEmailLinksCommandValidator().Validate(Command(field, url));

        Assert.Equal(2048, url.Length);
        Assert.True(result.IsValid);
    }

    [UnitTheory]
    [InlineData("verification")]
    [InlineData("reset")]
    public void GivenAUrlOneCharacterOverTheColumn_WhenValidated_ThenEmailLinkInvalid(string field)
    {
        var url = "https://app.test/" + new string('a', 2049 - "https://app.test/".Length);

        var result = new SetScopeEmailLinksCommandValidator().Validate(Command(field, url));

        Assert.Equal(2049, url.Length);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.ErrorMessage == ScopeMessages.EmailLinkInvalid);
    }

    [UnitTheory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void GivenNoUrl_WhenValidated_ThenItIsAcceptedAsAClear(string? url)
    {
        var result = new SetScopeEmailLinksCommandValidator().Validate(
            new SetScopeEmailLinksCommand { EmailVerificationUrl = url, PasswordResetUrl = url });

        Assert.True(result.IsValid);
    }

    [UnitFact]
    public void GivenAValidLinkAndAnInvalidOne_WhenValidated_ThenTheCommandIsRefused()
    {
        var result = new SetScopeEmailLinksCommandValidator().Validate(new SetScopeEmailLinksCommand
        {
            EmailVerificationUrl = "https://app.test/verify-email", PasswordResetUrl = "/unix/path"
        });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.ErrorMessage == ScopeMessages.EmailLinkInvalid);
    }
}
