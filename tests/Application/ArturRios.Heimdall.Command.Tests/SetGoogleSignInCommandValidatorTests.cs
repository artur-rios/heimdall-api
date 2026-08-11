using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Input.Validation;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Util.Test.Attributes;
using FluentValidation.TestHelper;

namespace ArturRios.Heimdall.Command.Tests;

public class SetGoogleSignInCommandValidatorTests
{
    private readonly SetGoogleSignInCommandValidator _validator = new();

    [UnitFact]
    public void GivenEnabledNotSupplied_WhenValidating_ThenEnabledRequiredError()
    {
        // Given a request that never said which value to set — the case a plain bool would have
        // bound to false, silently disabling Google Sign-In (AF-24c, NFR-10)
        var command = new SetGoogleSignInCommand { Id = Guid.NewGuid(), Enabled = null };

        // When
        var result = _validator.TestValidate(command);

        // Then
        result.ShouldHaveValidationErrorFor(x => x.Enabled).WithErrorMessage(ScopeMessages.EnabledRequired);
    }

    [UnitTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void GivenEnabledSupplied_WhenValidating_ThenNoEnabledError(bool enabled)
    {
        // Given — both halves of "Enable/Disable" are valid input
        var command = new SetGoogleSignInCommand { Id = Guid.NewGuid(), Enabled = enabled };

        // When
        var result = _validator.TestValidate(command);

        // Then
        result.ShouldNotHaveValidationErrorFor(x => x.Enabled);
    }
}
