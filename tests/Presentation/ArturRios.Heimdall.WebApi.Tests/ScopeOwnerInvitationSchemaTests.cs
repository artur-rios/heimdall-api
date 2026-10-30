using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.WebApi.Tests.Support;
using ArturRios.Util.Test.Attributes;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using static ArturRios.Heimdall.WebApi.Tests.Support.ApplicationSeeds;

namespace ArturRios.Heimdall.WebApi.Tests;

/// <summary>
///     <c>scope_owner_invitation</c>'s own rules, against real PostgreSQL (UC-21, FR-SC-14): one unended
///     invitation per scope and invitee, only a known outcome and only on an ended row, and what each hard
///     delete does to an invitation that names the deleted row.
/// </summary>
[Collection(nameof(FunctionalCollection))]
public class ScopeOwnerInvitationSchemaTests(PostgresFixture db)
{
    private const string UniqueViolation = "23505";
    private const string CheckViolation = "23514";

    private async Task<ScopeOwnerInvitation> SeedInvitationAsync(
        Scope scope, Person invitee, Person? inviter, DateTime? endedAt = null,
        ScopeOwnerInvitationOutcomes? outcome = null)
    {
        await using var context = db.CreateContext();
        var invitation = new ScopeOwnerInvitation
        {
            PublicId = Guid.NewGuid(),
            ScopeId = scope.Id,
            InviteePersonId = invitee.Id,
            InvitedByPersonId = inviter?.Id,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(7),
            EndedAt = endedAt,
            Outcome = outcome
        };
        context.ScopeOwnerInvitations.Add(invitation);
        await context.SaveChangesAsync();
        return invitation;
    }

