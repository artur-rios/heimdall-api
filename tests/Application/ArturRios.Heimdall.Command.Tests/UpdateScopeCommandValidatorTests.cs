using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Input.Validation;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Util.Test.Attributes;
using FluentValidation.TestHelper;

namespace ArturRios.Heimdall.Command.Tests;

public class UpdateScopeCommandValidatorTests
{
    private readonly UpdateScopeCommandValidator _validator = new();

    [UnitFact]
    public void GivenEmptyName_WhenValidating_ThenNameRequiredError()
    {
        // Given
        var command = new UpdateScopeCommand { Id = Guid.NewGuid(), Name = string.Empty };

        // When
        var result = _validator.TestValidate(command);

        // Then
        result.ShouldHaveValidationErrorFor(x => x.Name).WithErrorMessage(ScopeMessages.NameRequired);
    }

    [UnitFact]
    public void GivenNonEmptyName_WhenValidating_ThenNoNameError()
    {
        // Given
        var command = new UpdateScopeCommand { Id = Guid.NewGuid(), Name = "Acme" };

        // When
        var result = _validator.TestValidate(command);

        // Then
        result.ShouldNotHaveValidationErrorFor(x => x.Name);
    }

    [UnitFact]
    public void GivenNameOf201Characters_WhenValidating_ThenNameTooLongIsReported()
    {
        // Given a name one character past the column that stores it. Without this rule the value
        // reached PostgreSQL and came back as the persistence layer's data-access failure rather
        // than naming the field (NFR-10).
        var command = new UpdateScopeCommand { Id = Guid.NewGuid(), Name = new string('a', 201) };

        var result = _validator.TestValidate(command);

        result.ShouldHaveValidationErrorFor(x => x.Name).WithErrorMessage(ScopeMessages.NameTooLong);
    }

    [UnitFact]
    public void GivenNameOf200Characters_WhenValidating_ThenNoNameError()
    {
        var command = new UpdateScopeCommand { Id = Guid.NewGuid(), Name = new string('a', 200) };

        var result = _validator.TestValidate(command);

        result.ShouldNotHaveValidationErrorFor(x => x.Name);
    }

    [UnitFact]
    public void GivenDescriptionOf501Characters_WhenValidating_ThenDescriptionTooLongIsReported()
    {
        var command = new UpdateScopeCommand
        {
            Id = Guid.NewGuid(), Name = "Acme", Description = new string('a', 501)
        };

        var result = _validator.TestValidate(command);

        result.ShouldHaveValidationErrorFor(x => x.Description)
            .WithErrorMessage(ScopeMessages.DescriptionTooLong);
    }

    [UnitFact]
    public void GivenNullDescription_WhenValidating_ThenNoDescriptionError()
    {
        var command = new UpdateScopeCommand { Id = Guid.NewGuid(), Name = "Acme", Description = null };

        var result = _validator.TestValidate(command);

        result.ShouldNotHaveValidationErrorFor(x => x.Description);
    }

    [UnitFact]
    public void GivenConsentAsTheBasis_WhenValidated_ThenItIsRefused()
    {
        // Refused rather than accepted-and-ignored: a tenant that asked for consent and was silently
        // given contract performance would believe a basis applied that did not. Consent needs a
        // withdrawal path as easy as giving it (GDPR Art. 7(3)), and none exists.
        var result = new UpdateScopeCommandValidator().Validate(
            new UpdateScopeCommand { Name = "Acme", DefaultLegalBasis = (int)LegalBases.Consent });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.ErrorMessage == ScopeMessages.ConsentBasisNotSupported);
    }

    [UnitFact]
    public void GivenUnrecordedAsTheBasis_WhenValidated_ThenItIsRefused()
    {
        // Unrecorded describes rows predating the mechanism; allowing it to be chosen would stop the
        // marker meaning what it says.
        var result = new UpdateScopeCommandValidator().Validate(
            new UpdateScopeCommand { Name = "Acme", DefaultLegalBasis = (int)LegalBases.Unrecorded });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.ErrorMessage == ScopeMessages.LegalBasisUnknown);
    }

    [UnitTheory]
    [InlineData((int)LegalBases.ContractPerformance)]
    [InlineData((int)LegalBases.LegitimateInterests)]
    [InlineData((int)LegalBases.LegalObligation)]
    [InlineData(null)]
    public void GivenAUsableBasis_WhenValidated_ThenItIsAccepted(int? basis)
    {
        var result = new UpdateScopeCommandValidator().Validate(
            new UpdateScopeCommand { Name = "Acme", DefaultLegalBasis = basis });

        Assert.DoesNotContain(
            result.Errors,
            e => e.ErrorMessage == ScopeMessages.ConsentBasisNotSupported
                 || e.ErrorMessage == ScopeMessages.LegalBasisUnknown);
    }

    [UnitTheory]
    [InlineData("not a uri")]
    [InlineData("/relative/only")]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://acme.test/privacy")]
    public void GivenAnUnusablePrivacyNoticeUri_WhenValidated_ThenItIsRefused(string uri)
    {
        // "/relative/only" is the one that caught a real flaw: on Unix, Uri.TryCreate parses a
        // leading-slash path as an absolute file:// URI, so checking absoluteness alone accepted a
        // local filesystem path as a tenant's published notice. The rule checks the scheme.
        var result = new UpdateScopeCommandValidator().Validate(
            new UpdateScopeCommand { Name = "Acme", PrivacyNoticeUri = uri });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.ErrorMessage == ScopeMessages.PrivacyNoticeUriInvalid);
    }

    [UnitFact]
    public void GivenAnAbsolutePrivacyNoticeUri_WhenValidated_ThenItIsAccepted()
    {
        var result = new UpdateScopeCommandValidator().Validate(
            new UpdateScopeCommand { Name = "Acme", PrivacyNoticeUri = "https://acme.test/privacy" });

        Assert.DoesNotContain(result.Errors, e => e.ErrorMessage == ScopeMessages.PrivacyNoticeUriInvalid);
    }
}