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

    private static ServiceCollection Descriptors(EmailDeliveryOptions options, string environment)
    {
        var services = new ServiceCollection();
        services.AddEmailSenders(EnvironmentNamed(environment), options);

        return services;
    }

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

    public static TheoryData<Type, Type, Type> Senders => new()
    {
        { typeof(IEmailVerificationSender), typeof(MailgunEmailVerificationSender), typeof(LoggingEmailVerificationSender) },
        { typeof(IPasswordResetSender), typeof(MailgunPasswordResetSender), typeof(LoggingPasswordResetSender) },
        { typeof(ITwoFactorEmailSender), typeof(MailgunTwoFactorEmailSender), typeof(LoggingTwoFactorEmailSender) },
        { typeof(IRestrictionLiftNotifier), typeof(MailgunRestrictionLiftNotifier), typeof(LoggingRestrictionLiftNotifier) },
        { typeof(IRegistrationAttemptSender), typeof(MailgunRegistrationAttemptSender), typeof(LoggingRegistrationAttemptSender) },
        { typeof(IRegistrationLinkSender), typeof(MailgunRegistrationLinkSender), typeof(LoggingRegistrationLinkSender) },
        { typeof(IEmailChangeNoticeSender), typeof(MailgunEmailChangeNoticeSender), typeof(LoggingEmailChangeNoticeSender) },
        { typeof(IScopeInvitationNoticeSender), typeof(MailgunScopeInvitationNoticeSender), typeof(LoggingScopeInvitationNoticeSender) }
    };

    [UnitTheory]
    [MemberData(nameof(Senders))]
    public void GivenHermesSettings_WhenRegistering_ThenEverySenderDelivers(Type service, Type delivering, Type logging)
    {
        _ = logging;
        var descriptor = Descriptors(Configured, "Production").Single(entry => entry.ServiceType == service);

        Assert.Equal(delivering, descriptor.ImplementationType);
    }

    [UnitTheory]
    [MemberData(nameof(Senders))]
    public void GivenNoHermesSettingsOutsideProduction_WhenRegistering_ThenEverySenderLogs(Type service, Type delivering, Type logging)
    {
        _ = delivering;
        var descriptor = Descriptors(new EmailDeliveryOptions(), "Development").Single(entry => entry.ServiceType == service);

        Assert.Equal(logging, descriptor.ImplementationType);
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
    [InlineData("   ", "key")]
    [InlineData("http://hermes-api.test:8080", "   ")]
    [InlineData("hermes-api.production:8080", "key")]
    [InlineData("[[hermes-base-url]]", "key")]
    [InlineData("ftp://hermes-api.test", "key")]
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
    [InlineData("   ", "key")]
    [InlineData("http://hermes-api.test:8080", "   ")]
    [InlineData("hermes-api.production:8080", "key")]
    [InlineData("[[hermes-base-url]]", "key")]
    [InlineData("ftp://hermes-api.test", "key")]
    public void GivenNoCompleteHermesSettingsInProduction_WhenRegistering_ThenItRefusesToStartNamingThem(string baseUrl, string apiKey)
    {
        var options = new EmailDeliveryOptions { HermesBaseUrl = baseUrl, HermesApiKey = apiKey };

        var refusal = Assert.Throws<InvalidOperationException>(() => Build(options, "Production"));

        Assert.Contains(EmailDeliveryOptions.HermesBaseUrlVariable, refusal.Message);
        Assert.Contains(EmailDeliveryOptions.HermesApiKeyVariable, refusal.Message);
    }

    [UnitFact]
    public void GivenTheDocumentedVariables_WhenRead_ThenEachReachesItsOwnSetting()
    {
        var variables = new Dictionary<string, string?>
        {
            ["HERMES_BASE_URL"] = "http://hermes-api.production:8080",
            ["HERMES_API_KEY"] = "the-key"
        };

        var options = EmailDeliveryOptions.From(name => variables.GetValueOrDefault(name));

        Assert.Equal("http://hermes-api.production:8080", options.HermesBaseUrl);
        Assert.Equal("the-key", options.HermesApiKey);
        Assert.True(options.HermesConfigured);
    }

    [UnitTheory]
    [InlineData("http://hermes-api.production:8080")]
    [InlineData("https://hermes.example.com")]
    public void GivenAnAbsoluteHttpAddress_WhenChecked_ThenHermesIsConfigured(string baseUrl)
    {
        var options = new EmailDeliveryOptions { HermesBaseUrl = baseUrl, HermesApiKey = "key" };

        Assert.True(options.HermesConfigured);
    }
}
