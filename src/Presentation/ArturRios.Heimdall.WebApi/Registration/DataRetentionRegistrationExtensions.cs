using ArturRios.Data.Relational.Core.DependencyInjection;
using ArturRios.Heimdall.Command.Auditing;
using ArturRios.Heimdall.Command.Handlers;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Output;
using ArturRios.Heimdall.Shared.Retention;
using ArturRios.Heimdall.WebApi.Monitoring;
using ArturRios.Heimdall.WebApi.Retention;
using ArturRios.Util.WebApi.Configuration;
using ArturRios.Util.WebApi.Security.Extensions;
using Serilog;

namespace ArturRios.Heimdall.WebApi;

public static class DataRetentionRegistrationExtensions
{
    /// <summary>
    ///     Registers the retention schedule (NFR-19) and the pass that enforces it.
    /// </summary>
    /// <remarks>
    ///     The options are a singleton because they are a deployment-wide policy read once at
    ///     start-up, and the purge is registered as a hosted service unless it has been switched
    ///     off — which is a deployment decision an operator has to take deliberately, since keeping
    ///     personal data past its retention period is the thing NFR-19 forbids. The handler is
    ///     registered audited like every other command: the entry recording that the schedule was
    ///     enforced is as much the point as the deletion is.
    /// </remarks>
    public static IServiceCollection AddDataRetention(this IServiceCollection services)
    {
        var retention = DataRetentionOptions.FromEnvironment();

        if (retention.InvalidVariables.Count > 0)
        {
            Log.Warning(
                "Ignoring unusable retention settings and applying the documented defaults: {Variables}",
                retention.InvalidVariables);
        }

        services.AddSingleton(retention);
        services
            .AddAuditedCommandHandler<PurgeExpiredTokensCommand, PurgeExpiredTokensCommandOutput,
                PurgeExpiredTokensCommandHandler>();
        services
            .AddAuditedCommandHandler<AnonymiseExpiredDeletionsCommand, AnonymiseExpiredDeletionsCommandOutput,
                AnonymiseExpiredDeletionsCommandHandler>();
        services
            .AddAuditedCommandHandler<PseudonymiseAuditActorsCommand, PseudonymiseAuditActorsCommandOutput,
                PseudonymiseAuditActorsCommandHandler>();

        if (retention.PurgeEnabled)
        {
            services.AddHostedService<TokenRetentionService>();
        }
        else
        {
            Log.Warning(
                "The token retention purge is switched off by {Variable}; expired tokens will not be removed",
                DataRetentionOptions.PurgeEnabledVariable);
        }

        if (retention.AnonymisationEnabled)
        {
            services.AddHostedService<IdentityAnonymisationService>();
        }
        else
        {
            Log.Warning(
                "Identity anonymisation is switched off by {Variable}; logically deleted identities " +
                "will be kept past their retention window",
                DataRetentionOptions.AnonymisationEnabledVariable);
        }

        if (retention.SecurityMonitoringEnabled)
        {
            services.AddHostedService<SecurityMonitoringService>();
        }
        else
        {
            Log.Warning(
                "Security signal detection is switched off by {Variable}; the audit trail and lockout " +
                "counters will keep recording what happens and nobody will be told about it",
                DataRetentionOptions.SecurityMonitoringEnabledVariable);
        }

        if (retention.AuditPseudonymisationEnabled)
        {
            services.AddHostedService<AuditRetentionService>();
        }
        else
        {
            Log.Warning(
                "Audit attribution pseudonymisation is switched off by {Variable}; the trail will " +
                "keep naming people past its attribution period, including erased ones",
                DataRetentionOptions.AuditPseudonymisationEnabledVariable);
        }

        return services;
    }
}
