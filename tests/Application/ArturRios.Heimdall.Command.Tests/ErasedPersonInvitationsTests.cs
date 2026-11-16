using ArturRios.Heimdall.Command.Auditing;
using ArturRios.Heimdall.Command.Services;
using ArturRios.Heimdall.Command.Tests.Support;
using ArturRios.Heimdall.Domain.Persistence;
using ArturRios.Util.Test.Attributes;
using Microsoft.Extensions.Logging;
using Moq;

namespace ArturRios.Heimdall.Command.Tests;

// Unit tests for ErasedPersonInvitations, the step every erasure path shares: the invitations the erased persons
// were part of are voided, and each void is audited. The audit is best effort: the erasure has already happened,
// so a failed audit write is logged and the remaining voids are still audited.
public class ErasedPersonInvitationsTests
{
    private static readonly long[] s_erased = [10, 11];

    private static readonly DateTime s_now = new(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc);

    private sealed class FailingFirstAuditLogWriter : IAuditLogWriter
    {
        private int _calls;

        public List<Guid?> Written { get; } = [];

        public Task WriteAsync(string action, Guid? targetId, bool succeeded, string? failureReason)
        {
            if (_calls++ == 0)
            {
                throw new InvalidOperationException("audit store unavailable");
            }

            Written.Add(targetId);

            return Task.CompletedTask;
        }
    }

    private static Mock<IScopeOwnerInvitations> Voiding(params Guid[] voided)
    {
        var invitations = new Mock<IScopeOwnerInvitations>();
        invitations
            .Setup(store => store.ForgetPersonsAsync(It.IsAny<IReadOnlyCollection<long>>(), s_now))
            .ReturnsAsync(voided);

        return invitations;
    }

    [UnitFact]
    public async Task GivenNoPersons_WhenForgetting_ThenNothingIsVoidedOrAudited()
    {
        // Given
        var invitations = Voiding(Guid.NewGuid());
        var audit = new RecordingAuditLogWriter();

        // When
        var voided = await ErasedPersonInvitations.ForgetAsync(
            invitations.Object, audit, new RecordingLogger<ErasedPersonInvitationsTests>(), [], s_now);

        // Then the store is not even asked
        Assert.Empty(voided);
        Assert.Empty(audit.Entries);
        invitations.Verify(
            store => store.ForgetPersonsAsync(It.IsAny<IReadOnlyCollection<long>>(), It.IsAny<DateTime>()),
            Times.Never);
    }

    [UnitFact]
    public async Task GivenVoidedInvitations_WhenForgetting_ThenEachVoidIsAuditedAsASuccessAndReturned()
    {
        // Given
        Guid[] ids = [Guid.NewGuid(), Guid.NewGuid()];
        var invitations = Voiding(ids);
        var audit = new RecordingAuditLogWriter();

        // When
        var voided = await ErasedPersonInvitations.ForgetAsync(
            invitations.Object, audit, new RecordingLogger<ErasedPersonInvitationsTests>(), s_erased, s_now);

        // Then
        Assert.Equal(ids, voided);
        (string, Guid?, bool)[] expected =
        [
            (ScopeOwnerInvitationAuditActions.Voided, ids[0], true),
            (ScopeOwnerInvitationAuditActions.Voided, ids[1], true)
        ];
        Assert.Equal(expected, audit.Entries);
        invitations.Verify(
            store => store.ForgetPersonsAsync(
                It.Is<IReadOnlyCollection<long>>(persons => persons.SequenceEqual(s_erased)), s_now),
            Times.Once);
    }

    [UnitFact]
    public async Task GivenAnAuditWriteFails_WhenForgetting_ThenItIsLoggedAndTheOtherVoidsAreStillAudited()
    {
        // Given the audit store refusing the first entry
        Guid[] ids = [Guid.NewGuid(), Guid.NewGuid()];
        var audit = new FailingFirstAuditLogWriter();
        var logger = new RecordingLogger<ErasedPersonInvitationsTests>();

        // When
        var voided = await ErasedPersonInvitations.ForgetAsync(Voiding(ids).Object, audit, logger, [10], s_now);

        // Then the erasure's result stands, the failure names the invitation, and the second void is audited
        Assert.Equal(ids, voided);
        var (level, text) = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, level);
        Assert.Contains($"Failed to audit the void of invitation {ids[0]}", text);
        Guid?[] audited = [ids[1]];
        Assert.Equal(audited, audit.Written);
    }
}
