using ArturRios.Data.Relational.Core.Interfaces;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Output;
using ArturRios.Heimdall.Command.Services;
using ArturRios.Heimdall.Domain.Entities;
using ArturRios.Heimdall.Shared.Messages;
using ArturRios.Mediator.Command.Interfaces;
using ArturRios.Output;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Heimdall.Command.Handlers;

/// <summary>
///     Handles <see cref="RegisterCommand" /> (UC-47): validates, confirms the scope is open, hashes the
///     password, queues the request and answers. Whether the address is free — and so whether a person
///     is created or the existing owner is told of the attempt — is decided afterwards, in
///     <see cref="RegistrationProcessor" />.
/// </summary>
/// <remarks>
///     <para>
///         Every valid request against an open scope gets the same answer in the same time (AF-47b). The
///         scope check is on the request path because it says nothing about any address: a closed
///         scope is closed for everyone.
///     </para>
///     <para>
///         The password is hashed here rather than in the processor so that the queue never holds a
///         plaintext secret, and so that the request path costs one hash for every caller.
///     </para>
/// </remarks>
public class RegisterCommandHandler(
    IValidator<RegisterCommand> validator,
    IAsyncReadOnlyRepository<Scope, long> scopeReader,
    IRegistrationQueue registrationQueue)
    : ICommandHandlerAsync<RegisterCommand, RegisterCommandOutput>
{
    public async Task<DataOutput<RegisterCommandOutput?>> HandleAsync(
        RegisterCommand command, CancellationToken cancellationToken = default)
    {
        var output = DataOutput<RegisterCommandOutput?>.New;

        // AF-47c: malformed input, refused on the request alone.
        var validation = await validator.ValidateAsync(command, cancellationToken);

        if (!validation.IsValid)
        {
            return output.WithErrors(validation.Errors.Select(failure => failure.ErrorMessage));
        }

        // AF-47a: missing, deleted and closed scopes answer alike.
        var scopeOpen = await scopeReader.Query().AnyAsync(
            scope => scope.PublicId == command.ScopeId && !scope.IsDeleted && scope.SelfRegistrationEnabled,
            cancellationToken);

        if (!scopeOpen)
        {
            return output.WithError(AuthMessages.RegistrationUnavailable);
        }

        var (passwordHash, salt) = await PasswordHashGate.Shared.EncodeWithRandomSaltAsync(command.Password);

        // UC-47 steps 4 onward, deferred. The result is deliberately ignored: a dropped request must
        // answer exactly as a queued one does.
        registrationQueue.TryEnqueue(new RegistrationRequest(
            command.ScopeId!.Value, command.Name.Trim(), command.Email.Trim(), passwordHash, salt));

        return output
            .WithData(new RegisterCommandOutput())
            .WithMessage(AuthMessages.RegistrationRequested);
    }
}
