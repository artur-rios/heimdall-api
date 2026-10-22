using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Query.Input;
using ArturRios.Heimdall.Query.Output;
using ArturRios.Heimdall.WebApi.Controllers;
using ArturRios.Heimdall.WebApi.Security;
using ArturRios.Mediator.Query;
using ArturRios.Mediator.Query.Interfaces;
using ArturRios.Output;
using ArturRios.Util.Test.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace ArturRios.Heimdall.WebApi.Tests;

// Unit tests for the controllers' half of #219: every query action hands the mediator
// HttpContext.RequestAborted, which the mediator forwards to the handler. CA2016 cannot see this, because
// a controller action holds no token parameter, so without these a controller could quietly go back to
// the default token and an abandoned read would run to completion again. Each test drives one action
// against a recording handler and asserts the handler received the request's own token.
public class QueryCancellationWiringTests
{
    private sealed class RecordingPaginatedHandler<TQuery, TOutput> : IPaginatedQueryHandlerAsync<TQuery, TOutput>
        where TQuery : BaseQuery
        where TOutput : QueryOutput
    {
        public CancellationToken? Received { get; private set; }

        public Task<PaginatedOutput<TOutput>> HandleAsync(TQuery query, CancellationToken cancellationToken = default)
        {
            Received = cancellationToken;

            return Task.FromResult(PaginatedOutput<TOutput>.New);
        }
    }

    private sealed class RecordingHandler<TQuery, TOutput> : IQueryHandlerAsync<TQuery, TOutput>
        where TQuery : BaseQuery
        where TOutput : QueryOutput
    {
        public CancellationToken? Received { get; private set; }

        public Task<DataOutput<TOutput?>> HandleAsync(TQuery query, CancellationToken cancellationToken = default)
        {
            Received = cancellationToken;

            return Task.FromResult(DataOutput<TOutput?>.New);
        }
    }

    public static TheoryData<string> Actions => ["audit-list", "scope-list", "scope-get"];

    [UnitTheory]
    [MemberData(nameof(Actions))]
    public async Task GivenRequestToken_WhenCallingAQueryAction_ThenTheHandlerReceivesIt(string action)
    {
        // Given a request whose RequestAborted is a token of its own, and a recording handler per query
        using var aborted = new CancellationTokenSource();
        var audit = new RecordingPaginatedHandler<ListAuditLogQuery, AuditLogEntryOutput>();
        var scopes = new RecordingPaginatedHandler<ListScopesQuery, ScopeOutput>();
        var scope = new RecordingHandler<GetScopeByIdQuery, ScopeOutput>();

        var services = new ServiceCollection()
            .AddSingleton<IPaginatedQueryHandlerAsync<ListAuditLogQuery, AuditLogEntryOutput>>(audit)
            .AddSingleton<IPaginatedQueryHandlerAsync<ListScopesQuery, ScopeOutput>>(scopes)
            .AddSingleton<IQueryHandlerAsync<GetScopeByIdQuery, ScopeOutput>>(scope)
            .BuildServiceProvider();

        var mediator = new QueryMediator(services.GetRequiredService<IServiceScopeFactory>());
        var httpContext = new DefaultHttpContext { RequestAborted = aborted.Token };

        // The auth middleware attaches the caller under this key; ApplyActor reads it back.
        httpContext.Items["User"] = new IdentityUser(Guid.NewGuid(), (int)Roles.SystemAdmin);
        var context = new ControllerContext { HttpContext = httpContext };

        // When the action runs
        CancellationToken? received;

        switch (action)
        {
            case "audit-list":
                await new AuditController(mediator) { ControllerContext = context }.List(new ListAuditLogQuery());
                received = audit.Received;
                break;
            case "scope-list":
                await new ScopeController(null!, mediator) { ControllerContext = context }.List(new ListScopesQuery());
                received = scopes.Received;
                break;
            default:
                await new ScopeController(null!, mediator) { ControllerContext = context }.GetById(Guid.NewGuid());
                received = scope.Received;
                break;
        }

        // Then the handler saw the request's own token, not the default
        Assert.Equal(aborted.Token, received);
    }
}
