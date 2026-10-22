using ArturRios.Heimdall.Query.Handlers;
using ArturRios.Heimdall.Query.Input;
using ArturRios.Heimdall.Query.Input.Validation;
using ArturRios.Heimdall.Query.Output;
using ArturRios.Mediator.Query;
using ArturRios.Mediator.Query.Interfaces;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;

namespace ArturRios.Heimdall.Query.Registration;

public static class QueryServiceCollectionExtensions
{
    /// <summary>
    ///     Registers the query mediator, the query handlers and the validators of the paginated list queries.
    /// </summary>
    public static IServiceCollection AddQueryHandlers(this IServiceCollection services)
    {
        // #219: the controllers pass HttpContext.RequestAborted to every query, and the mediator forwards it
        // to the handler, so an abandoned read stops its query. Commands are the opposite (see CommandMediator).
        services.AddScoped<QueryMediator>();
        services
            .AddScoped<IQueryHandlerAsync<GetScopeByIdQuery, ScopeOutput>, GetScopeByIdQueryHandler>();
        services
            .AddScoped<IPaginatedQueryHandlerAsync<ListScopesQuery, ScopeOutput>, ListScopesQueryHandler>();
        services
            .AddScoped<IQueryHandlerAsync<GetPersonByIdQuery, PersonOutput>, GetPersonByIdQueryHandler>();
        services
            .AddScoped<IPaginatedQueryHandlerAsync<ListScopePersonsQuery, PersonOutput>, ListScopePersonsQueryHandler>();
        services
            .AddScoped<IPaginatedQueryHandlerAsync<ListScopeOwnersQuery, PersonOutput>, ListScopeOwnersQueryHandler>();
        services
            .AddScoped<IPaginatedQueryHandlerAsync<ListScopeOwnerInvitationsQuery, ScopeOwnerInvitationOutput>,
                ListScopeOwnerInvitationsQueryHandler>();
        services
            .AddScoped<IPaginatedQueryHandlerAsync<ListMyScopeInvitationsQuery, MyScopeInvitationOutput>,
                ListMyScopeInvitationsQueryHandler>();
        services
            .AddScoped<IPaginatedQueryHandlerAsync<ListScopeAdminsQuery, PersonSummaryOutput>,
                ListScopeAdminsQueryHandler>();
        services
            .AddScoped<IPaginatedQueryHandlerAsync<ListPendingRegistrationsQuery, PendingRegistrationOutput>,
                ListPendingRegistrationsQueryHandler>();
        services
            .AddScoped<IPaginatedQueryHandlerAsync<ListSystemAdminsQuery, PersonSummaryOutput>,
                ListSystemAdminsQueryHandler>();
        services
            .AddScoped<IQueryHandlerAsync<GetApplicationByIdQuery, ApplicationOutput>,
                GetApplicationByIdQueryHandler>();
        services
            .AddScoped<IQueryHandlerAsync<CheckApplicationTokenQuery, ApplicationTokenCheckOutput>,
                CheckApplicationTokenQueryHandler>();
        services
            .AddScoped<IPaginatedQueryHandlerAsync<ListScopeApplicationsQuery, ApplicationOutput>,
                ListScopeApplicationsQueryHandler>();
        services
            .AddScoped<IQueryHandlerAsync<ListApplicationCredentialsQuery, ApplicationCredentialListOutput>,
                ListApplicationCredentialsQueryHandler>();
        services
            .AddScoped<IQueryHandlerAsync<GetScopePermissionByIdQuery, ScopePermissionOutput>,
                GetScopePermissionByIdQueryHandler>();
        services
            .AddScoped<IPaginatedQueryHandlerAsync<ListScopePermissionsQuery, ScopePermissionOutput>,
                ListScopePermissionsQueryHandler>();
        services
            .AddScoped<IQueryHandlerAsync<GetGoogleUserByIdQuery, GoogleUserOutput>,
                GetGoogleUserByIdQueryHandler>();
        services
            .AddScoped<IPaginatedQueryHandlerAsync<ListScopeGoogleUsersQuery, GoogleUserOutput>,
                ListScopeGoogleUsersQueryHandler>();
        services
            .AddScoped<IPaginatedQueryHandlerAsync<ListErasureRequestsQuery, ErasureRequestOutput>,
                ListErasureRequestsQueryHandler>();
        services
            .AddScoped<IQueryHandlerAsync<DetectSecuritySignalsQuery, SecuritySignalsOutput>,
                DetectSecuritySignalsQueryHandler>();
        services
            .AddScoped<IValidator<ListErasureRequestsQuery>, ListErasureRequestsQueryValidator>();

        // UC-48 (FR-AT-01/02): the audit trail, System Admins only.
        services
            .AddScoped<IPaginatedQueryHandlerAsync<ListAuditLogQuery, AuditLogEntryOutput>,
                ListAuditLogQueryHandler>();
        services.AddScoped<IValidator<ListAuditLogQuery>, ListAuditLogQueryValidator>();
        services
            .AddScoped<IQueryHandlerAsync<GetTwoFactorStatusQuery, TwoFactorStatusOutput>,
                GetTwoFactorStatusQueryHandler>();

        // NFR-10: pagination/filter validation for every paginated list query.
        services.AddScoped<IValidator<ListScopesQuery>, ListScopesQueryValidator>();
        services.AddScoped<IValidator<ListScopePersonsQuery>, ListScopePersonsQueryValidator>();
        services.AddScoped<IValidator<ListScopeOwnersQuery>, ListScopeOwnersQueryValidator>();
        services.AddScoped<IValidator<ListScopeOwnerInvitationsQuery>, ListScopeOwnerInvitationsQueryValidator>();
        services.AddScoped<IValidator<ListMyScopeInvitationsQuery>, ListMyScopeInvitationsQueryValidator>();
        services.AddScoped<IValidator<ListScopeAdminsQuery>, ListScopeAdminsQueryValidator>();
        services
            .AddScoped<IValidator<ListPendingRegistrationsQuery>, ListPendingRegistrationsQueryValidator>();
        services.AddScoped<IValidator<ListSystemAdminsQuery>, ListSystemAdminsQueryValidator>();
        services
            .AddScoped<IValidator<ListScopeApplicationsQuery>, ListScopeApplicationsQueryValidator>();
        services
            .AddScoped<IValidator<ListScopePermissionsQuery>, ListScopePermissionsQueryValidator>();
        services
            .AddScoped<IValidator<ListScopeGoogleUsersQuery>, ListScopeGoogleUsersQueryValidator>();

        return services;
    }
}
