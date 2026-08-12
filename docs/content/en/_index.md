+++
title = 'Heimdall API'
linkTitle = 'Heimdall API'
+++

{{< blocks/cover title="Heimdall API" image_anchor="top" height="med" color="primary" >}}
<div class="mx-auto">
  <a class="btn btn-lg btn-primary me-3 mb-4" href="{{< relref "/docs" >}}">
    Documentation <i class="fas fa-arrow-alt-circle-right ms-2"></i>
  </a>
  <a class="btn btn-lg btn-secondary mb-4" href="https://github.com/artur-rios/heimdall-api">
    GitHub <i class="fab fa-github ms-2"></i>
  </a>
  <p class="lead mt-5">
    Centralized identity management for many client systems at once —<br class="d-none d-lg-inline">
    each isolated inside its own scope.
  </p>
</div>
{{< /blocks/cover >}}

{{% blocks/lead color="dark" %}}
Heimdall answers one question for every system that delegates to it — **who is this caller, and what
are they allowed to do?** — while keeping each client system's identities isolated from every other's.

Built with ASP.NET Core on .NET 10, in a layered architecture with CQRS.
{{% /blocks/lead %}}

{{< blocks/section color="light" type="row" >}}

{{% blocks/feature icon="fa-layer-group" title="Multi-tenant by scope" %}}
Every `User` belongs to exactly one scope, a `ScopeAdmin` owns one or more, and a `SystemAdmin`
governs the whole system and belongs to none.

A person has no scope column at all — the relationship is derived from their role.
{{% /blocks/feature %}}

{{% blocks/feature icon="fa-key" title="Authentication that scales out" %}}
Password login with Argon2id, optional two-factor (authenticator app, email, recovery codes), and
per-scope Google Sign-In.

Tokens are stateless signed JWTs — authentication reads no database per request.
{{% /blocks/feature %}}

{{% blocks/feature icon="fa-user-shield" title="Human and non-human identities" %}}
Persons, applications owned by a Scope Admin, and Google Users — plus scope-specific permissions
that fold into the JWT issued for that scope.

Logical and hard deletion, with defined cascade rules.
{{% /blocks/feature %}}

{{< /blocks/section >}}

{{< blocks/section color="white" type="row" >}}

{{% blocks/feature icon="fa-rocket" title="Getting started" url="/docs/getting-started/" url_text="Set it up" %}}
Prerequisites, environment configuration, migrations, and running the API locally — plus the
start-up guards that fail loudly instead of failing later.
{{% /blocks/feature %}}

{{% blocks/feature icon="fa-project-diagram" title="Architecture & flows" url="/docs/architecture/" url_text="See the diagrams" %}}
Class diagrams for the layers and the domain model, and sequence diagrams tracing login, two-factor,
Google Sign-In, onboarding and audit logging end to end.
{{% /blocks/feature %}}

{{% blocks/feature icon="fa-clipboard-check" title="The specification" url="/docs/requirements/" url_text="Read the requirements" %}}
Vision, numbered functional and non-functional requirements, every use case with its alternative
flows, the testing standard, and the development workflow.
{{% /blocks/feature %}}

{{< /blocks/section >}}

{{% blocks/section color="dark" type="row" %}}

{{% blocks/feature icon="fa-vial" title="Tested at two layers" url="/coverage-report/" url_text="Browse the coverage report" %}}
Unit tests for every handler and validator, functional tests driving the real API over HTTP against a
PostgreSQL database provisioned by Testcontainers.
{{% /blocks/feature %}}

{{% blocks/feature icon="fa-book" title="Traceable to a requirement" url="/docs/api-reference/" url_text="See the endpoints" %}}
Every endpoint cites the use case it implements and the requirements behind it, and the source code
cites them back — so a line of code leads to the flow that demanded it.
{{% /blocks/feature %}}

{{% /blocks/section %}}
