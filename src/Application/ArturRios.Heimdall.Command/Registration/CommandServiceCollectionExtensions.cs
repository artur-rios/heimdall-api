using ArturRios.Heimdall.Command.Auditing;
using ArturRios.Heimdall.Command.Handlers;
using ArturRios.Heimdall.Command.Input;
using ArturRios.Heimdall.Command.Input.Validation;
using ArturRios.Heimdall.Command.Output;
using ArturRios.Heimdall.Command.Services;
using ArturRios.Mediator.Command;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;

namespace ArturRios.Heimdall.Command.Registration;

public static class CommandServiceCollectionExtensions
{
    /// <summary>
    ///     Registers the command mediator, the audit log writer and the audited command handlers (with their
    ///     validators) of the scope, person, authentication, application and scope permission use cases.
    /// </summary>
    public static IServiceCollection AddCommandHandlers(this IServiceCollection services)
    {
        // #219: commands are dispatched without a cancellation token, by design. A write that has begun
        // must finish or roll back as one unit, not stop halfway because the client went away, so the
        // controllers hand ExecuteCommandAsync no HttpContext.RequestAborted and the command handlers
        // forward no token to EF Core. Queries are the opposite: they receive the request's token.
        services.AddScoped<CommandMediator>();
        services.AddScoped<IAuditLogWriter, AuditLogWriter>();
        services.AddScoped<IValidator<CreateScopeCommand>, CreateScopeCommandValidator>();
        services.AddAuditedCommandHandler<CreateScopeCommand, CreateScopeCommandOutput, CreateScopeCommandHandler>();
        services.AddScoped<IValidator<UpdateScopeCommand>, UpdateScopeCommandValidator>();
        services.AddAuditedCommandHandler<UpdateScopeCommand, UpdateScopeCommandOutput, UpdateScopeCommandHandler>();
        services.AddAuditedCommandHandler<DeleteScopeCommand, DeleteScopeCommandOutput, DeleteScopeCommandHandler>();
        services.AddAuditedCommandHandler<HardDeleteScopeCommand, HardDeleteScopeCommandOutput, HardDeleteScopeCommandHandler>();
        // UC-24 does have a validator despite carrying a single field: Enabled is nullable so an
        // omitted value is refused (NFR-10) rather than binding to false and disabling the setting.
        services.AddScoped<IValidator<SetGoogleSignInCommand>, SetGoogleSignInCommandValidator>();
        services.AddAuditedCommandHandler<SetGoogleSignInCommand, SetGoogleSignInCommandOutput, SetGoogleSignInCommandHandler>();
        services.AddScoped<IValidator<SetSelfRegistrationCommand>, SetSelfRegistrationCommandValidator>();
        services.AddAuditedCommandHandler<SetSelfRegistrationCommand, SetSelfRegistrationCommandOutput, SetSelfRegistrationCommandHandler>();
        services.AddScoped<IValidator<SetScopeEmailLinksCommand>, SetScopeEmailLinksCommandValidator>();
        services.AddAuditedCommandHandler<SetScopeEmailLinksCommand, SetScopeEmailLinksCommandOutput, SetScopeEmailLinksCommandHandler>();
        // UC-50 (FR-SR-12): no validator — both identifiers are route values already constrained to
        // GUIDs. Audited like every write, with the registration as target.
        services.AddAuditedCommandHandler<DiscardPendingRegistrationCommand, DiscardPendingRegistrationCommandOutput, DiscardPendingRegistrationCommandHandler>();
        services.AddScoped<IValidator<CreateAdminCommand>, CreateAdminCommandValidator>();
        services.AddAuditedCommandHandler<CreateAdminCommand, CreatePersonCommandOutput, CreateAdminCommandHandler>();
        services.AddScoped<IValidator<CreateUserCommand>, CreateUserCommandValidator>();
        services.AddAuditedCommandHandler<CreateUserCommand, CreatePersonCommandOutput, CreateUserCommandHandler>();
        services.AddScoped<IValidator<CreateScopeOwnerCommand>, CreateScopeOwnerCommandValidator>();
        services.AddAuditedCommandHandler<CreateScopeOwnerCommand, CreatePersonCommandOutput, CreateScopeOwnerCommandHandler>();
        // No validator: UC-21's request carries no body — both identifiers are route values already
        // constrained to GUIDs — so there is nothing left for NFR-10 to validate.
        services.AddAuditedCommandHandler<AddScopeOwnerCommand, AddScopeOwnerCommandOutput, AddScopeOwnerCommandHandler>();
        // UC-58: an owner or a System Admin withdraws an open scope owner invitation.
        services.AddAuditedCommandHandler<WithdrawScopeOwnerInvitationCommand, WithdrawScopeOwnerInvitationCommandOutput, WithdrawScopeOwnerInvitationCommandHandler>();
        // UC-60/UC-61: the invitee accepts or declines; no validator — the route's id is the whole input.
        services.AddAuditedCommandHandler<AcceptScopeOwnerInvitationCommand, AcceptScopeOwnerInvitationCommandOutput, AcceptScopeOwnerInvitationCommandHandler>();
        services.AddAuditedCommandHandler<DeclineScopeOwnerInvitationCommand, DeclineScopeOwnerInvitationCommandOutput, DeclineScopeOwnerInvitationCommandHandler>();
        // Likewise no validator for UC-22: both identifiers are route values.
        services.AddAuditedCommandHandler<RemoveScopeOwnerCommand, RemoveScopeOwnerCommandOutput, RemoveScopeOwnerCommandHandler>();
        // Likewise no validator for UC-23: both identifiers are route values.
        services.AddAuditedCommandHandler<PromoteScopeUserCommand, PromoteScopeUserCommandOutput, PromoteScopeUserCommandHandler>();
        services.AddScoped<IValidator<UpdatePersonCommand>, UpdatePersonCommandValidator>();
        services.AddAuditedCommandHandler<UpdatePersonCommand, UpdatePersonCommandOutput, UpdatePersonCommandHandler>();
        services.AddAuditedCommandHandler<DeletePersonCommand, DeletePersonCommandOutput, DeletePersonCommandHandler>();
        services.AddAuditedCommandHandler<HardDeletePersonCommand, HardDeletePersonCommandOutput, HardDeletePersonCommandHandler>();
        services.AddScoped<IValidator<LoginCommand>, LoginCommandValidator>();
        services.AddAuditedCommandHandler<LoginCommand, LoginCommandOutput, LoginCommandHandler>();
        services.AddScoped<IValidator<PasswordRecoveryCommand>, PasswordRecoveryCommandValidator>();
        services.AddAuditedCommandHandler<PasswordRecoveryCommand, PasswordRecoveryCommandOutput, PasswordRecoveryCommandHandler>();
        services.AddScoped<IValidator<RegisterCommand>, RegisterCommandValidator>();
        services.AddAuditedCommandHandler<RegisterCommand, RegisterCommandOutput, RegisterCommandHandler>();
        services.AddScoped<IValidator<ResetPasswordCommand>, ResetPasswordCommandValidator>();
        services.AddAuditedCommandHandler<ResetPasswordCommand, ResetPasswordCommandOutput, ResetPasswordCommandHandler>();
        services.AddScoped<IValidator<VerifyEmailCommand>, VerifyEmailCommandValidator>();
        services.AddAuditedCommandHandler<VerifyEmailCommand, VerifyEmailCommandOutput, VerifyEmailCommandHandler>();
        services.AddScoped<IValidator<RequestErasureCommand>, RequestErasureCommandValidator>();
        services
            .AddAuditedCommandHandler<RequestErasureCommand, RequestErasureCommandOutput,
                RequestErasureCommandHandler>();
        services
            .AddAuditedCommandHandler<ExportMyDataCommand, DataExportCommandOutput,
                ExportMyDataCommandHandler>();
        services
            .AddAuditedCommandHandler<RestrictProcessingCommand, RestrictProcessingCommandOutput,
                RestrictProcessingCommandHandler>();
        services
            .AddAuditedCommandHandler<LiftProcessingRestrictionCommand,
                LiftProcessingRestrictionCommandOutput, LiftProcessingRestrictionCommandHandler>();
        services
            .AddAuditedCommandHandler<ReapplyErasuresCommand, ReapplyErasuresCommandOutput,
                ReapplyErasuresCommandHandler>();
        // No validator: UC-15's request carries no caller-supplied input at all — the person comes
        // from the bearer token — so there is nothing for NFR-10 to validate.
        services.AddAuditedCommandHandler<ResendVerificationEmailCommand, ResendVerificationEmailCommandOutput, ResendVerificationEmailCommandHandler>();
        // Likewise no validator for UC-25, for a different reason: the use case defines no 400 flow
        // and needs none, since an absent ID token fails verification (AF-25a, 401) and an empty
        // scope identifier matches no scope (AF-25b, 403).
        services.AddAuditedCommandHandler<GoogleSignInCommand, GoogleSignInCommandOutput, GoogleSignInCommandHandler>();
        // No validator for UC-26 either, and for UC-15's reason: the sign-out request carries no
        // caller-supplied input — the Google User comes from the bearer token.
        services.AddAuditedCommandHandler<GoogleSignOutCommand, GoogleSignOutCommandOutput, GoogleSignOutCommandHandler>();
        // UC-28 needs no validator either: both fields are typed route parameters, so there is no
        // caller-supplied input NFR-10 could reject that the route would not have refused first.
        services.AddAuditedCommandHandler<DeleteGoogleUserCommand, DeleteGoogleUserCommandOutput, DeleteGoogleUserCommandHandler>();
        services.AddAuditedCommandHandler<HardDeleteGoogleUserCommand, HardDeleteGoogleUserCommandOutput, HardDeleteGoogleUserCommandHandler>();
        services.AddScoped<IValidator<CreateApplicationCommand>, CreateApplicationCommandValidator>();
        services.AddAuditedCommandHandler<CreateApplicationCommand, CreateApplicationCommandOutput, CreateApplicationCommandHandler>();
        services.AddScoped<IValidator<UpdateApplicationCommand>, UpdateApplicationCommandValidator>();
        services.AddAuditedCommandHandler<UpdateApplicationCommand, UpdateApplicationCommandOutput, UpdateApplicationCommandHandler>();
        services.AddAuditedCommandHandler<DeleteApplicationCommand, DeleteApplicationCommandOutput, DeleteApplicationCommandHandler>();
        services.AddAuditedCommandHandler<HardDeleteApplicationCommand, HardDeleteApplicationCommandOutput, HardDeleteApplicationCommandHandler>();
        services.AddScoped<IValidator<IssueApplicationCredentialCommand>, IssueApplicationCredentialCommandValidator>();
        services.AddAuditedCommandHandler<IssueApplicationCredentialCommand, IssueApplicationCredentialCommandOutput, IssueApplicationCredentialCommandHandler>();
        services.AddAuditedCommandHandler<RevokeApplicationCredentialCommand, RevokeApplicationCredentialCommandOutput, RevokeApplicationCredentialCommandHandler>();
        services.AddScoped<IValidator<IssueApplicationTokenCommand>, IssueApplicationTokenCommandValidator>();
        services.AddAuditedCommandHandler<IssueApplicationTokenCommand, IssueApplicationTokenCommandOutput, IssueApplicationTokenCommandHandler>();
        services.AddScoped<IValidator<CreateScopePermissionCommand>, CreateScopePermissionCommandValidator>();
        services.AddAuditedCommandHandler<CreateScopePermissionCommand, CreateScopePermissionCommandOutput, CreateScopePermissionCommandHandler>();
        services.AddScoped<IValidator<UpdateScopePermissionCommand>, UpdateScopePermissionCommandValidator>();
        services.AddAuditedCommandHandler<UpdateScopePermissionCommand, UpdateScopePermissionCommandOutput, UpdateScopePermissionCommandHandler>();
        services.AddAuditedCommandHandler<DeleteScopePermissionCommand, DeleteScopePermissionCommandOutput, DeleteScopePermissionCommandHandler>();
        services.AddAuditedCommandHandler<HardDeleteScopePermissionCommand, HardDeleteScopePermissionCommandOutput, HardDeleteScopePermissionCommandHandler>();

        return services;
    }

