# Application authentication for service consumers — design

**Issue:** heimdall-api #178. **Consumer that needs it:** Cerberus, cerberus-api #44 (UC-43,
Retrieve software secrets), which must authenticate a registered *application*, not a person,
before returning ciphertext it was explicitly granted.
**Out of scope:** Cerberus's own changes (its issue), a Heimdall UI screen for managing
credentials (a follow-up heimdall-ui issue), and any release.

## 1. Decisions taken with the owner

| Question | Decision |
| --- | --- |
| How consumers verify application tokens | Signed the way Heimdall's tokens are signed today (HMAC-SHA256 with the current/previous key pair), checked offline with the secret consumers already hold, and **re-checked live** through a new `GET /api/applications/self` called with the application token — as Cerberus already re-checks persons through `GET /api/persons/{id}`. No new key infrastructure. |
| Credentials and rotation | A random secret shown once, stored only as a hash. An application may hold **at most two live secrets**, so a new one can be deployed before the old is revoked. Each can be revoked on its own and may carry an expiry. Issued by the owning Scope Admin or a System Admin. |
| Token lifetime | **10 minutes**, configurable. |

## 2. Why a separate audience

Cerberus's existing validator accepts any correctly signed Heimdall token whose `scopeId` names its
scope, then re-checks persons online. An application token signed with the same key, issuer and
audience would pass that offline check as if it were a person. Application tokens therefore carry
a **distinct audience** (`HEIMDALL_APPLICATION_TOKEN_AUDIENCE`, required, and required to differ
from `HEIMDALL_AUTH_TOKEN_AUDIENCE`; startup refuses otherwise).

That choice protects consumers, and Heimdall itself refuses application tokens through their claims:

- **Application tokens are refused everywhere else.** A consumer that pins the person audience
  refuses one outright; Cerberus's `HeimdallTokenValidator` does (`ValidateAudience = true`), and the
  consumer guide tells every consumer to. Heimdall's own `AuthenticationMiddleware` does **not**
  check the audience: Util.WebApi validates through `ArturRios.Jwt`'s `IsTokenValidAsync`, which
  checks the signature and lifetime only. So inside Heimdall the refusal comes from the claims. An
  application token carries no `roleId`, which `IdentityUserMapper.FromClaims` already requires, and
  the mapper refuses any token carrying a `tokenKind` claim. That second check means a later change
  cannot turn an application token into a person by adding a role to it. Either way the result is
  a `401` on every existing endpoint.
- **Person tokens cannot pass as an application.** The only endpoint that accepts application
  tokens validates them itself, against the issuer, the application audience and a `tokenKind` claim
  of `application`; a person, Google User, 2FA challenge or subject-rights-only token fails all
  three.

> **Amended 2026-10-14, while writing the plan.** The first version said the middleware validates
> every route against the person audience. Reading `ArturRios.Jwt` showed it does not, so this
> section now names the claim checks that give the same isolation. §3 was also tightened: the
> status code for an issued credential, the per-actor rule, and the race on the two-credential
> limit. The decisions in §1 are unchanged.

## 3. Credentials (UC-53, UC-54, UC-55)

**Entity.** New table `application_credential`:

| Column | Notes |
| --- | --- |
| `Id` | bigint, internal |
| `PublicId` | uuid, unique — the credential's reference everywhere |
| `ApplicationId` | FK to `application`, cascade on hard delete |
| `SecretHash` | SHA-256 hex of the secret, unique |
| `CreatedAt` | |
| `ExpiresAt` | nullable; `null` means no expiry |
| `RevokedAt` | nullable |
| `LastUsedAt` | nullable; stamped when a token is issued from it |

A credential is **live** when `RevokedAt is null` and (`ExpiresAt is null` or `ExpiresAt > now`).
No personal data: the Data Protection Document records the table as holding none, and the Data
Retention Schedule gains a row (revoked or expired credentials are purged after the same grace the
single-use tokens use, by the existing token retention pass).

