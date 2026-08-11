using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Output;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Domain.Enums;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Heimdall.Shared.Services;
using ArturRios.Mediator.Command.Interfaces;
using ArturRios.Output;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Application = ArturRios.Heimdall.Domain.Entities.Application;

namespace ArturRios.Heimdall.Command.Handlers;

/// <summary>
///     Handles <see cref="CreateApplicationCommand" /> (UC-16, FR-AP-01/02/03): validates input
///     (AF-16d), verifies the target scope exists and is active (AF-16a), enforces the acting role's
///     rule — a Scope Admin must own the scope (AF-16e) and may only name themself as owner
///     (AF-16c), a System Admin bypasses both — verifies the owner is a non-deleted
///     <c>ScopeAdmin</c> who owns the scope (AF-16b), then creates the application record.
/// </summary>
public class CreateApplicationCommandHandler(
    IValidator<CreateApplicationCommand> validator,
    IAsyncReadOnlyRepository<Scope> scopeReader,
    IAsyncReadOnlyRepository<Person> personReader,
    IAsyncRepository<Application> applicationWriter,
    IScopeOwnershipChecker scopeOwnership)
    : ICommandHandlerAsync<CreateApplicationCommand, CreateApplicationCommandOutput>
{
    public async Task<DataOutput<CreateApplicationCommandOutput?>> HandleAsync(CreateApplicationCommand command)
    {
        var output = DataOutput<CreateApplicationCommandOutput?>.New;

        // AF-16d: validate input shape.
        var validation = await validator.ValidateAsync(command);

        if (!validation.IsValid)
        {
            return output.WithErrors(validation.Errors.Select(failure => failure.ErrorMessage));
        }

        // AF-16a: the target scope must exist and not be logically deleted.
        var scope = await scopeReader.Query()
            .FirstOrDefaultAsync(x => x.PublicId == command.ScopeId && !x.IsDeleted);

        if (scope is null)
        {
            return output.WithError(ApplicationMessages.ScopeNotFound);
        }

        // AF-16e: a Scope Admin actor may only act on a scope they own; a System Admin bypasses the
        // check inside the checker. Asked before who was named, so an actor acting where they have no
        // standing is told that, rather than being told about the owner.
        if (!await scopeOwnership.ActorMayManageScopeAsync(
                command.ActingRole, command.ActingPersonId, scope.Id))
        {
            return output.WithError(ApplicationMessages.NotScopeOwner);
        }

        // AF-16c: a Scope Admin may only create applications they own. Decided from the command
        // alone, before the owner is read, so the refusal cannot reveal whether the named person
        // exists. A System Admin may name any owner FR-AP-03 accepts.
        if (command.ActingRole != (int)Roles.SystemAdmin && command.OwnerId != command.ActingPersonId)
        {
            return output.WithError(ApplicationMessages.CannotSetAnotherOwner);
        }

        // AF-16b / FR-AP-03: the owner must be an existing, non-logically-deleted ScopeAdmin who owns
        // the scope. The role is asserted alongside the SCOPE_OWNER row: SRD §4.5 already restricts
        // that table to ScopeAdmins, so this states the requirement rather than adding to it.
        var owner = await personReader.Query().FirstOrDefaultAsync(person =>
            person.PublicId == command.OwnerId && !person.IsDeleted &&
            person.RoleId == (long)Roles.ScopeAdmin &&
            person.ScopeOwnerships.Any(ownership => ownership.ScopeId == scope.Id));

        if (owner is null)
        {
            return output.WithError(ApplicationMessages.OwnerNotValidForScope);
        }

        // FR-AP-01/02: create the application in the scope, active.
        var application = new Application
        {
            Name = command.Name,
            IsDeleted = false,
            ScopeId = scope.Id,
            OwnerId = owner.Id
        };

        var creation = await applicationWriter.CreateAsync(application);

        if (!creation.Success)
        {
            return output.WithErrors(creation.Errors);
        }

        return output
            .WithData(new CreateApplicationCommandOutput
            {
                Id = application.PublicId,
                Name = application.Name,
                ScopeId = scope.PublicId,
                OwnerId = owner.PublicId,
                CreatedAt = application.CreatedAt
            })
            .WithMessage(ApplicationMessages.ApplicationCreatedSuccessfully);
    }
}