    /// <summary>
    ///     Registers the TOTP secret protector and verifier, the two-factor services and the audited command
    ///     handlers of the two-factor use cases (UC-36 to UC-40, UC-46).
    /// </summary>
    public static IServiceCollection AddTwoFactorServices(this IServiceCollection services)
    {
        services.AddScoped<ITotpSecretProtector, TotpSecretProtector>();
        // Shared by UC-37's confirmation and ITwoFactorFactorVerifier (UC-38/39/40): the TOTP secret,
        // the clock-drift window, and the single-use rule that keeps an app code from being replayed
        // live in one place rather than a copy per call site.
        services.AddScoped<ITotpCodeVerifier, TotpCodeVerifier>();
        services.AddScoped<IValidator<EnableTwoFactorAuthCommand>, EnableTwoFactorAuthCommandValidator>();
        services.AddAuditedCommandHandler<EnableTwoFactorAuthCommand, EnableTwoFactorAuthCommandOutput, EnableTwoFactorAuthCommandHandler>();

        // UC-37 (FR-2F-04/05): no validator — which code(s) are required depends on the pending row's
        // AppEnabled/EmailEnabled, a database read the handler alone can make.
        services.AddAuditedCommandHandler<ConfirmTwoFactorAuthCommand, ConfirmTwoFactorAuthCommandOutput, ConfirmTwoFactorAuthCommandHandler>();

        // UC-38 (FR-2F-06…FR-2F-09): no validator either — same reason as UC-37, plus the challenge
        // token itself is validated inside the handler, the same "opaque token as a body value"
        // shape UC-13's ResetPasswordCommand uses.
        services.AddAuditedCommandHandler<VerifyTwoFactorAuthCommand, VerifyTwoFactorAuthCommandOutput, VerifyTwoFactorAuthCommandHandler>();

        // UC-46 (FR-2F-16): no validator — the challenge token is the whole of the input and is
        // validated inside the handler, the same shape UC-38 uses.
        services.AddAuditedCommandHandler<ResendTwoFactorChallengeCodeCommand, ResendTwoFactorChallengeCodeCommandOutput, ResendTwoFactorChallengeCodeCommandHandler>();

        // Shared by UC-11's AF-11g, UC-36's Email method and UC-46's reissue: retire-then-issue is
        // one step in one place, so the three cannot drift and leave two live codes for one
        // configuration — the retirement is what makes a code single-use in practice.
        services.AddScoped<ITwoFactorEmailCodeIssuer, TwoFactorEmailCodeIssuer>();

        // Shared by VerifyTwoFactorAuthCommandHandler (UC-38), DisableTwoFactorAuthCommandHandler
        // (UC-39), and RegenerateRecoveryCodesCommandHandler (UC-40) — the "code against TOTP, or
        // against the current email code, or against an unused recovery code" comparison lives in
        // exactly one place.
        services.AddScoped<ITwoFactorFactorVerifier, TwoFactorFactorVerifier>();

        // UC-39 (FR-2F-11): no validator — which shape of second factor (code vs. recoveryCode) is
        // valid depends on a database read (the pending row's AppEnabled/EmailEnabled), the same
        // reason UC-37/UC-38 have none.
        services.AddAuditedCommandHandler<DisableTwoFactorAuthCommand, DisableTwoFactorAuthCommandOutput, DisableTwoFactorAuthCommandHandler>();

        // UC-40 (FR-2F-12): no validator either, same reason as UC-39 — reuses ITwoFactorFactorVerifier
        // (registered above) rather than reimplementing the second-factor check.
        services.AddAuditedCommandHandler<RegenerateRecoveryCodesCommand, RegenerateRecoveryCodesCommandOutput, RegenerateRecoveryCodesCommandHandler>();

        return services;
    }
}
