+++
title = 'Heimdall API'
linkTitle = 'Heimdall API'
+++

# Heimdall API

A centralized **identity management API** built with ASP.NET Core (.NET 10). It provides person
management, application (non-human identity) management, authentication and authorization, password
recovery, email verification, two-factor authentication, and Google Sign-In for multiple client
systems through **scope-based multi-tenancy** — each client system operates within its own isolated
scope.

<div class="mt-5 mb-5">
  <a class="btn btn-lg btn-primary me-3" href="docs/">Read the documentation</a>
  <a class="btn btn-lg btn-secondary" href="https://github.com/artur-rios/heimdall-api">View on GitHub</a>
</div>

## What it does

| | |
| --- | --- |
| **Multi-tenant by scope** | Every `User` belongs to exactly one scope; a `ScopeAdmin` owns one or more scopes; a `SystemAdmin` governs the whole system and belongs to no scope. |
| **Persons & applications** | Human identities (persons) and non-human identities (applications, each owned by a Scope Admin who owns the application's scope). |
| **Scope-specific permissions** | Each scope defines its own permissions; a permission flagged `IncludeAsJwtClaim` is folded into the JWT issued to identities acting within that scope. |
| **Authentication** | Password login (JWT), password recovery, email verification, optional Google Sign-In per scope, and optional two-factor authentication with single-use recovery codes. |
| **Deletion strategies** | Logical (soft) and hard deletion, with well-defined cascade rules. |
| **Layered (DDD) architecture** | Domain, Application (CQRS), Infrastructure (EF Core), and Presentation (Web API). |

## Where to start

- [Overview](docs/overview/) — the domain vocabulary and how the pieces fit together.
- [Getting started](docs/getting-started/) — prerequisites, configuration, and running the API.
- [Testing](docs/testing/) — the unit and functional suites, and how to chase a flaky test.
- [Architecture](docs/architecture/) — layers, CQRS, and the request pipeline, with class diagrams.
- [Flows](docs/flows/) — sequence diagrams for login, two-factor, Google Sign-In, and more.
- [Requirements](docs/requirements/) — the vision, requirements, use cases, and testing specification.
