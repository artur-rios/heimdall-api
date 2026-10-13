using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Input.Validation;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Util.Test.Attributes;

namespace ArturRios.Heimdall.Command.Tests;

// Unit tests for RegisterCommandValidator (UC-47, FR-SR-03): the CreateUserCommand field rules, plus a
// scope. Boundaries are tested on both sides.
public class RegisterCommandValidatorTests
{
    private static RegisterCommand Valid() => new()
    {
        Name = "New Person", Email = "new@test.local", Password = "12345678", ScopeId = Guid.NewGuid()
    };

    private static IEnumerable<string> ErrorsFor(RegisterCommand command) =>
        new RegisterCommandValidator().Validate(command).Errors.Select(error => error.ErrorMessage);

    [UnitFact]
    public void GivenAValidCommand_WhenValidating_ThenThereAreNoErrors() =>
        Assert.Empty(ErrorsFor(Valid()));

    [UnitFact]
    public void GivenANameOf200Characters_WhenValidating_ThenItIsAccepted()
    {
        var command = Valid();
        command.Name = new string('a', 200);

        Assert.Empty(ErrorsFor(command));
    }

    [UnitFact]
    public void GivenANameOf201Characters_WhenValidating_ThenNameTooLong()
    {
        var command = Valid();
        command.Name = new string('a', 201);

        Assert.Contains(PersonMessages.NameTooLong, ErrorsFor(command));
    }

    [UnitFact]
    public void GivenAnEmptyName_WhenValidating_ThenNameRequired()
    {
        var command = Valid();
        command.Name = "";

        Assert.Contains(PersonMessages.NameRequired, ErrorsFor(command));
    }

    [UnitFact]
    public void GivenAMalformedEmail_WhenValidating_ThenEmailInvalid()
    {
        var command = Valid();
        command.Email = "not-an-address";

        Assert.Contains(PersonMessages.EmailInvalid, ErrorsFor(command));
    }

    [UnitFact]
    public void GivenASevenCharacterPassword_WhenValidating_ThenPasswordTooShort()
    {
        var command = Valid();
        command.Password = "1234567";

        Assert.Contains(PersonMessages.PasswordTooShort, ErrorsFor(command));
    }

    [UnitFact]
    public void GivenNoScope_WhenValidating_ThenScopeRequired()
    {
        var command = Valid();
        command.ScopeId = null;

        Assert.Contains(AuthMessages.ScopeRequired, ErrorsFor(command));
    }
}
