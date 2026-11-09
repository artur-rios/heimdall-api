using ArturRios.Heimdall.Command.Input.Validation;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Util.Test.Attributes;
using FluentValidation;

namespace ArturRios.Heimdall.Command.Tests;

// Unit tests for the address rule in PersonFieldRules: an address is accepted only if Hermes, which delivers
// every email Heimdall sends, would accept it too.
public class PersonFieldRulesTests
{
    private sealed class Target
    {
        public string Email { get; init; } = string.Empty;
    }

    private static List<string> ErrorsFor(string email)
    {
        var validator = new InlineValidator<Target>();
        validator.RuleFor(target => target.Email).PersonEmail();

        return validator.Validate(new Target { Email = email }).Errors.Select(error => error.ErrorMessage).ToList();
    }

    [UnitTheory]
    [InlineData("\"quoted\"@example.com")]
    [InlineData("\"a,b\"@example.com")]
    [InlineData("a..b@example.com")]
    [InlineData(".a@example.com")]
    [InlineData("Foo <a@example.com>")]
    [InlineData("<a@example.com>")]
    [InlineData("a@example")]
    [InlineData("a@[127.0.0.1]")]
    [InlineData("a@[IPv6:::1]")]
    [InlineData("a@example.com.")]
    [InlineData("a b@example.com")]
    [InlineData("a(comment)@example.com")]
    [InlineData("x@[a@b.c]")]
    [InlineData("a@x.com, b@x.com")]
    public void GivenAnAddressHermesCannotDeliverTo_WhenValidating_ThenEmailInvalid(string email)
    {
        // Given / When
        var errors = ErrorsFor(email);

        // Then
        Assert.Contains(PersonMessages.EmailInvalid, errors);
    }

    [UnitTheory]
    [InlineData("a@example.com")]
    [InlineData("A.B+c@Example.COM")]
    [InlineData("first.last-x@sub.example.co.uk")]
    [InlineData("josé@exämple.com")]
    public void GivenADeliverableAddress_WhenValidating_ThenThereAreNoErrors(string email)
    {
        // Given / When
        var errors = ErrorsFor(email);

        // Then
        Assert.Empty(errors);
    }

    [UnitFact]
    public void GivenSurroundingWhitespace_WhenValidating_ThenItIsAcceptedBecauseTheHandlersTrimIt()
    {
        // Given / When
        var errors = ErrorsFor("  a@example.com ");

        // Then
        Assert.Empty(errors);
    }

    [UnitFact]
    public void GivenANullAddress_WhenValidating_ThenOnlyEmailRequiredIsReported()
    {
        // Given / When
        var errors = ErrorsFor(null!);

        // Then
        Assert.Equal([PersonMessages.EmailRequired], errors);
    }
}
