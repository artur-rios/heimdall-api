using ArturRios.Mediator.Command;

namespace ArturRios.Heimdall.Command.Output;

/// <summary>
///     Result of <see cref="Input.ResendTwoFactorChallengeCodeCommand" /> (UC-46). Deliberately
///     empty, for the reason <see cref="PasswordRecoveryCommandOutput" /> is: any field describing
///     what happened — whether a challenge was real, whether the person has the email method,
///     whether a code went out, how many reissues remain — would answer the question every
///     alternative flow of UC-46 exists to leave unanswered. A new challenge token in particular is
///     absent by design and not by omission: returning one would only be possible for a genuine
///     challenge, which is precisely the oracle this response must not be.
/// </summary>
public class ResendTwoFactorChallengeCodeCommandOutput : CommandOutput;
