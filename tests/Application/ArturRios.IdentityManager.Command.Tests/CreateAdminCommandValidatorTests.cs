using ArturRios.IdentityManager.Command.Input;
using ArturRios.IdentityManager.Command.Input.Validation;
using ArturRios.IdentityManager.Domain.Enums;
using ArturRios.IdentityManager.Shared.Messages;
using ArturRios.Util.Test.Attributes;

namespace ArturRios.IdentityManager.Command.Tests;

// Unit tests for CreateAdminCommandValidator (UC-06 path b, AF-06d).
public class CreateAdminCommandValidatorTests
{
    private static CreateAdminCommand Valid() => new()
    {
        Name = "Admin",
        Email = "admin@test.local",
        Password = "Str0ngPass!",
        Role = (int)Roles.ScopeAdmin
    };

    [UnitFact]
    public async Task GivenValidCommand_WhenValidating_ThenNoErrors()
    {
        var result = await new CreateAdminCommandValidator().ValidateAsync(Valid());
        Assert.True(result.IsValid);
    }

    [UnitFact]
    public async Task GivenEmptyPassword_WhenValidating_ThenPasswordRequiredError()
    {
        var command = Valid();
        command.Password = "";
        var result = await new CreateAdminCommandValidator().ValidateAsync(command);
        Assert.Contains(result.Errors, e => e.ErrorMessage == PersonMessages.PasswordRequired);
    }

    [UnitFact]
    public async Task GivenUserRole_WhenValidating_ThenInvalidRoleError()
    {
        var command = Valid();
        command.Role = (int)Roles.User;
        var result = await new CreateAdminCommandValidator().ValidateAsync(command);
        Assert.Contains(result.Errors, e => e.ErrorMessage == PersonMessages.InvalidRole);
    }

    [UnitFact]
    public async Task GivenInvalidEmail_WhenValidating_ThenEmailInvalidError()
    {
        var command = Valid();
        command.Email = "not-an-email";
        var result = await new CreateAdminCommandValidator().ValidateAsync(command);
        Assert.Contains(result.Errors, e => e.ErrorMessage == PersonMessages.EmailInvalid);
    }
}
