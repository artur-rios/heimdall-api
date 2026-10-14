using System.Data.Common;
using ArturRios.Data.Relational.Core.Repositories;
using ArturRios.Heimdall.Command.Handlers;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Input.Validation;
using ArturRios.Heimdall.Command.Services;
using ArturRios.Heimdall.Data.Persistence;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Heimdall.WebApi.Tests.Support;
using ArturRios.Util.Test.Attributes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using static ArturRios.Heimdall.WebApi.Tests.Support.ApplicationSeeds;
using Application = ArturRios.Heimdall.Domain.Entities.Application;

namespace ArturRios.Heimdall.WebApi.Tests;

/// <summary>
///     A revocation (UC-55) racing a token exchange (UC-56) or another revocation, against the real
///     database: the revocation that commits first must stand, whatever the other request read.
/// </summary>
/// <remarks>
///     <para>
///         The race is made deterministic rather than hoped for. Each handler runs over EF repositories
///         and <see cref="AtomicWrites" /> on a context whose interceptor, just before that context sends its first <c>UPDATE</c>,
///         revokes the credential through a second connection and commits. So the handler has read the
///         credential live, and the revocation lands between its read and its write — the window in
///         which a whole-row save used to write <c>revoked_at</c> back as it had read it.
///     </para>
///     <para>
///         The competing revocation is stamped a minute in the past, so the assertions can tell its
///         time from any the handler under test would write.
///     </para>
/// </remarks>
[Collection(nameof(FunctionalCollection))]
public class ApplicationCredentialRaceTests(PostgresFixture db)
{
    [FunctionalFact]
    public async Task GivenARevocationCommittedAfterTheExchangeReadIt_WhenExchanging_ThenRefusedAndTheRevocationStands()
    {
        // Given
        var (scope, _, application) = await SeedOwnedApplicationAsync(db);
        var (credential, secret) = await SeedCredentialAsync(db, application);
        var revokedAt = OneMinuteAgo();
        var issuer = new RecordingIssuer();
        await using var context = db.CreateContext(new BeforeFirstUpdate(() => RevokeAsync(credential, revokedAt)));
        var handler = new IssueApplicationTokenCommandHandler(new IssueApplicationTokenCommandValidator(),
            new EfRepository<ApplicationCredential, long>(context), new AtomicWrites(context), issuer);

        // When
        var output = await handler.HandleAsync(new IssueApplicationTokenCommand
        {
            ApplicationId = application.PublicId, ScopeId = scope.PublicId, Secret = secret
        });

        // Then
        Assert.Equal([ApplicationTokenMessages.CredentialsNotAccepted], output.Errors);
        Assert.Equal(0, issuer.Calls);
        var stored = Assert.Single(await CredentialsOfAsync(db, application));
        Assert.Equal(revokedAt, stored.RevokedAt);
        Assert.Null(stored.LastUsedAt);
    }

    [FunctionalFact]
    public async Task GivenARevocationCommittedAfterAnotherReadIt_WhenRevoking_ThenAlreadyRevokedAndTheFirstTimeStands()
    {
        // Given
        var (scope, _, application) = await SeedOwnedApplicationAsync(db);
        var (credential, _) = await SeedCredentialAsync(db, application);
        var revokedAt = OneMinuteAgo();
        await using var context = db.CreateContext(new BeforeFirstUpdate(() => RevokeAsync(credential, revokedAt)));
        var handler = new RevokeApplicationCredentialCommandHandler(new EfRepository<Application, long>(context),
            new EfRepository<ApplicationCredential, long>(context), new AtomicWrites(context));

        // When
        var output = await handler.HandleAsync(new RevokeApplicationCredentialCommand
        {
            ScopeId = scope.PublicId, Id = application.PublicId, CredentialId = credential.PublicId,
            ActingRole = (int)Roles.SystemAdmin, ActingPersonId = Guid.NewGuid()
        });

        // Then
        Assert.Equal([ApplicationCredentialMessages.CredentialAlreadyRevoked], output.Errors);
        Assert.Equal(revokedAt, Assert.Single(await CredentialsOfAsync(db, application)).RevokedAt);
    }

    /// <summary>A time PostgreSQL stores exactly (whole seconds), distinct from any the handler writes.</summary>
    private static DateTime OneMinuteAgo()
    {
        var time = DateTime.UtcNow.AddMinutes(-1);
        return new DateTime(time.Ticks - time.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);
    }

    /// <summary>The competing admin's revocation: its own connection, committed when this returns.</summary>
    private async Task RevokeAsync(ApplicationCredential credential, DateTime revokedAt)
    {
        await using var other = db.CreateContext();
        await other.ApplicationCredentials
            .Where(x => x.Id == credential.Id)
            .ExecuteUpdateAsync(set => set.SetProperty(x => x.RevokedAt, revokedAt));
    }

    /// <summary>Runs an action once, just before the context's first <c>UPDATE</c> reaches the database.</summary>
    private sealed class BeforeFirstUpdate(Func<Task> action) : DbCommandInterceptor
    {
        private bool _fired;

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            await FireBeforeUpdateAsync(command);
            return result;
        }

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            await FireBeforeUpdateAsync(command);
            return result;
        }

        private async Task FireBeforeUpdateAsync(DbCommand command)
        {
            if (_fired || !command.CommandText.TrimStart().StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _fired = true;
            await action();
        }
    }

    /// <summary>Counts the tokens the handler asks for; it must ask for none when it refuses.</summary>
    private sealed class RecordingIssuer : IApplicationTokenIssuer
    {
        public int Calls { get; private set; }

        public Task<AuthToken> IssueAsync(ApplicationTokenSubject subject)
        {
            Calls++;
            return Task.FromResult(new AuthToken("signed.application.token", DateTime.UtcNow.AddMinutes(10)));
        }
    }
}
