using ArturRios.Data.Relational.Core.DependencyInjection;
using ArturRios.Heimdall.Command.Auditing;
using ArturRios.Heimdall.Command.Handlers;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Output;
using ArturRios.Heimdall.Command.Services;
using ArturRios.Heimdall.WebApi.Email;
using ArturRios.Messaging.Email;
using ArturRios.Util.WebApi.Configuration;
using ArturRios.Util.WebApi.Security.Extensions;
using Serilog;

namespace ArturRios.Heimdall.WebApi;

public static class EmailRegistrationExtensions
{
    /// <summary>
    ///     Chooses how the two transactional emails — UC-06's verification token and UC-12's
    ///     password reset token — are delivered. With Mailgun credentials present, both go out for
    ///     real through <c>ArturRios.Messaging</c>; without them, both are logged.
    /// </summary>
    /// <remarks>
    ///     The fallback is deliberate rather than a failure: a developer running the API locally,
    ///     and the functional suite, both need person creation and password recovery to work without
    ///     credentials and without reaching the network. Failing startup instead would make Mailgun
    ///     a prerequisite for running the tests. In Production, though, the fallback logs a
    ///     verification token, a password reset token, or a 2FA code in plaintext on every send — an
    ///     account-takeover primitive for anyone who can read the logs — so an unconfigured Production
    ///     deployment fails startup outright instead of silently degrading into that.
    /// </remarks>
    public static IServiceCollection AddEmailSenders(this IServiceCollection services, IHostEnvironment environment)
    {
        var options = EmailDeliveryOptions.FromEnvironment();

        services.AddSingleton(options);

        if (!options.MailgunConfigured)
        {
            if (environment.IsProduction())
            {
                throw new InvalidOperationException(
                    $"Mailgun is not configured ({MailgunEmailService.ApiKeyVariable} / " +
                    $"{MailgunEmailService.DomainVariable}) in Production. Refusing to start: the " +
                    "fallback sender logs verification tokens, password reset tokens, and 2FA codes " +
                    "in plaintext, which must never happen outside local development and tests.");
            }

            Log.Warning(
                "Mailgun is not configured ({ApiKeyVariable} / {DomainVariable}); verification and " +
                "password reset tokens will be logged instead of emailed",
                MailgunEmailService.ApiKeyVariable, MailgunEmailService.DomainVariable);

            services.AddScoped<IEmailVerificationSender, LoggingEmailVerificationSender>();
            services.AddScoped<IPasswordResetSender, LoggingPasswordResetSender>();
            services.AddScoped<ITwoFactorEmailSender, LoggingTwoFactorEmailSender>();
            services.AddScoped<IRestrictionLiftNotifier, LoggingRestrictionLiftNotifier>();
            services.AddScoped<IRegistrationAttemptSender, LoggingRegistrationAttemptSender>();
            services.AddScoped<IRegistrationLinkSender, LoggingRegistrationLinkSender>();
            services.AddScoped<IEmailChangeNoticeSender, LoggingEmailChangeNoticeSender>();
            services.AddScoped<IScopeInvitationNoticeSender, LoggingScopeInvitationNoticeSender>();

            return services;
        }

        // A typed client, so the Mailgun service reuses pooled connections instead of creating an
        // HttpClient per send.
        services.AddHttpClient<IEmailService, MailgunEmailService>();
        services.AddScoped<IEmailVerificationSender, MailgunEmailVerificationSender>();
        services.AddScoped<IPasswordResetSender, MailgunPasswordResetSender>();
        services.AddScoped<ITwoFactorEmailSender, MailgunTwoFactorEmailSender>();
        services.AddScoped<IRestrictionLiftNotifier, MailgunRestrictionLiftNotifier>();
        services.AddScoped<IRegistrationAttemptSender, MailgunRegistrationAttemptSender>();
        services.AddScoped<IRegistrationLinkSender, MailgunRegistrationLinkSender>();
        services.AddScoped<IEmailChangeNoticeSender, MailgunEmailChangeNoticeSender>();
        services.AddScoped<IScopeInvitationNoticeSender, MailgunScopeInvitationNoticeSender>();

        return services;
    }

