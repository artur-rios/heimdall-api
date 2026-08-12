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
| `POST /resend-verification` | Authenticated (self) | UC-15 — retire outstanding links, mail a fresh one |
| `POST /google` | Anonymous | UC-25 — Google sign-up/sign-in against a scope |
| `POST /google/sign-out` | Authenticated (Google User) | UC-26 — end a Google session |
| `POST /2fa/enable` | Authenticated (self) | UC-36 — begin 2FA setup |
| `POST /2fa/confirm` | Authenticated (self) | UC-37 — confirm setup, receive recovery codes |
| `POST /2fa/verify` | Anonymous | UC-38 — redeem the challenge token with a second factor |
| `POST /2fa/disable` | Authenticated (self) | UC-39 — turn 2FA off |
| `POST /2fa/recovery-codes/regenerate` | Authenticated (self) | UC-40 — issue a fresh set of ten |

Every **anonymous** endpoint here is rate-limited to **10 requests per minute per IP**. Each of them
checks a credential, and none is protected by a token — see
[Operations](../operations/#rate-limiting) for why that matters and what the limit does not replace.

The 2FA endpoints never name a person: the subject is always the caller, read from the bearer token,
so a caller can only ever act on their own configuration.

## Scopes — `/api/scopes`

| Method & path | Who | Use case |
| --- | --- | --- |
| `POST /` | System Admin | UC-01 — create a scope with one or more initial owners |
| `GET /` | System Admin | UC-02 — list scopes (paginated, filterable) |
| `GET /{id}` | Any authenticated | UC-02 — read one scope |
| `PUT /{id}` | System Admin | UC-03 — update name and description |
| `DELETE /{id}` | System Admin | UC-04 — logical delete (cascades to Users, Google Users, applications) |
| `DELETE /{id}/hard` | System Admin | UC-05 — hard delete (also purges permissions and join rows) |
| `PUT /{id}/google-signin` | System Admin + Scope Admin | UC-24 — turn Google Sign-In on or off |

## Persons — `/api`

| Method & path | Who | Use case |
| --- | --- | --- |
| `POST /persons` | System Admin | UC-06 path b — create a `ScopeAdmin` or `SystemAdmin` with no scope |
| `POST /scopes/{scopeId}/persons` | System Admin + Scope Admin | UC-06 path a — create a `User` in a scope |
| `POST /scopes/{scopeId}/owners` | System Admin + Scope Admin | UC-06 path c — create a new `ScopeAdmin` directly as a co-owner |
| `POST /scopes/{scopeId}/owners/{personId}` | System Admin + Scope Admin | UC-21 — add an existing `ScopeAdmin` as an owner |
| `POST /scopes/{scopeId}/users/{personId}/promote` | System Admin + Scope Admin | UC-23 — promote a scope's `User` to owner |
| `GET /persons/{id}` | Any authenticated | UC-07 — read one person (a person may read themselves) |
| `GET /scopes/{scopeId}/persons` | System Admin + Scope Admin | UC-07 — list a scope's Users |
| `GET /scopes/{scopeId}/owners` | System Admin + Scope Admin | UC-07 — list a scope's owners |
| `PUT /persons/{id}` | Any authenticated | UC-08 — update name and email; only a System Admin may change the role |
| `DELETE /persons/{id}` | System Admin + Scope Admin | UC-09 — logical delete |
| `DELETE /persons/{id}/hard` | System Admin | UC-10 — hard delete, cascading to owned applications, tokens, 2FA and join rows |
| `DELETE /scopes/{scopeId}/owners/{personId}` | System Admin + Scope Admin | UC-22 — remove an owner (rejected if it would be the last one) |

## Applications — `/api/scopes/{scopeId}/applications`

| Method & path | Who | Use case |
| --- | --- | --- |
| `POST /` | System Admin + Scope Admin | UC-16 — register an application in the scope |
| `GET /{id}` | System Admin + Scope Admin | UC-17 — read one |
| `GET /` | System Admin + Scope Admin | UC-17 — list the scope's applications |
| `PUT /{id}` | System Admin + Scope Admin | UC-18 — update name and owner |
| `DELETE /{id}` | System Admin + Scope Admin | UC-19 — logical delete |
| `DELETE /{id}/hard` | System Admin | UC-20 — hard delete |

An application's owner must be a **`ScopeAdmin` who owns that scope** — a `User` may never own one
(**FR-AP-03**).

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

## Health check — `/healthcheck`

| Method & path | Who | Use case |
| --- | --- | --- |
| `GET /healthcheck` | Anonymous | UC-30 — liveness; the process is up (no database read) |
| `GET /healthcheck/detailed` | System Admin | UC-30 — per-dependency status |

## Response shape

Every endpoint answers one of two envelopes:

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

## Errors that are deliberately vague

Some responses tell the caller less than the API knows, on purpose:

- **`POST /api/auth/login`** answers the same "invalid credentials" error whether the email is
  unknown, the password wrong, the person logically deleted, or their scope gone (AF-11a…AF-11e). The
  checks still run in the specification's order so the code reads against UC-11 — only the *answer*
  is uniform.
- **`POST /api/auth/password-recovery`** answers identically whether or not the address belongs to
  anyone, and a Mailgun failure is logged rather than surfaced — an email outage must not turn into a
  500 that confirms an anonymous caller's guess.
- **`POST /api/auth/google`** answers alike for an unverifiable token and a Google User whose account
  is gone (AF-25a, AF-25d).

## The specification

§5 of the [System Requirements Document](../requirements/system-requirements-document/) lists the
endpoints as specified, and §7 is the full **authorization matrix** — every action against every
role, including Anonymous. The [Use Case Specification Document](../requirements/use-case-specification-document/)
gives each endpoint's main flow and every numbered alternative flow.
