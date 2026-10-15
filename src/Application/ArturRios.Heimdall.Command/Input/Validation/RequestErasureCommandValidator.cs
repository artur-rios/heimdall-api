using ArturRios.Heimdall.Command.Input;
using FluentValidation;

namespace ArturRios.Heimdall.Command.Input.Validation;

/// <summary>
///     Validates <see cref="RequestErasureCommand" /> (NFR-10). It deliberately checks nothing: the
///     request has no shape that can be wrong before the caller is known.
/// </summary>
/// <remarks>
///     Which credential is required depends on whether the caller is a person or a Google User, and
///     that is not knowable from the request — it takes a database read. So even a missing
///     credential is the handler's to refuse (AF-42a): for a person it is a wrong password, charged
///     to the re-authentication budget and answered with the message every re-authentication shares
///     (FR-AU-13); for a Google User, a token that was not accepted. Refusing it here would answer a
///     person differently from a wrong password, and leave the attempt uncounted.
/// </remarks>
public class RequestErasureCommandValidator : AbstractValidator<RequestErasureCommand>;