    private async Task<Exception?> RunAsync(string statement)
    {
        await using var connection = new NpgsqlConnection(db.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(statement, connection);

        return await Record.ExceptionAsync(() => command.ExecuteNonQueryAsync());
    }

    private async Task<ScopeOwnerInvitation?> ReloadAsync(ScopeOwnerInvitation invitation)
    {
        await using var context = db.CreateContext();
        return await context.ScopeOwnerInvitations.AsNoTracking().FirstOrDefaultAsync(x => x.Id == invitation.Id);
    }

    [FunctionalFact]
    public async Task GivenAnOpenInvitation_WhenASecondOneIsInsertedForThePair_ThenTheDatabaseRefusesIt()
    {
        // Given
        var scope = await Seeds.ScopeAsync(db);
        var inviter = await Seeds.PersonAsync(db, Roles.ScopeAdmin, new PersonOptions { Name = "Admin", EmailPrefix = "admin", Owns = [scope] });
        var invitee = await Seeds.PersonAsync(db, Roles.ScopeAdmin, new PersonOptions { Name = "Admin", EmailPrefix = "admin" });
        await SeedInvitationAsync(scope, invitee, inviter);

        // When
        var failure = await Record.ExceptionAsync(() => SeedInvitationAsync(scope, invitee, inviter));

        // Then
        var refusal = Assert.IsType<PostgresException>(Assert.IsType<DbUpdateException>(failure).InnerException);
        Assert.Equal(UniqueViolation, refusal.SqlState);
        Assert.Equal("ix_scope_owner_invitation_scope_id_invitee_person_id", refusal.ConstraintName);
    }

    [FunctionalFact]
    public async Task GivenAnEndedInvitation_WhenANewOneIsInsertedForThePair_ThenTheDatabaseAllowsIt()
    {
        // Given
        var scope = await Seeds.ScopeAsync(db);
        var inviter = await Seeds.PersonAsync(db, Roles.ScopeAdmin, new PersonOptions { Name = "Admin", EmailPrefix = "admin", Owns = [scope] });
        var invitee = await Seeds.PersonAsync(db, Roles.ScopeAdmin, new PersonOptions { Name = "Admin", EmailPrefix = "admin" });
        await SeedInvitationAsync(scope, invitee, inviter, DateTime.UtcNow, ScopeOwnerInvitationOutcomes.Declined);

        // When
        var failure = await Record.ExceptionAsync(() => SeedInvitationAsync(scope, invitee, inviter));

        // Then
        Assert.Null(failure);
    }

    [FunctionalFact]
    public async Task GivenAnUnendedInvitation_WhenAnOutcomeIsWritten_ThenTheDatabaseRefusesIt()
    {
        // Given
        var scope = await Seeds.ScopeAsync(db);
        var invitee = await Seeds.PersonAsync(db, Roles.ScopeAdmin, new PersonOptions { Name = "Admin", EmailPrefix = "admin" });
        var invitation = await SeedInvitationAsync(scope, invitee, null);

        // When
        var failure = await RunAsync($"UPDATE heimdall.scope_owner_invitation SET outcome = 1 WHERE id = {invitation.Id}");

        // Then
        var refusal = Assert.IsType<PostgresException>(failure);
        Assert.Equal(CheckViolation, refusal.SqlState);
        Assert.Equal("ck_scope_owner_invitation_outcome_ends_it", refusal.ConstraintName);
    }

    [FunctionalFact]
    public async Task GivenAnUnknownOutcome_WhenAnEndedInvitationIsInsertedWithIt_ThenTheDatabaseRefusesIt()
    {
        // Given
        var scope = await Seeds.ScopeAsync(db);
        var invitee = await Seeds.PersonAsync(db, Roles.ScopeAdmin, new PersonOptions { Name = "Admin", EmailPrefix = "admin" });

        // When
        var failure = await Record.ExceptionAsync(() =>
            SeedInvitationAsync(scope, invitee, null, DateTime.UtcNow, (ScopeOwnerInvitationOutcomes)5));

        // Then
        var refusal = Assert.IsType<PostgresException>(Assert.IsType<DbUpdateException>(failure).InnerException);
        Assert.Equal(CheckViolation, refusal.SqlState);
        Assert.Equal("ck_scope_owner_invitation_outcome_known", refusal.ConstraintName);
    }

    [FunctionalFact]
    public async Task GivenNoOutcome_WhenAnInvitationEndedByExpiryIsInserted_ThenTheDatabaseAllowsIt()
    {
        // Given — the shape ScopeOwnerInvitations.OpenAsync leaves when it closes an expired row
        var scope = await Seeds.ScopeAsync(db);
        var invitee = await Seeds.PersonAsync(db, Roles.ScopeAdmin, new PersonOptions { Name = "Admin", EmailPrefix = "admin" });

        // When
        var failure = await Record.ExceptionAsync(() =>
            SeedInvitationAsync(scope, invitee, null, DateTime.UtcNow, outcome: null));

        // Then
        Assert.Null(failure);
    }

    [FunctionalFact]
    public async Task GivenAnOutcome_WhenAnInvitationWithNoEndIsInsertedWithIt_ThenTheDatabaseRefusesIt()
    {
        // Given
        var scope = await Seeds.ScopeAsync(db);
        var invitee = await Seeds.PersonAsync(db, Roles.ScopeAdmin, new PersonOptions { Name = "Admin", EmailPrefix = "admin" });

        // When
        var failure = await Record.ExceptionAsync(() =>
            SeedInvitationAsync(scope, invitee, null, endedAt: null, ScopeOwnerInvitationOutcomes.Declined));

        // Then
        var refusal = Assert.IsType<PostgresException>(Assert.IsType<DbUpdateException>(failure).InnerException);
        Assert.Equal(CheckViolation, refusal.SqlState);
        Assert.Equal("ck_scope_owner_invitation_outcome_ends_it", refusal.ConstraintName);
    }

    [FunctionalFact]
    public async Task GivenAnOpenInvitation_WhenTheInviterIsHardDeleted_ThenItSurvivesWithoutAnInviter()
    {
        // Given — Review Focus 4: the inviter is removed by UC-10 while the invitation is open
        var scope = await Seeds.ScopeAsync(db);
        var inviter = await Seeds.PersonAsync(db, Roles.ScopeAdmin, new PersonOptions { Name = "Admin", EmailPrefix = "admin", Owns = [scope] });
        var invitee = await Seeds.PersonAsync(db, Roles.ScopeAdmin, new PersonOptions { Name = "Admin", EmailPrefix = "admin" });
        var invitation = await SeedInvitationAsync(scope, invitee, inviter);

        // When
        Assert.Null(await RunAsync($"DELETE FROM heimdall.person WHERE id = {inviter.Id}"));

        // Then
        var stored = await ReloadAsync(invitation);
        Assert.NotNull(stored);
        Assert.Null(stored.InvitedByPersonId);
        Assert.Null(stored.EndedAt);
    }

    [FunctionalFact]
    public async Task GivenInvitations_WhenTheirInviteeOrScopeIsHardDeleted_ThenTheyGoWithIt()
    {
        // Given
        var scope = await Seeds.ScopeAsync(db);
        var otherScope = await Seeds.ScopeAsync(db);
        var invitee = await Seeds.PersonAsync(db, Roles.ScopeAdmin, new PersonOptions { Name = "Admin", EmailPrefix = "admin" });
        var otherInvitee = await Seeds.PersonAsync(db, Roles.ScopeAdmin, new PersonOptions { Name = "Admin", EmailPrefix = "admin" });
        var ofInvitee = await SeedInvitationAsync(scope, invitee, null);
        var ofScope = await SeedInvitationAsync(otherScope, otherInvitee, null);

        // When
        Assert.Null(await RunAsync($"DELETE FROM heimdall.person WHERE id = {invitee.Id}"));
        Assert.Null(await RunAsync($"DELETE FROM heimdall.scope WHERE id = {otherScope.Id}"));

        // Then
        Assert.Null(await ReloadAsync(ofInvitee));
        Assert.Null(await ReloadAsync(ofScope));
    }
}
