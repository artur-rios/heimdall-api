using ArturRios.Heimdall.Command.Services;
using ArturRios.Heimdall.WebApi.Email;
using ArturRios.Messaging.Email;
using ArturRios.Util.Test.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArturRios.Heimdall.WebApi.Tests;

// UC-05 AF-04 and AF-05, and which IEmailService backs the senders. FR-HE-07 keeps the logging senders outside
// production; FR-HE-06 refuses to start in production, where those senders would write tokens to the log.
public class EmailSenderRegistrationTests
{
    private static readonly EmailDeliveryOptions Configured = new()
    {
        HermesBaseUrl = "http://hermes-api.test:8080",
        HermesApiKey = "key"
    };

    private static IHostEnvironment EnvironmentNamed(string name) =>
        new Microsoft.Extensions.Hosting.Internal.HostingEnvironment { EnvironmentName = name };

    private static ServiceProvider Build(EmailDeliveryOptions options, string environment)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddLogging();
        services.AddEmailSenders(EnvironmentNamed(environment), options);

        return services.BuildServiceProvider();
    }

    [UnitTheory]
    [InlineData("Production")]
    [InlineData("Development")]
    public void GivenHermesSettings_WhenRegistering_ThenEmailGoesThroughHermes(string environment)
    {
        using var provider = Build(Configured, environment);

        Assert.IsType<HermesEmailService>(provider.GetRequiredService<IEmailService>());
    }

    [UnitFact]
    public void GivenHermesSettings_WhenRegistering_ThenTheSendersAreTheOnesThatDeliver()
    {
        using var provider = Build(Configured, "Production");
        using var scope = provider.CreateScope();

        Assert.IsType<MailgunPasswordResetSender>(scope.ServiceProvider.GetRequiredService<IPasswordResetSender>());
    }

    [UnitFact]
    public void GivenHermesSettings_WhenRegistering_ThenTheClientWaitsLongerThanHermesCanTake()
    {
        using var provider = Build(Configured, "Production");

        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(IEmailService));

        Assert.Equal(TimeSpan.FromSeconds(25), client.Timeout);
    }

    [UnitTheory]
    [InlineData("", "")]
    [InlineData("http://hermes-api.test:8080", "")]
    [InlineData("", "key")]
    public void GivenNoCompleteHermesSettingsOutsideProduction_WhenRegistering_ThenTheLoggingSendersAreUsed(string baseUrl, string apiKey)
    {
        var options = new EmailDeliveryOptions { HermesBaseUrl = baseUrl, HermesApiKey = apiKey };

        using var provider = Build(options, "Development");
        using var scope = provider.CreateScope();

        Assert.IsType<LoggingPasswordResetSender>(scope.ServiceProvider.GetRequiredService<IPasswordResetSender>());
        Assert.Null(provider.GetService<IEmailService>());
    }

    [UnitTheory]
    [InlineData("", "")]
    [InlineData("http://hermes-api.test:8080", "")]
    [InlineData("", "key")]
    public void GivenNoCompleteHermesSettingsInProduction_WhenRegistering_ThenItRefusesToStartNamingThem(string baseUrl, string apiKey)
    {
        var options = new EmailDeliveryOptions { HermesBaseUrl = baseUrl, HermesApiKey = apiKey };

        var refusal = Assert.Throws<InvalidOperationException>(() => Build(options, "Production"));

        Assert.Contains(EmailDeliveryOptions.HermesBaseUrlVariable, refusal.Message);
        Assert.Contains(EmailDeliveryOptions.HermesApiKeyVariable, refusal.Message);
    }

    [UnitFact]
    public void GivenTheEnvironmentVariablesNames_WhenRead_ThenTheyAreTheDocumentedOnes()
    {
        Assert.Equal("HERMES_BASE_URL", EmailDeliveryOptions.HermesBaseUrlVariable);
        Assert.Equal("HERMES_API_KEY", EmailDeliveryOptions.HermesApiKeyVariable);
    }
}
