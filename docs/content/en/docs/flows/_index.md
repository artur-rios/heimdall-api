+++
title = 'Flows'
linkTitle = 'Flows'
weight = 70
description = 'Sequence diagrams for the paths that are worth tracing end to end.'
+++

Most endpoints are a straight line: validate, read, write, respond. The flows collected here are the
ones that are not — they cross services, branch on stored state, or deliberately answer the same way
down several different paths.

| Flow | Why it is worth tracing |
| --- | --- |
| [Login](login/) | Five distinct failure causes, one answer — and a sixth outcome that is not a token at all. |
| [Two-factor authentication](two-factor/) | Setup, confirmation, and the challenge-token round trip that completes a gated login. |
| [Google Sign-In](google-sign-in/) | Verification before anything else, then sign-up or sign-in down the same endpoint. |
| [Person onboarding](person-onboarding/) | Creation, the verification email, and the resend path — including what happens with no mail credentials. |
| [Password recovery](password-recovery/) | An endpoint whose whole design is about what it must *not* reveal. |
| [Scope permission claims](scope-permission-claims/) | How a permission defined in a scope ends up inside a caller's JWT. |
| [Audit logging](audit-logging/) | One decorator, every write, no handler changes. |

Each page cites the use case (`UC-…`) and alternative flows (`AF-…`) it implements. The authoritative
descriptions live in the
[Use Case Specification Document](../requirements/use-case-specification-document/).