    /// <summary>
    ///     Registers the e-mail verification and password reset services, the cooldown and invitation
    ///     options, the password recovery and registration queues with their dispatchers, and the audited
    ///     handlers of the administrator-initiated e-mails (UC-12, UC-47, UC-51, UC-52).
    /// </summary>
    public static IServiceCollection AddAccountEmailFlows(this IServiceCollection services)
    {
        services.AddSingleton(EmailVerificationOptions.FromEnvironment());
        services.AddScoped<IEmailVerificationService, EmailVerificationService>();
        services.AddSingleton(PasswordResetOptions.FromEnvironment());
        services.AddScoped<IPasswordResetService, PasswordResetService>();
        // UC-51/UC-52 (FR-EV-07, FR-PR-06): the per-person cooldown on administrator-initiated email,
        // read once at start-up like the token lifetimes it is compared against.
        var adminEmail = AdminEmailOptions.FromEnvironment();
        ConfigurationWarnings.WarnOnFallback(AdminEmailOptions.CooldownVariable, adminEmail.RejectedCooldown,
            AdminEmailOptions.DefaultCooldownSeconds);
        services.AddSingleton(adminEmail);

        // UC-21 (FR-SC-18): how long a scope owner invitation stays acceptable, read once at start-up.
        var scopeInvitation = ScopeInvitationOptions.FromEnvironment();
        ConfigurationWarnings.WarnOnFallback(ScopeInvitationOptions.LifetimeVariable, scopeInvitation.RejectedLifetime,
            ScopeInvitationOptions.DefaultLifetimeSeconds);
        services.AddSingleton(scopeInvitation);
        // UC-51: no validator — the only input is the route's person id, already constrained to a GUID.
        services.AddAuditedCommandHandler<SendPersonVerificationEmailCommand, SendPersonVerificationEmailCommandOutput, SendPersonVerificationEmailCommandHandler>();
        // UC-52: no validator either — the route's person id is the whole of the input.
        services.AddAuditedCommandHandler<SendPersonPasswordResetCommand, SendPersonPasswordResetCommandOutput, SendPersonPasswordResetCommandHandler>();
        // UC-12 runs in two halves (AF-12a): the handler queues, the dispatcher does the work after
        // the caller has been answered, so no address takes longer to answer than another. See
        // PasswordRecoveryQueue for why this is an in-memory queue rather than a floor or an outbox.
        services.AddSingleton<PasswordRecoveryQueue>();
        services.AddSingleton<IPasswordRecoveryQueue>(
            provider => provider.GetRequiredService<PasswordRecoveryQueue>());
        services.AddScoped<PasswordRecoveryProcessor>();
        services.AddHostedService<PasswordRecoveryDispatcher>();
        // UC-47 runs in the same two halves as UC-12 (AF-47b): the handler queues, the dispatcher
        // stores the pending registration or tells the holder of the attempt after the caller has been
        // answered. Following the mailed link (UC-14) then reaches the confirmer, which creates the User.
        services.AddSingleton<RegistrationQueue>();
        services.AddSingleton<IRegistrationQueue>(
            provider => provider.GetRequiredService<RegistrationQueue>());
        services.AddScoped<RegistrationProcessor>();
        // #206: the cap on a scope's live pending registrations, read once at start-up.
        var registration = RegistrationOptions.FromEnvironment();

        if (registration.RejectedMaxPendingPerScope is not null)
        {
            Log.Warning(
                "Ignoring unusable {Variable}={Value}; using the default of {Default}",
                RegistrationOptions.MaxPendingPerScopeVariable, registration.RejectedMaxPendingPerScope,
                RegistrationOptions.DefaultMaxPendingPerScope);
        }

        services.AddSingleton(registration);
        services.AddScoped<IPendingRegistrationConfirmer, PendingRegistrationConfirmer>();
        services.AddHostedService<RegistrationDispatcher>();

        return services;
    }
}
