using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Query.Handlers;
using ArturRios.Heimdall.Query.Input;
using ArturRios.Heimdall.Query.Input.Validation;
using ArturRios.Util.Test.Attributes;
using ArturRios.Util.Test.Mock;

namespace ArturRios.Heimdall.Query.Tests;

// Unit tests for the read path's cancellation (#219): a token the controller passes from
// HttpContext.RequestAborted must reach validation and the EF Core calls, so an abandoned request
// stops its query instead of running to completion. Each test hands a handler an already-cancelled
// token and expects OperationCanceledException, the documented behaviour of FluentValidation when
// it is given one. The in-memory repository's queryable ignores the token, so what is observed
// here is the validation step; the EF Core calls after it are forwarded the same way and are held
// to it by rule CA2016, which fails the build.
public class QueryCancellationTests
{
    private static CancellationToken Cancelled()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();

        return source.Token;
    }

    [UnitFact]
    public async Task GivenCancelledToken_WhenListingScopes_ThenCancellationIsObserved()
    {
        // Given a handler over a scope and a request that was already abandoned
        var repository = new AsyncFakeRepository<Scope, long>();
        await repository.CreateAsync(new Scope { Id = 1, PublicId = Guid.NewGuid(), Name = "Alpha" });
        var handler = new ListScopesQueryHandler(repository, new ListScopesQueryValidator());

        // When / Then the handler stops with OperationCanceledException instead of returning a page
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => handler.HandleAsync(
            new ListScopesQuery { ActingRole = (int)Roles.SystemAdmin, PageNumber = 1, PageSize = 10 },
            Cancelled()));
    }

    [UnitFact]
    public async Task GivenCancelledToken_WhenListingSystemAdmins_ThenCancellationIsObserved()
    {
        // Given a handler over no persons and a request that was already abandoned
        var handler = new ListSystemAdminsQueryHandler(
            new AsyncFakeRepository<Person, long>(), new ListSystemAdminsQueryValidator());

        // When / Then validation observes the token before any query runs
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => handler.HandleAsync(
            new ListSystemAdminsQuery { ActingRole = (int)Roles.SystemAdmin, PageNumber = 1, PageSize = 10 },
            Cancelled()));
    }
}
