+++
title = 'Documentation'
linkTitle = 'Docs'
weight = 10
description = 'Everything needed to understand, run, test, and extend the Heimdall API.'
+++

Heimdall is a centralized identity management API. It answers one question for many client systems
at once — *who is this caller, and what are they allowed to do?* — while keeping each client system's
identities isolated from every other's inside its own **scope**.

## The documentation

| Section | What it covers |
| --- | --- |
| [Overview](overview/) | The domain vocabulary — scopes, persons, roles, applications, Google Users, permissions — and the rules that bind them. |
| [Getting started](getting-started/) | Prerequisites, environment configuration, migrations, and running the API locally. |
| [Testing](testing/) | The unit and functional suites, categories, `.trx` output, and the flake hunter. |
| [Architecture](architecture/) | The four layers, the CQRS mediator, the auditing decorator, and the HTTP request pipeline — with class diagrams. |
| [Domain model](domain-model/) | The entities, their relationships, and the deletion cascade rules — with a class diagram. |
| [API reference](api-reference/) | Every endpoint, the role that may call it, and the use case it implements. |
| [Flows](flows/) | Sequence diagrams for login, two-factor authentication, Google Sign-In, person onboarding, and audit logging. |
| [Operations](operations/) | Migrations, health checks, logging, rate limiting, and the environment variables the API reads. |
| [Requirements](requirements/) | The source specifications: vision, system requirements, use cases, technology stack, testing, workflow, and operations. |

## How the documents relate

The **requirements** documents are the specification — they say what the system must do, and every
feature traces back to a numbered requirement (`FR-…`, `NFR-…`) and a use case (`UC-…`). The other
sections describe what was built and how to work with it, and cite those identifiers wherever a
design decision came from one.

{{% alert title="Single source of truth" %}}
The requirements pages on this site are the very files under
[`docs/requirements/`](https://github.com/artur-rios/heimdall-api/tree/main/docs/requirements) in the
repository — rendered, not copied. Editing the Markdown updates this site.
{{% /alert %}}
