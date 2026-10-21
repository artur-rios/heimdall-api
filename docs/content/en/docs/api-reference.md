+++
title = 'API reference'
linkTitle = 'API reference'
weight = 60
description = 'Every endpoint, the role that may call it, and the use case it implements.'
+++

Schema-level documentation — every parameter, every request and response body — is published as an
OpenAPI document and rendered by the [API explorer](../api-explorer/), which is the same document a
running instance serves through its own Swagger UI at `/swagger`. To *call* the endpoints, use that,
or the [`.http` files or Bruno collection](../getting-started/#calling-the-api) in `api-client/`.

This page is the map — what exists, who may call it, and which use case it implements.

## Reading the "Who" column

| Notation | Meaning |
| --- | --- |
| **Anonymous** | `[AllowAnonymous]` — no bearer token required. |
| **System Admin** | `[RoleRequirement(SystemAdmin)]`. |
| **+ Scope Admin** | The attribute admits Scope Admins too; the handler then confirms they own the addressed scope. |
| **Any authenticated** | No role attribute *on purpose* — the actor set the use case grants includes a `User`, so the attribute would have to admit them and the handler settles it. |

Two rules apply everywhere and are not repeated per row:

- The identifier in a path is always a **`PublicId`** (GUID), never an internal id.
- A **challenge token** (issued by a 2FA-gated login) is rejected by every endpoint below except
  `POST /api/auth/2fa/verify`, with a 401.

## Authentication — `/api/auth`

| Method & path | Who | Use case |
| --- | --- | --- |
| `POST /login` | Anonymous | UC-11 — password login; answers a token, or a challenge token if 2FA is active |
| `POST /password-recovery` | Anonymous | UC-12 — request a reset link |
| `POST /password-reset` | Anonymous | UC-13 — set a new password from the token |
| `POST /verify-email` | Anonymous | UC-14 — confirm an address from the mailed token |
| `POST /resend-verification` | Authenticated (self) | UC-15 — retire outstanding links, mail a fresh one; `429` when the account-email budget refuses it (cooldown or daily limit); rate-limited |
| `POST /google` | Anonymous | UC-25 — Google sign-up/sign-in against a scope; each ID token is accepted once (FR-GO-21), and a used one answers `401` as an invalid one does. A verified token is spent even when the call then fails (`403`, `409`, `5xx`), so get a fresh one from Google for every attempt, retries included |
| `POST /google/sign-out` | Authenticated (Google User) | UC-26 — end a Google session |
| `POST /2fa/enable` | Authenticated (self) | UC-36 — begin 2FA setup; needs `currentPassword` (`401` otherwise); with `Email`, `429` when the account-email budget refuses it |
| `POST /2fa/confirm` | Authenticated (self) | UC-37 — confirm setup, receive recovery codes |
| `POST /2fa/verify` | Anonymous | UC-38 — redeem the challenge token with a second factor |
| `POST /2fa/disable` | Authenticated (self) | UC-39 — turn 2FA off |
| `POST /2fa/recovery-codes/regenerate` | Authenticated (self) | UC-40 — issue a fresh set of ten |
| `GET /2fa` | Any authenticated | FR-2F-15 — the caller's own two-factor status; 403 for a Google User |
| `POST /data-export` | Any authenticated (self) | UC-41 — a copy of everything held about you, with recipients and retention |
| `POST /erasure-request` | Any authenticated (self) | UC-42 — ask for your own erasure; requires a password or a fresh Google ID token, not one used before (FR-GO-21) — a verified token is spent even when the request then fails, so each attempt needs a new one — and lifts your own restriction if you have one (`restrictionLifted`) |
| `POST /processing-restriction` | Any authenticated (self) | UC-44 — suspend processing without deleting, on an Art. 18(1) ground |
| `POST /processing-restriction/lift` | Self, or System Admin for another | UC-45 — lift it; an admin must inform the subject first |
| `GET /erasure-requests` | System Admin | UC-43 — the outstanding erasure requests, soonest deadline first |

Every **anonymous** endpoint here is rate-limited to **10 requests per minute per IP**. Each of them
checks a credential, and none is protected by a token — see
[Operations](../operations/#rate-limiting) for why that matters and what the limit does not replace.
The authenticated endpoints that check a password or a second factor — `2fa/enable`, `2fa/confirm`,
`2fa/disable`, `2fa/recovery-codes/regenerate`, `erasure-request` and `PUT /persons/{id}` — share the
same limit.

The 2FA endpoints never name a person: the subject is always the caller, read from the bearer token,
so a caller can only ever act on their own configuration.

A token naming a restricted identity (UC-44), or one logically deleted and not yet anonymised, is
refused everywhere with a 401 — except `data-export`, `erasure-request`, `processing-restriction` and
`processing-restriction/lift`, through which the subject exercises their own rights (NFR-24). Those
answer with the use case's own flows instead: a restricted subject can export, request erasure —
which lifts the restriction, so the erasure is not held behind it — lift their own restriction, and is
told a second restriction is a conflict (AF-44a).

A restricted identity can still sign in — `login`, `2fa/verify` and `google` answer `200` with
`processingRestricted: true` and a **subject-rights-only token** (UC-11 AF-11i, UC-25 AF-25e). That
token claims no scope and only those four endpoints accept it; every other endpoint answers `403`,
even after the restriction is lifted, when the subject signs in again for a full token.

## Scopes — `/api/scopes`

| Method & path | Who | Use case |
| --- | --- | --- |
| `POST /` | System Admin | UC-01 — create a scope with one or more initial owners |
| `GET /` | System Admin; Scope Admin (the scopes they own) | UC-02 — list scopes (paginated, filterable); a Scope Admin's page and total count their own scopes only |
| `GET /{id}` | Any authenticated | UC-02 — read one scope |
| `PUT /{id}` | System Admin | UC-03 — update name and description |
| `DELETE /{id}` | System Admin | UC-04 — logical delete (cascades to Users, Google Users, applications, all or nothing; repeating it finishes a cascade left unfinished) |
| `DELETE /{id}/hard` | System Admin | UC-05 — hard delete (also purges permissions and join rows), all or nothing |
| `PUT /{id}/google-signin` | System Admin + Scope Admin | UC-24 — turn Google Sign-In on or off |
| `GET /{id}/pending-registrations` | System Admin + Scope Admin | UC-49 — the self-registrations awaiting confirmation, newest first: name, address, expiry, guesses left; never the hash, salt or token |
| `DELETE /{id}/pending-registrations/{registrationId}` | System Admin + Scope Admin | UC-50 — discard one; its link then answers "Invalid token" and the registrant may sign up again |

## Persons — `/api`

| Method & path | Who | Use case |
| --- | --- | --- |
| `POST /persons` | System Admin | UC-06 path b — create a `ScopeAdmin` or `SystemAdmin` with no scope |
| `POST /scopes/{scopeId}/persons` | System Admin + Scope Admin | UC-06 path a — create a `User` in a scope |
| `POST /scopes/{scopeId}/owners` | System Admin + Scope Admin | UC-06 path c — create a new `ScopeAdmin` directly as a co-owner |
| `POST /scopes/{scopeId}/owners/{personId}` | System Admin + Scope Admin | UC-21 — a System Admin adds an existing `ScopeAdmin` or `SystemAdmin` as an owner (`201`, or `200` for an existing owner); a Scope Admin's request invites them instead (`202`, "The invitation was sent."; they own nothing until they accept). Under the account-creation rate limit, for either caller |
| `POST /scopes/{scopeId}/users/{personId}/promote` | System Admin + Scope Admin | UC-23 — promote a scope's `User` to owner; `409` "Concurrency conflict: the record was modified or removed by another process." when the person was changed, promoted or deleted after the request read them (AF-23f) — read them again before retrying |
| `GET /persons/{id}` | Any authenticated | UC-07 — read one person (a person may read themselves) |
| `GET /scopes/{scopeId}/persons` | System Admin + Scope Admin | UC-07 — list a scope's Users |
| `GET /scopes/{scopeId}/owners` | System Admin + Scope Admin | UC-07 — list a scope's owners |
| `GET /persons/scope-admins` | System Admin + Scope Admin | UC-07 read d — list who may own a scope (Scope Admins, plus System Admins for a System Admin caller) for an owner picker; optionally excludes a scope's current owners |
| `GET /persons/system-admins` | System Admin | UC-07 read e — list the System Admins: who else holds full access |
| `PUT /persons/{id}` | Any authenticated | UC-08 — update name and email; only a System Admin may change the role. Changing your **own** email needs `currentPassword`, and `twoFactorCode` when your 2FA is active (`401` otherwise); any email change revokes the person's tokens and notifies the previous address. An address or role change answers `409` "Concurrency conflict: the record was modified or removed by another process." when the person was changed or deleted after the request read them (AF-08l) — read them again before retrying |
| `DELETE /persons/{id}` | System Admin + Scope Admin | UC-09 — logical delete |
| `DELETE /persons/{id}/hard` | System Admin | UC-10 — hard delete, cascading to owned applications, tokens, 2FA and join rows, all or nothing |
| `POST /persons/{id}/verification-email` | System Admin + Scope Admin | UC-51 — mail a fresh verification link to the person's own address; a Scope Admin only for Users of scopes they own; 429 within the cooldown |
| `POST /persons/{id}/password-reset` | System Admin + Scope Admin | UC-52 — mail a password-reset link to the person's own address, with UC-12's eligibility; 429 within the cooldown |
| `DELETE /scopes/{scopeId}/owners/{personId}` | System Admin + Scope Admin | UC-22 — remove an owner (rejected if it would be the last one) |

## Scope owner invitations — `/api`

| Method & path | Who | Use case |
| --- | --- | --- |
| `GET /scopes/{scopeId}/owner-invitations` | System Admin + Scope Admin | UC-58 — the scope's open invitations, newest first: invitee, inviter, created and expiry times. A processing-restricted invitee's are withheld, and the inviter's name is absent when the inviter was removed, restricted or anonymised |
| `DELETE /scopes/{scopeId}/owner-invitations/{invitationId}` | System Admin + Scope Admin | UC-58 — withdraw an open invitation (`204`); an ended, unknown or other scope's one is `404` |
| `GET /persons/me/scope-invitations` | System Admin; Scope Admin | UC-59 — the caller's own open invitations, soonest to expire first; the inviter's name is absent when the inviter was removed, restricted or anonymised |
| `POST /scope-invitations/{invitationId}/accept` | The invitee | UC-60 — the invitation ends and the ownership starts, in one transaction |
| `POST /scope-invitations/{invitationId}/decline` | The invitee | UC-61 — decline |

## Applications — `/api/scopes/{scopeId}/applications`

| Method & path | Who | Use case |
| --- | --- | --- |
| `POST /` | System Admin + Scope Admin | UC-16 — register an application in the scope |
| `GET /{id}` | System Admin + Scope Admin | UC-17 — read one |
| `GET /` | System Admin + Scope Admin | UC-17 — list the scope's applications |
| `PUT /{id}` | System Admin + Scope Admin | UC-18 — update name and owner |
| `DELETE /{id}` | System Admin + Scope Admin | UC-19 — logical delete |
| `DELETE /{id}/hard` | System Admin | UC-20 — hard delete |

An application's owner must be a **`ScopeAdmin` or `SystemAdmin` who owns that scope** — a `User` may never own one
(**FR-AP-03**).

### Application credentials — `/api/scopes/{scopeId}/applications/{id}/credentials`

| Method & path | Who | Use case |
| --- | --- | --- |
| `POST /` | System Admin + Scope Admin | UC-53 — issue a credential; the secret is in this response only; 409 with two live credentials already |
| `GET /` | System Admin + Scope Admin | UC-54 — list the application's credentials, newest first; never a secret or hash |
| `DELETE /{credentialId}` | System Admin + Scope Admin | UC-55 — revoke a credential; tokens issued from it fail the next live check |

A Scope Admin may manage the credentials only of an application they own, in a scope they still own,
the rule `GET`/`PUT /{id}` apply (**FR-AP-12**).

### Application tokens — `/api/applications`

| Method & path | Who | Use case |
| --- | --- | --- |
| `POST /token` | Anonymous (the secret authenticates) | UC-56 — exchange a credential's secret for an application token; every refusal is one `401` |
| `GET /self` | Anonymous (validates its own bearer token) | UC-57 — check an application token live; one `401` for every refusal |

The [Application authentication](../flows/application-authentication/) guide is the page for the
service consuming these tokens.

## Scope permissions — `/api/scopes/{scopeId}/permissions`

| Method & path | Who | Use case |
| --- | --- | --- |
| `POST /` | System Admin + Scope Admin | UC-31 — create a permission |
| `GET /{id}` | System Admin + Scope Admin | UC-32 — read one |
| `GET /` | System Admin + Scope Admin | UC-32 — list the scope's permissions |
| `PUT /{id}` | System Admin + Scope Admin | UC-33 — update name, description, and the JWT-claim flag |
| `DELETE /{id}` | System Admin + Scope Admin | UC-34 — logical delete |
| `DELETE /{id}/hard` | System Admin | UC-35 — hard delete |

Owning the scope is the whole of the authorization to manage its permissions — a permission has no
owner of its own. A permission with `IncludeAsJwtClaim` set has its name folded into the JWT of
identities acting within that scope (**FR-AU-08**); see
[Scope permission claims](../flows/scope-permission-claims/).

## Google Users — `/api/scopes/{scopeId}/google-users`

| Method & path | Who | Use case |
| --- | --- | --- |
| `GET /` | System Admin + Scope Admin | UC-27 — list the scope's Google Users |
| `GET /{id}` | Any authenticated | UC-27 — read one; **admits the Google User themselves** |
| `DELETE /{id}` | System Admin + Scope Admin | UC-28 — logical delete (idempotent: repeating answers 200 with `alreadyDeleted: true`) |
| `DELETE /{id}/hard` | System Admin | UC-29 — hard delete (a second call is a 404) |

The by-id read carries no role attribute for a concrete reason: a Google User's token is `User`-role,
so any attribute strong enough to keep other Users out would lock out the actor UC-27 explicitly
grants. The listing admits none of them.

Both deletions take the scope's `PublicId` in the path and refuse a Google User reached through the
wrong scope.

## Audit trail — `/api/audit-log`

| Method & path | Who | Use case |
| --- | --- | --- |
| `GET /api/audit-log` | System Admin | UC-48 — the audit trail, newest first; filter by `from`/`to` (UTC, inclusive), `actorId`, `action`, `targetId`, `succeeded` |

An entry is shown as stored: an actor the retention pass has cleared reads as `null`, so filtering by
`actorId` cannot find it. `action` is the command's type name, matched exactly (`CreateScopeCommand`).
A Scope Admin is refused — the trail has no scope column to divide it by.

## Health check — `/api/healthcheck`

| Method & path | Who | Use case |
| --- | --- | --- |
| `GET /api/healthcheck` | Anonymous | UC-30 — liveness; the process is up (no database read) |
| `GET /api/healthcheck/detailed` | System Admin | UC-30 — per-dependency status |

The API also answers both at the root, `/healthcheck` and `/healthcheck/detailed`, for the container
health check and yggdrasil's status page, which call it on its own port. Behind Traefik, only the
`/api/` addresses reach it from the web UI's host.

## Signing keys — `/.well-known/jwks.json`

| Method & path | Who | Use case |
| --- | --- | --- |
| `GET /.well-known/jwks.json` | Anonymous | The public ES256 keys every token can be verified with |

This one sits outside `/api` and is the exception to the response envelope below: the body is a raw
JSON Web Key Set (RFC 7517), the current key first and, during a rotation, the previous one. It
never carries a private member.

```json
{"keys":[{"kty":"EC","crv":"P-256","x":"…","y":"…","kid":"…","use":"sig","alg":"ES256"}]}
```

The response is `application/json` with `Cache-Control: public, max-age=300`. It shares the
`AuthAnonymous` rate limit, 10 requests a minute per address, and answers 429 beyond it. Platform
consumers read it at `http://heimdall-api.<environment>:8080/.well-known/jwks.json`; anyone else at
`https://<PUBLIC_HOST>/.well-known/jwks.json` (the UI host does not serve it). See
[Operations](../operations/#consumers).

## Response shape

Every other endpoint answers one of two envelopes:

```jsonc
// DataOutput<T>
{
  "data": { /* the resource, or null */ },
  "messages": ["Scope created successfully"],
  "errors": [],
  "success": true
}
```

```jsonc
// PaginatedOutput<T> — every list endpoint
{
  "data": [ /* the page */ ],
  "pageNumber": 1,
  "pageSize": 20,
  "totalRecords": 137,
  "messages": [],
  "errors": [],
  "success": true
}
```

The HTTP status comes from the handler's message, mapped through the area's **message map** (a
dictionary from message text to status code), rather than from the controller choosing a status per
branch. Same message, same status, everywhere it is returned.

List endpoints accept pagination and filter parameters, all validated before the query runs
(**NFR-10**), and most accept `includeDeleted=true` to see logically deleted rows.

## Malformed requests

A request that cannot be bound — a body that is not JSON, or a value of the wrong type in the body or
query string (`?succeeded=maybe`, `{"name": 123}`) — answers `400` with the error
`The request is malformed.`, on every endpoint (**NFR-10**). The answer is a `DataOutput` envelope even
on list endpoints. Authentication and role checks come first, so a caller without a valid token still
receives `401`, and one of the wrong role `403`; refusals that depend on data, made in a handler, come
after the `400`.

A query parameter that is not nullable no longer falls back to its default when it is empty or
unparseable: `?pageNumber=`, `?pageNumber=abc`, `?pageSize=`, `?includeDeleted=` and `?includeDeleted=1`
are all `400`. A nullable one sent empty (`?succeeded=`) still binds to null and is ignored. A missing
required field is not a binding failure: it keeps the field's own message from the validator.

Of the endpoints that take a body, only `POST …/credentials` accepts an empty one, meaning "no expiry".

## Errors that are deliberately vague

Some responses tell the caller less than the API knows, on purpose:

- **`POST /api/auth/login`** answers the same "invalid credentials" error whether the email is
  unknown, the password wrong, the person logically deleted, or their scope gone (AF-11a…AF-11e). The
  checks still run in the specification's order so the code reads against UC-11 — only the *answer*
  is uniform.
- **`POST /api/auth/password-recovery`** answers identically whether or not the address belongs to
  anyone — in the same time, too: it only queues the request, and the lookup, the token and the email
  happen after the response. A Mailgun failure is logged rather than surfaced — an email outage must
  not turn into a 500 that confirms an anonymous caller's guess.
- **`POST /api/auth/google`** answers alike for an unverifiable token, a token that does not carry
  the `nonce` the request sent, and a Google User whose account is gone (AF-25a, AF-25g, AF-25d).
- **`POST /api/auth/password-reset`** and **`POST /api/auth/verify-email`** answer "Invalid token."
  both for a token nobody issued and for one whose person has since been restricted, deleted or
  anonymised, or can no longer sign in (AF-13e, AF-14d).
- **`POST /api/scope-invitations/{id}/accept`** and **`…/decline`** answer "The invitation was not found
  or is no longer open." alike for an unknown id, somebody else's invitation — the inviter's own
  included — an ended or expired one, and one voided because it could no longer be honoured (AF-60a,
  AF-60b).

## The specification

§5 of the [System Requirements Document](../requirements/system-requirements-document/) lists the
endpoints as specified, and §7 is the full **authorization matrix** — every action against every
role, including Anonymous. The [Use Case Specification Document](../requirements/use-case-specification-document/)
gives each endpoint's main flow and every numbered alternative flow.
