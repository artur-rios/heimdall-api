using ArturRios.Mediator.Command;
using ArturRios.Mediator.Command.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace ArturRios.Heimdall.Command.Auditing;

public static class CommandHandlerRegistrationExtensions
{
    /// <summary>
    ///     Registers <typeparamref name="THandler" /> and wraps it with <see cref="AuditingCommandHandler{TCommand,TOutput}" />
    ///     so every command handler produces an audit trail entry on success (NFR-09), without any
    ///     change to the handler itself.
    /// </summary>
    public static IServiceCollection AddAuditedCommandHandler<TCommand, TOutput, THandler>(
        this IServiceCollection services)
        where TCommand : BaseCommand
        where TOutput : CommandOutput
        where THandler : class, ICommandHandlerAsync<TCommand, TOutput>
    {
        services.AddScoped<THandler>();
        services.AddScoped<ICommandHandlerAsync<TCommand, TOutput>>(provider =>
            new AuditingCommandHandler<TCommand, TOutput>(
                provider.GetRequiredService<THandler>(),
                provider.GetRequiredService<IAuditLogWriter>(),
                provider.GetRequiredService<
                    Microsoft.Extensions.Logging.ILogger<AuditingCommandHandler<TCommand, TOutput>>>()));

        return services;
    }
}
