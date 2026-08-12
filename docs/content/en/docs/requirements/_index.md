+++
title = 'Requirements'
linkTitle = 'Requirements'
weight = 90
description = 'The specification: vision, system requirements, use cases, technology stack, testing, workflow, and operations.'
+++

These seven documents are the **specification** for Heimdall. Everything else on this site describes
what was built; these say what must be true.

They are also the working documents of the project, not a retrospective write-up: a feature is
implemented by picking a use case, following its main and alternative flows, and citing the
requirement identifiers in the code.

| Document | What it settles |
| --- | --- |
| [Vision Document](vision-document/) | Why the system exists, who it serves, and what success looks like. |
| [System Requirements Document](system-requirements-document/) | The functional (`FR-…`) and non-functional (`NFR-…`) requirements, the data model, the endpoints, the authorization matrix, and the deletion strategy. |
| [Use Case Specification Document](use-case-specification-document/) | Every use case (`UC-…`) with its main flow and numbered alternative flows (`AF-11a`, `AF-11b`, …). |
| [Technology Stack Document](technology-stack-document/) | The technologies, libraries, and **pinned versions**. The single source of truth for versions. |
| [Testing Specification Document](testing-specification-document/) | What to test for each use case, the unit and functional standards, and the required coverage. |
| [Development Workflow Document](development-workflow-document/) | How a use case goes from backlog to merged — branch, issue status, testing gate, pull request. |
| [Operations & Infrastructure Document](operations-infrastructure-document/) | The technical foundation and the health-check feature. |

## Identifier conventions

| Prefix | Meaning | Example |
| --- | --- | --- |
| `FR-XX-nn` | Functional requirement, grouped by capability | `FR-AU-04` — the claims an auth token carries |
| `NFR-nn` | Non-functional requirement | `NFR-15` — internal ids never reach a caller |
| `UC-nn` | Use case | `UC-11` — Login |
| `AF-nna` | Alternative flow within a use case | `AF-11g` — the person has active 2FA |

The capability groups are `SC` (scope), `PE` (person), `RO` (role), `AU` (authentication), `PR`
(password recovery), `EV` (email verification), `AP` (application), `GO` (Google Sign-In), `SP`
(scope permission), `2F` (two-factor), `HC` (health check).

{{% alert title="Rendered in place, not copied" color="info" %}}
The pages in this section are the very files under
[`docs/requirements/`](https://github.com/artur-rios/heimdall-api/tree/main/docs/requirements) in the
repository, mounted into this site by Hugo. There is no second copy to keep in sync — editing the
Markdown updates both GitHub and this site.
{{% /alert %}}