**Secret.** 32 bytes from a cryptographic RNG, base64url-encoded, prefixed `hds_` (so a leaked one
is recognisable in logs and by secret scanners). High entropy makes a fast hash sufficient, and
keeps the token endpoint off the Argon2id gate that password logins share (NFR-18); comparison is
by looking up the hash, so no timing difference depends on how much of a secret matches.

**Endpoints** under `api/scopes/{scopeId}/applications/{id}/credentials`, in a new
`ApplicationCredentialController`, `[RoleRequirement(SystemAdmin, ScopeAdmin)]`, with the same
per-actor rule as `GET`/`PUT`/`DELETE /api/scopes/{scopeId}/applications/{id}`. A System Admin may
manage any application; a Scope Admin only one they own **and** whose scope they still own (UC-17
AF-17b). Owning the scope alone is not enough, as it is not for reading or renaming the application:

- **UC-53 Issue** — `POST`, body `{ "expiresAt": "…" }` (optional; must be in the future; a value
  without an offset is read as UTC). Answers `201 Created`, as every create in this API does, with
  data `{ id, secret, createdAt, expiresAt }`. This is the only time `secret` ever appears. The
  two-credential limit is checked and the row inserted under a lock on the application's row, so
  concurrent requests cannot leave three live credentials. Refused `409` when two live credentials
  already exist; `404` when the scope or application is missing, logically deleted, or the
  application is not in that scope; `403` for a Scope Admin who does not own the application; `400` for an expiry in the
  past.
- **UC-54 List** — `GET`, every credential of the application, newest first: `{ id, createdAt,
  expiresAt, revokedAt, lastUsedAt, live }`. Never the secret or its hash.
- **UC-55 Revoke** — `DELETE …/credentials/{credentialId}`. Sets `RevokedAt`. Revoking an already
  revoked credential answers `409`; one of another application is `404`.

Issue and revoke are audited through `AddAuditedCommandHandler`, the credential's `PublicId` as the
target. Logically deleting an application or its scope leaves the rows alone — the token endpoint
and the live re-check both refuse a deleted application or scope, so its credentials are inert.

## 4. Obtaining a token (UC-56)

`POST /api/applications/token`, `[AllowAnonymous]`, `[EnableRateLimiting(AuthAnonymous)]` (so the
trusted caller key of TH-32 applies), body `{ applicationId, scopeId, secret }`.

Checks, all of which must hold: a credential with that secret's hash exists and is live; it belongs
to the application `applicationId`; the application is not logically deleted and belongs to the
scope `scopeId`; the scope is not logically deleted. **Every failure is the same `401` with one
message** ("The application credentials were not accepted."), so the endpoint reveals nothing about
which applications, scopes or credentials exist (the stance UC-11 takes for persons). A request
that fails validation (missing fields, malformed ids) is `400`.

On success it stamps `LastUsedAt` and answers `{ token, expiresAt }`. The token is a JWT:

| Claim | Value |
| --- | --- |
| `iss` | the configured issuer (as person tokens) |
| `aud` | `HEIMDALL_APPLICATION_TOKEN_AUDIENCE` |
| `exp`, `iat`, `nbf` | `exp` = now + `HEIMDALL_APPLICATION_TOKEN_EXPIRATION_IN_SECONDS` (default 600) |
| `tokenKind` | `application` |
| `id` | the application's `PublicId` |
| `scopeId` | the application's scope `PublicId` |
| `credentialId` | the issuing credential's `PublicId` |
| `name` | the application's name |

No `roleId`, `ownedScopeIds` or `scopePermissions`: an application holds no Heimdall role.
Issuance is audited (actor `null`, target the application). It is signed with the current key, so
key rotation (`HEIMDALL_AUTH_TOKEN_SECRET_PREVIOUS`) covers application tokens exactly as it covers
person tokens.

