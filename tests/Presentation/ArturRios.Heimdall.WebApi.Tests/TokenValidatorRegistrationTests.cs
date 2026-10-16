using ArturRios.Heimdall.WebApi.Security;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.WebApi.Security.Authentication;
using ArturRios.Util.WebApi.Security.Enums;
using ArturRios.Util.WebApi.Security.Extensions;
using ArturRios.Util.WebApi.Security.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace ArturRios.Heimdall.WebApi.Tests;

// Unit tests for Startup.ReplaceLibraryTokenValidator (Threat Model TB-5): the registration
// Util.WebApi's AddTokenAuthentication makes is the one PersonTokenValidator replaces, and a
// registration of another shape stops start-up with a message that says so.
public class TokenValidatorRegistrationTests
{
    private static IServiceCollection LibraryRegistration()
    {
        var services = new ServiceCollection();

        // The options Startup registers with
        services.AddTokenAuthentication<IdentityUserMapper>(options =>
        {
            options.Source = TokenSource.Header;
            options.EnableJwt = true;
            options.EnableGoogle = false;
            options.JwtMode = JwtValidationMode.ClaimsOnly;
        });

        return services;
    }

    [UnitFact]
    public void GivenTheLibrarysRegistration_WhenReplacingItsValidator_ThenPersonTokenValidatorIsTheOnlyOne()
    {
        // Given
        var services = LibraryRegistration();

        // When
        Startup.ReplaceLibraryTokenValidator(services);

        // Then — one token validator, Heimdall's, and the library's kept only as the inner one
        var validators = services.Where(descriptor => descriptor.ServiceType == typeof(ITokenValidator)).ToList();
        Assert.Equal(typeof(PersonTokenValidator), Assert.Single(validators).ImplementationType);
        Assert.Contains(services, descriptor =>
            descriptor.ServiceType == typeof(JwtTokenValidator) &&
            descriptor.ImplementationType == typeof(JwtTokenValidator) &&
            descriptor.Lifetime == ServiceLifetime.Singleton);
    }

    [UnitFact]
    public void GivenTheLibrarysValidatorRegisteredThroughAFactory_WhenReplacingIt_ThenStartUpStopsAndSaysWhy()
    {
        // Given — the same service, registered in a shape the replacement does not recognise
        var services = LibraryRegistration();
        var library = services.Single(descriptor => descriptor.ServiceType == typeof(ITokenValidator));
        services.Remove(library);
        services.AddSingleton<ITokenValidator>(provider => provider.GetRequiredService<JwtTokenValidator>());

        // When
        var refusal = Assert.Throws<InvalidOperationException>(() => Startup.ReplaceLibraryTokenValidator(services));

        // Then
        Assert.Contains("JwtTokenValidator", refusal.Message);
        Assert.Contains("PersonTokenValidator cannot replace it", refusal.Message);
        Assert.EndsWith("Refusing to start.", refusal.Message);
    }
}