## 5. Live re-check (UC-57)

`GET /api/applications/self`, `[AllowAnonymous]` at the middleware (which would otherwise refuse
the audience) with the application token in the `Authorization: Bearer` header, validated by a
dedicated `ApplicationTokenValidator`: signature against the current and previous keys, issuer,
the application audience, lifetime with no clock skew, and `tokenKind == application`. Then, from
the database: the application exists and is not logically deleted, its scope is not logically
deleted and matches the token's `scopeId`, and the credential named by `credentialId` is still
live. All true → `200` with `{ applicationId, name, scopeId, credentialId, expiresAt }`. Anything
else → `401` with one message, no detail. So revocation, application deletion and scope deletion
take effect on the very next re-check, including for tokens already issued — that is the
documented revocation policy, and the consumer guide tells consumers to re-check before every
sensitive release.

## 6. Configuration

| Variable | Default | Notes |
| --- | --- | --- |
| `HEIMDALL_APPLICATION_TOKEN_AUDIENCE` | none — required | must differ from `HEIMDALL_AUTH_TOKEN_AUDIENCE` |
| `HEIMDALL_APPLICATION_TOKEN_EXPIRATION_IN_SECONDS` | `600` | seconds, as `HEIMDALL_AUTH_TOKEN_EXPIRATION_IN_SECONDS` |

Both are added to the Operations & Infrastructure Document and the environment templates, and the
startup check refuses a missing or equal audience.

## 7. Documentation

- Use Case Specification Document: **UC-53** Issue an Application Credential, **UC-54** List an
  Application's Credentials, **UC-55** Revoke an Application Credential, **UC-56** Obtain an
  Application Token, **UC-57** Check an Application Token; traceability rows.
- System Requirements Document: **FR-AP-10…** for credentials, tokens and the re-check; the
  endpoint table.
- Threat Model Document: **TH-33** stolen credential secret (mitigations: shown once, hashed,
  revocable, two-slot rotation, expiry, audit, rate limit); **TH-34** leaked application token
  (10-minute life, live re-check); **TH-35** token confusion between kinds (separate audience,
  `tokenKind`, self-validating endpoint).
- Data Protection Document and Data Retention Schedule as in §3.
- A consumer guide in `docs/content/en/docs/` — "Authenticating applications" — covering
  provisioning and rotation, the token request, claims, offline validation parameters (issuer,
  application audience, HS256, both keys, zero clock skew, `tokenKind`), the live re-check and the
  revocation policy, every status code; plus `api-client/http` and `api-client/bruno` requests,
  regenerated OpenAPI, CHANGELOG `[Unreleased]`.

## 8. Testing

- Unit: secret generation and hashing; issue (two-slot limit, expiry validation, authorization),
  list, revoke; token issuance for each refusal (unknown application, wrong scope, wrong secret,
  revoked, expired, deleted application, deleted scope) all yielding the same message; the claims
  of an issued token; the validator (wrong audience, wrong `tokenKind`, expired, bad signature,
  previous-key signature accepted).
- Functional: the full flow — issue a secret, exchange it, call `/applications/self`; a second
  secret and a third refused; revoke one and its already-issued token fails the re-check while the
  other's still passes; delete the application and the scope and the re-check fails; an
  application token is `401` on a person endpoint (`GET /api/persons/{id}`) and a person token is
  `401` on `/applications/self`; scope isolation (a credential of scope A cannot obtain a token
  naming scope B); the token endpoint's identical refusals compared with `DataOutput.Timestamp`
  normalised; non-owning Scope Admin `403` on the credential endpoints.
- Startup: a missing or equal application audience refuses to start.

## 9. Branch and order

One branch, `feature/uc-53-application-credentials`, carrying this spec, the plan and the
implementation, and one pull request closing #178. It is independent of the #174/#175/#177 work;
CHANGELOG and requirement documents conflict trivially and are resolved by keeping both sides.
