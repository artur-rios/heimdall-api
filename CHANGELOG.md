# Changelog

All notable changes to the Heimdall API are recorded in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- UC-53 to UC-57 (#178): a registered application can authenticate to Heimdall as itself. A System Admin, or the
  Scope Admin who owns the application and its scope, issues it up to two live credentials
  (`POST /api/scopes/{scopeId}/applications/{id}/credentials`), lists them (`GET` the same path) and revokes one
  (`DELETE …/credentials/{credentialId}`). The secret (`hds_…`) is returned once and only its SHA-256 is stored;
  a third live credential is refused with `409`. The application exchanges a secret for a ten-minute token
  (`POST /api/applications/token`, anonymous and rate-limited, every refusal the same `401`), and a consumer
  re-checks that token live with `GET /api/applications/self`, which refuses it as soon as its credential is
  revoked or expired or its application or scope is deleted. Application tokens carry their own audience and no
  role, and Heimdall's own endpoints refuse them. A new migration, `AddApplicationCredential`, adds the
  `application_credential` table; the retention purge removes spent credentials. **Operators:** new variables
  `HEIMDALL_APPLICATION_TOKEN_AUDIENCE` (required) and `HEIMDALL_APPLICATION_TOKEN_EXPIRATION_IN_SECONDS`
  (default `600`); see the consumer guide, "Application authentication".
- UC-51 and UC-52: an administrator can have a verification link (`POST /api/persons/{id}/verification-email`) or
  a password-reset link (`POST /api/persons/{id}/password-reset`) mailed to a person's own address — a System
  Admin for anyone, a Scope Admin for Users of scopes they own. No token is ever returned. Deleted or restricted
  persons are refused (`409`), as are a verified address (`400`) and, for a reset, an account that could not sign
  in (`409`, UC-12's rule, now shared). At most one email of each kind per person per cooldown: a repeat answers
  `429`. The cooldown is best-effort. **Operators:** the new optional `HEIMDALL_ADMIN_EMAIL_COOLDOWN_IN_SECONDS`
  sets it (default `300`, `0` turns it off; unusable values fall back to `300`); `docker-compose.yml` passes it
  through.
- UC-49 and UC-50: a scope's owners and System Admins can list the self-registrations awaiting confirmation
  (`GET /api/scopes/{id}/pending-registrations` — name, address, expiry, guesses left, never the password hash
  or token) and discard one (`DELETE /api/scopes/{id}/pending-registrations/{registrationId}`), after which its
  link answers "Invalid token". Discards are audited. A new migration, `AddPendingRegistrationPublicId`, gives
  each pending registration a random public id, backfilling existing rows.
- `GET /api/persons/system-admins` lists the deployment's System Admins (UC-07 read e, FR-PE-13), so one can
  see who else holds full access. System Admin only; a Scope Admin or User is answered `403`. Paginated, with
  optional case-insensitive `name` and `email` filters, ordered by name, and projected to `id`, `name` and
  `email`, as `GET /api/persons/scope-admins` is. Logically deleted and restricted (NFR-24) administrators are
  not listed.
- UC-48: `GET /api/audit-log` lists the audit trail for System Admins, newest first, filterable by when an entry
  was recorded (`from`/`to`, UTC, inclusive), `actorId`, `action` (the command's type name, matched exactly),
  `targetId` and `succeeded`. An actor the retention pass has cleared reads as `null` and is never reconstructed.
  A new migration, `AddAuditLogTargetIndex`, indexes `audit_log.target_id` so filtering by target does not scan
  the table.

### Changed

- 2FA challenge tokens and subject-rights-only tokens now carry their own audience (`HEIMDALL_AUTH_TOKEN_AUDIENCE`
  plus `.2fa-challenge` and `.subject-rights`, respectively); consumers that pin the person audience reject them.
  Full authentication tokens keep `HEIMDALL_AUTH_TOKEN_AUDIENCE`, and Heimdall accepts a challenge or
  subject-rights-only token only under its own audience. Clients that pass the challenge token back to
  `POST /api/auth/2fa/verify` unread need no change. **Operators:** no new variable; start-up logs the three
  audiences, and now refuses a `HEIMDALL_APPLICATION_TOKEN_AUDIENCE` equal to either derived audience. Challenge
  and subject-rights-only tokens issued before the upgrade are refused once, so those users sign in again; full
  tokens are unaffected. During a rolling deploy, instances not yet upgraded still issue the old tokens, which
  upgraded instances refuse, and the separation holds only once every instance is upgraded. Consumers that don't
  validate the audience should start doing so, pinned to `HEIMDALL_AUTH_TOKEN_AUDIENCE` exactly; see the consumer
  guide, "Login: audiences".
- A present but unusable `HEIMDALL_AUTH_TOKEN_EXPIRATION_IN_SECONDS`,
  `HEIMDALL_APPLICATION_TOKEN_EXPIRATION_IN_SECONDS` or `HEIMDALL_ADMIN_EMAIL_COOLDOWN_IN_SECONDS` now logs a warning
  at start-up naming the variable, the rejected value and the default applied. The auth token lifetime also now
  falls back to its default (`3600`) when zero, negative or not finite, where it was used as written, and the
  lifetime settings now parse with the invariant culture rather than the host's.
- A request turned away by the `AuthAnonymous` rate limit (`429`) now carries a `Retry-After` header, in whole
  seconds, and the usual error envelope with the message "Too many requests.", instead of an empty body.
- Contributors: the `dotnet-ef` tool in `.config/dotnet-tools.json` is now pinned to the version of
  `Microsoft.EntityFrameworkCore`, and `scripts/vulnerabilities.py` fails when the two drift apart.
- **Upgrading:** `HEIMDALL_APPLICATION_TOKEN_AUDIENCE` is now **required** and must differ from
  `HEIMDALL_AUTH_TOKEN_AUDIENCE`; the API refuses to start otherwise. `docker-compose.yml` defaults it to
  `heimdall-applications` and the `docker/*.env.example` templates preset that value, so a Compose deployment keeps
  starting. The Web API's `Environments/.env` holds only a placeholder; any other deployment must set it.
  Application tokens are validated with zero clock skew, so keep instance clocks synchronised (NTP).
- The audit retention pass (NFR-21) now also clears an entry's `TargetId` when it names a person or Google User,
  on the same two grounds as the actor: the entry is past the attribution period, or that identity has been
  anonymised (#184). A self-service entry, or an administrator's action on a person, therefore stops naming that
  person once its attribution is cleared. Targets naming a scope, an application, a credential or a permission are
  kept. A new migration, `AllowClearingAuditTarget`, replaces the append-only `UPDATE` trigger with
  `audit_log_attribution_clear_only()`, which permits exactly this one more change — a person target set to
  `NULL` once due, never to another value — and also makes the row `id` immutable. `GET /api/audit-log` reads a
  cleared target as `targetId: null`, and filtering by `targetId` no longer finds it. **Operators:** no setting
  changes. Existing entries already past the period are cleaned over the next scheduled runs, a batch
  (`HEIMDALL_RETENTION_PURGE_BATCH_SIZE`) at a time, and the run's log line now reports both counts.
- The audit retention pass (NFR-21) now also clears the target of a person or Google User who was hard-deleted
  (UC-10, UC-29, and the members UC-05 removes), including the hard delete's own entry, on its next run and at any
  age (#195). Before, a removed person's id stayed in the trail for good, because with the row gone the trigger could
  not tell it had named a person. Each hard delete now first writes a tombstone — the id, whether it was a person or
  a Google User, and when — to a new `erased_subject` table (migration `AddErasedSubject`); a retried delete writes
  it again without error. A trigger on that table accepts a row only for a person or Google User that still exists,
  so a tombstone cannot be planted to make any other target clearable. A target whose id is tombstoned and no longer
  resolves to a row is due, and a new migration, `AllowClearingErasedSubjectTarget`, lets the
  `audit_log_attribution_clear_only()` trigger accept exactly that clearing; nothing else it permits changes. The
  pass removes a tombstone once it is a day old and no entry names it, and its output and log line now report how
  many it removed. **Operators:** no setting changes. **Targets orphaned before this change are not recoverable:**
  the ids of persons and Google Users hard-deleted earlier were never tombstoned, so nothing records that they named
  a person, and they stay in the trail as recorded. Clearing them would need a list of those ids from outside the
  database; this release does not attempt it.
- `PUT /api/persons/{id}` and `POST /api/auth/2fa/enable` are now under the `AuthAnonymous` rate limit (10
  requests a minute per client address, `429` with `Retry-After` beyond it), like the other endpoints that check a
  password. The person update is limited whether or not it checks one, administrators' updates included.
  **Operators:** no setting changes; a deployment behind a proxy needs `HEIMDALL_TRUSTED_PROXIES` set, as for
  login, or every caller shares one budget.

### Fixed

- A sign-in attempt cut short at the lockout threshold now locks the account for the usual period instead of
  leaving it unable to sign in. An account already left at the limit without a lock is handled normally again by
  its next sign-in attempt, which locks it for the usual period, or clears it if the password is right. A new data
  migration, `ReleaseStuckLoginLockouts`, also resets such accounts.
- The Command tests no longer fail intermittently on password-hash gate timeouts under load (#196): the test
  assembly installs a gate with the production permit count and a two-minute wait, and the saturation
  exception's message now names the gate and how long it waited. Production behaviour and responses are
  unchanged.
- A request that cannot be bound now answers `400` with "The request is malformed." on every endpoint (#186),
  instead of running the action with a missing or default argument: a body that is not JSON was a `500`, and a
  query filter of the wrong type (`?succeeded=maybe`, `?actorId=not-a-guid`) was dropped and answered `200`
  unfiltered. A global `MalformedRequestFilter` replaces the two per-action guards added in #178; it runs after
  authentication and role checks, so an unauthenticated caller still receives `401`. A non-nullable query
  parameter sent empty or unparseable (`?pageNumber=`, `?pageNumber=abc`, `?includeDeleted=1`) is now a `400`
  rather than its default; a missing required field keeps its validator message.

### Security

- A `public_id` can no longer be rewritten, or borrowed from another entity (#199). The audit trail's retention rules
  (#184, #195) read a person's or Google User's id as proof that an audit target names a person, so a row given a
  scope's, an application's or a credential's id, then anonymised or tombstoned, made targets that never named a
  person clearable. Two new migrations add triggers to `person`, `google_user`, `scope`, `application`,
  `application_credential`, `scope_permission`, `role` and `pending_registration`. `ProtectIdentityPublicIds` creates
  `public_id_is_immutable()`, which refuses any `UPDATE` that changes `public_id`, and `public_id_is_unused()`, which
  refuses an `INSERT` whose `public_id` a row of any other of those tables holds, soft-deleted or not, or a hard
  delete tombstoned, and, for a person or Google User, one that an audit entry names, so a hard-deleted entity's id
  cannot be taken over while the trail still names it; it binds both to the identity tables. `ProtectEntityPublicIds`
  binds both to the other six, so `public_id` is immutable on every table the check trusts, which closes the
  rename-then-reuse path, and the check runs whichever table is written second. Every other column stays writable,
  and the API only ever creates fresh ids and never changes one, so no request behaves differently. **Operators:** no
  setting changes. The triggers guard future writes only; the migrations do not look for a duplicate already stored,
  which could exist only if somebody wrote one by hand in SQL. Operations, "Shared public ids", gives two queries to
  run before migrating; each should return no rows. Once migrated, hand-written SQL that rewrites a `public_id`, such
  as a support fix-up or a restore that remaps ids, fails with `restrict_violation`.
- Person tokens are now revoked when a credential changes. Every person token (full, 2FA challenge and
  subject-rights-only) carries the person's security stamp in a new `sst` claim, and a token whose stamp is missing
  or out of date is refused on every endpoint with `401` and "This token has been revoked. Sign in again." The stamp
  is replaced by a password reset, an email change (by the person or by an administrator), confirming or disabling
  two-factor authentication, and regenerating recovery codes; the token that made the change is revoked with the
  rest. `POST /api/auth/2fa/verify` refuses a challenge issued before such a change as an invalid challenge. A new
  migration, `AddPersonSecurityStamp`, adds `person.security_stamp`, filled with a fresh value per row; only a
  targeted write replaces it, so a concurrent whole-row save cannot restore an old one. Google User tokens are
  unchanged. **Operators:** tokens issued before the upgrade carry no stamp and are refused, so everyone signs in
  once more after deploying; during a rolling deploy, tokens issued by instances not yet upgraded are refused by
  upgraded ones until every instance is upgraded. The migration rewrites the `person` table (its default is
  evaluated per row) under an exclusive lock, which blocks sign-ins and every other read or write of the table
  until it finishes; on a large table, plan for that lock time. Consumers that validate person tokens offline
  do not see the stamp, so a revoked token stays valid at them until it expires. Revocation and the
  re-authentication below hold only once every instance runs this version: until then, an instance not yet
  upgraded still accepts revoked tokens and own email changes without a password.
- Changing your own email through `PUT /api/persons/{id}` now requires `currentPassword`, and `twoFactorCode` (an
  authenticator-app or email code, or a recovery code, which is spent) when your two-factor authentication is
  active. No endpoint sends an email code to a signed-in caller, so a person whose only method is Email uses a
  recovery code. A missing or wrong password answers `401` with "The current password is incorrect.", and a missing or
  wrong second factor `401` with "The code or recovery code is missing, incorrect, or already used.", before the
  new address is checked, and nothing changes. An administrator changing someone else's email, and anyone changing
  only their name, send neither. The email counts as changed unless it matches exactly after trimming, so a change
  of case alone also asks for the password, revokes tokens, sends the notice and resets `emailVerified`. Starting
  two-factor setup (`POST /api/auth/2fa/enable`) now requires `currentPassword` too, refused the same way. Any
  email change also sends a notice to the previous address, which names neither the new address nor a link; a
  failed delivery is logged and does not fail the update. Addresses are now stored trimmed, on creation and on
  update; an update that differs from the stored address only by surrounding whitespace is no change.
  **Clients:** send `currentPassword` (and `twoFactorCode` when 2FA is active) for an own email change, and
  `currentPassword` to enable 2FA; after an own email change, a password reset, confirming or disabling 2FA, or
  regenerating recovery codes, sign in again. **Operators:** release the heimdall-ui version that sends
  `currentPassword` together with this API, or before it; an older UI gets `401` on 2FA enable and on an own email
  change.

## [1.4.0] - 2026-10-13

### Added

- UC-47: anonymous self-registration (`POST /api/auth/register`) into scopes that open it with `PUT /api/scopes/{id}/self-registration`. Answers new and taken addresses identically; a taken address's holder is emailed about the attempt. The account is created when the emailed link is followed (`POST /api/auth/verify-email`) with the password chosen at sign-up, with its address verified; until then nothing can sign in. Confirmation needs that password: without it the answer is `422`, with a wrong one `401`, neither consumes the registration, and the 10th wrong password discards it. A link followed again once the account exists answers "Invalid token".
- Per-scope email links (FR-SR-10). `PUT /api/scopes/{id}/email-links` sets the base links a scope's
  verification (and sign-up confirmation) and password-reset emails point at, so a client system such as
  Fortuna can send its users to its own `/verify-email` and `/password-reset` screens. They replace
  `HEIMDALL_EMAIL_VERIFICATION_URL` / `HEIMDALL_PASSWORD_RESET_URL` for that scope's `User`s only;
  administrators and a scope with none set keep the global links. Each must be an absolute `http` or
  `https` URL of at most 2048 characters. `emailVerificationUrl` and `passwordResetUrl` are returned on every
  scope response. A new migration, `AddScopeEmailLinks`, adds the two nullable columns.
- Trusted caller keys for the anonymous-auth rate limit. A client system such as Fortuna's API reaches Heimdall
  from one address for every end user, so they shared one bucket of 10 requests a minute. A request carrying a
  key from the new `HEIMDALL_TRUSTED_CALLER_KEYS` in `X-Heimdall-Caller-Key` is now counted under the address in
  `X-Heimdall-Client-Address` (an IPv6 address by its /64), in a bucket of its own that a direct connection from that address does not share. A wrong or missing key, or an unusable address, falls
  back to the connection address, as before. **Operators:** the variable is optional; each key must be at least 32
  characters or start-up is refused, and two may be listed to rotate. `docker-compose.yml` passes it through.

## [1.3.0] - 2026-10-11

### Changed

- A subject whose processing is restricted (NFR-24) can sign in again, for their own rights only (#145). Until now
  `POST /api/auth/login`, `POST /api/auth/2fa/verify` and `POST /api/auth/google` refused a restricted identity
  with the uniform `401`, so once the token held at the time of the restriction expired the subject could no longer
  lift it (UC-45), export (UC-41) or request erasure (UC-42) without a System Admin. They now answer `200` with a
  **subject-rights-only token** and a new `processingRestricted: true` field (UC-11 AF-11i, UC-25 AF-25e). That
  token claims no scope, and every endpoint other than `data-export`, `erasure-request`, `processing-restriction`
  and `processing-restriction/lift` answers it `403` — still so after the restriction is lifted, when the subject
  signs in again for a full token. It never lifts anybody else's restriction, whatever role it claims. The check
  comes after the password, lockout and second-factor checks, so every refusal still answers alike, and a
  suspended (not yet anonymised) restricted subject is admitted too. A restricted person with email two-factor
  authentication is now sent the code they sign in with, and `2fa/resend` sends it again on request.

- `ArturRios.Messaging` 1.3.0 → 1.4.0 (#147). Email no longer goes out as "Mailgun Sandbox": the new `MAILGUN_FROM`
  variable sets the sender (`Heimdall <no-reply@example.com>`, or a bare address), and without it mail is sent from
  a bare `postmaster@<MAILGUN_DOMAIN>`. **Operators:** set `MAILGUN_FROM` in each environment's env file — the
  templates in `docker/` and `docker-compose.yml` now carry it. A value that is not exactly one address makes every
  send fail, logged with the variable's name. The library also stops writing recipient addresses to the log; its
  lines carry a pseudonymous `RecipientRef` instead.

- The tests run on xUnit v3: `xunit` 2.9.3 → `xunit.v3.mtp-off` 4.0.2 and `ArturRios.Util.Test` 4.0.0 → 5.0.0,
  the release built on xUnit v3. The `mtp-off` flavour runs through VSTest, so `dotnet test`, its filters and the
  `.trx` logger work as before. Test-only; the HTTP API and the OpenAPI document are unchanged.

### Fixed

- An erasure requested while the subject's processing was restricted could miss its legal deadline: NFR-24 kept the
  restricted record out of the anonymisation pass, so the request waited, unanonymised, until somebody lifted the
  restriction. `POST /api/auth/erasure-request` (UC-42) now lifts the subject's own restriction in the same write —
  only the subject can impose one (UC-44), and the request already demands their credential — whether the request
  suspends the identity or is recorded but blocked (AF-42d), for persons and Google Users alike. It is lifted as a
  subject lifts their own (UC-45), so no Art. 18(3) notification is sent. The response carries a new boolean,
  `restrictionLifted` (additive), and the audit trail records a `LiftProcessingRestrictionCommand` entry on the
  subject beside the request's own. A restriction imposed after the request still holds the erasure until it is
  lifted. UC-42, UC-44, NFR-24 and the API reference say so; the OpenAPI document is regenerated (#146).

## [1.2.0] - 2026-10-09

### Changed

- A System Admin may own a scope (FR-SC-08). `POST /api/scopes` and `POST /api/scopes/{scopeId}/owners/{personId}`
  accept a live `SystemAdmin` as an owner as well as a `ScopeAdmin`, so a System Admin can create a scope with
  themselves as its owner when no Scope Admin exists yet — before, the request was refused with "One or more owners
  do not reference an existing, non-deleted ScopeAdmin." A System Admin who owns a scope may also own its
  applications (FR-AP-03). `GET /api/persons/scope-admins` lists System Admins too when a System Admin calls it;
  a Scope Admin still sees Scope Admins only. The owner-validation messages name both roles. NFR-12 counts a
  System Admin owner like any other: deleting, hard-deleting, or erasing the last owner of a scope is refused
  whatever their role.
- Every dependency is on its latest stable release again: `Testcontainers.PostgreSql` 4.15.0 → 4.16.0 and
  `xunit.runner.visualstudio` 4.0.0 → 4.0.1 (both test-only), and the documentation site's Docsy theme 0.16.0 →
  0.18.0, which now needs Dart Sass (1.105.1 in the docs workflow) to build its stylesheets and takes the Mermaid
  settings in `docs/hugo.toml` under `params.docsy.plugins.mermaid`. The theme submodule now points at Docsy's new
  home, `github.com/docsy/docsy`. The HTTP API and the OpenAPI document are unchanged.
- The functional tests run against PostgreSQL 18 (`postgres:18-alpine`, was `postgres:16-alpine`), the major
  version the deployed environments run.

## [1.1.1] - 2026-10-09

### Fixed

- The health check moved to `GET /api/healthcheck` and `GET /api/healthcheck/detailed`. At the root it was
  unreachable from the web UI's host, which routes only `/api/` to the API, so heimdall-ui's health screen showed
  errors in every deployed environment. The root `/healthcheck` addresses still answer, for the container health
  check and yggdrasil's status page, but are no longer in the OpenAPI document.

## [1.1.0] - 2026-10-09

### Added

- `HEIMDALL_TRUSTED_PROXIES` lists the reverse proxies (addresses and CIDR networks) whose `X-Forwarded-For` and
  `X-Forwarded-Proto` headers are believed, one hop deep, so the rate limiter and the request log see the real
  caller. Unset, nothing is trusted and start-up warns.
- Every request's client IP address is logged, and kept with the application logs for 12 months. The Privacy
  Notice (now 1.2), the Data Retention Schedule and the Data Protection Document declare it.
- A Scope Admin can list scopes: `GET /api/scopes` returns the scopes they own, its total counting only those,
  with ownership read from the database. A System Admin still lists every scope; a User is still refused (`403`).

### Changed

- The credential-checking authenticated endpoints — `POST /api/auth/2fa/confirm`, `2fa/disable`,
  `2fa/recovery-codes/regenerate` and `erasure-request` — share the 10-a-minute rate limit the anonymous ones have,
  and an IPv6 caller is now counted by its `/64` rather than per address.
- A migration adds `two_factor_auth.challenge_id`. A 2FA challenge issued before the upgrade is refused after it, so
  a login in flight at deploy time has to sign in again.
- A migration adds `two_factor_auth.challenge_attempts` (integer, default `0`; additive, no table rewrite).
- `POST /api/auth/password-recovery` answers before it looks the address up: the lookup, the token and the email
  are done by a background worker, so the reset email arrives just after the response rather than before it. A
  restart drops recovery requests still queued (the person asks again), and a full queue (1,024) drops and logs.
- A login attempt is counted in `failed_login_attempts` while it is in flight, and cleared if the password was
  right, so every login now writes that counter.
- Every dependency is on its latest stable release, including `ArturRios.Util.WebApi` 5.1.0,
  `ArturRios.Data.Relational.Core` 5.0.0, `Mediator` 2.0.0, `Swashbuckle.AspNetCore` 10.3.0 and OpenTelemetry
  1.19. The HTTP API and the OpenAPI document are unchanged.
- Four deployment environments instead of three: `local` (Docker Desktop on Windows, by hand), and `development`,
  `homologation` and `production`, which share one VPS and are deployed by yggdrasil — `develop` to development,
  `release/x.y.z` to homologation, the release pull request to production. Development and homologation run on
  demand. The Development-in-WSL setup is gone. A new `docker/homologation.env.example` runs homologation as
  `Staging`; the development and production templates now describe the VPS (`PUBLIC_HOST`, `UI_HOST`, Traefik's
  Docker range in `HEIMDALL_TRUSTED_PROXIES`, one database and login per environment) and the local one points
  CORS and the e-mail links at the local heimdall-ui on `http://localhost:8081`. The e-mail templates' reset link
  is `/password-reset`, the route heimdall-ui serves. The docs page "Deploying with Docker" is now
  "Environments and deployment"; the old address redirects to it.

### Removed

- `scripts/deploy_wsl.py`, which deployed into a WSL distro for the retired Development-in-WSL environment.

### Fixed

- Login and password recovery resolve the live person when a logically deleted one still holds the same address,
  instead of refusing the live one until the deleted one is anonymised.
- A record under a restriction of processing is no longer anonymised, nor moved toward erasure when a blocked
  request clears (NFR-24).
- Re-applying erasures after a restore also removes the person's two-factor configuration, recovery and email codes,
  and reset and verification tokens, as the scheduled anonymisation does.
- An email code that authorizes a recovery-code regeneration is spent, as one that completes a login is.
- Restricted Scope Admins are withheld from the scope-owner and scope-admin listings, as restricted Users already
  were.
- A page number large enough to overflow the page offset answers `400` instead of `500`; the message now states
  the accepted range.
- The retention settings accept the lower bound of their documented ranges (1, 30 and 1 days, 1 minute).
- A restricted subject can reach the endpoints through which they exercise their own rights — data export, erasure
  request, restriction (answered `409`, already restricted) and lifting their own restriction — instead of `401`
  everywhere (NFR-24, UC-41, UC-44, UC-45). A suspended, not yet anonymised subject can export and restrict, as
  UC-41 and UC-44 say, and a repeated erasure request answers `409` rather than `401`. Every other endpoint still
  refuses them, and a restricted System Admin cannot lift anybody else's restriction.
- Validating a 2FA challenge honours the previous signing keys, as bearer tokens do, so a key rotation no longer
  voids challenges in flight.
- The OpenAPI document describes `GET /api/auth/2fa` and `POST /api/auth/data-export` with their own summaries; the
  two-factor status summary had been attached to the export.
- The OpenAPI document's `info.license` names the project's actual licence, "ArturRios.Heimdall — Proprietary
  License", instead of MIT, which it never was. Its description no longer says a 2FA challenge token works only at
  `POST /api/auth/2fa/verify`: it is never a bearer credential, and `POST /api/auth/2fa/challenge/resend` reads it
  from the request body too.

### Security

- A 2FA challenge token can be redeemed once (FR-2F-10). It stayed valid for its whole ten minutes, so whoever held
  a spent one could trade any further factor for another full token without the password. A newer login also
  replaces the previous challenge.
- A restricted identity can no longer authenticate through Google Sign-In or by completing a 2FA challenge issued
  before the restriction (NFR-24); both used to issue a full token.
- Changing a person's email retires their outstanding verification tokens, which could otherwise mark the new,
  unproven address verified.
- A Scope Admin removed as an owner of a scope can no longer read, update or delete the applications they still own
  in it.
- Two-factor email codes are hashed and checked under the process-wide Argon2id concurrency bound
  (`PasswordHashGate`), which they bypassed.
- The Mailgun library's own log lines, which wrote the recipient's address in the clear, are suppressed (NFR-22);
  Heimdall already logs every delivery outcome under a non-reversible reference.
- Password recovery takes the same time whether or not the address is registered (AF-12a). A registered address
  used to wait for the token insert and the Mailgun round trip before answering, which told an anonymous caller
  the address existed.
- Single-use items are spent once and budgets hold under concurrent requests (NFR-27): recovery codes, email codes,
  password-reset and verification tokens, the 2FA challenge and the TOTP time step are each spent by one
  conditional `UPDATE`, and the login lockout, the email-code guess limit and the code-reissue limit are charged
  before the comparison. Parallel requests could redeem one recovery code or token several times, trade one
  challenge for several tokens, and lose lockout increments so ten simultaneous wrong passwords never locked the
  account.
- A 2FA challenge allows five guesses with an authenticator-app code or a recovery code, then is retired, so further
  guessing needs the password again (FR-2F-17). Only the per-IP rate limit bounded them before.

## [1.0.0] - 2026-09-28

First release.

### Added

- Scope-based multi-tenancy: scopes, their owners, and the System Admin, Scope Admin and User roles.
- Person and application (non-human identity) management, each with logical and hard deletion and defined cascade
  rules.
- Scope-specific permissions, optionally folded into the JWT issued within the scope.
- Password login (JWT), password recovery, email verification, and email delivery through Mailgun, with a logging
  fallback when no credentials are configured.
- Two-factor authentication by authenticator app and/or email, with single-use recovery codes.
- Google Sign-In per scope, and administration of the scope's Google Users.
- LGPD and GDPR data subject rights: self-service export, erasure and restriction of processing, with the
  administrative counterparts.
- Data protection: a retention schedule, anonymisation of deleted identities, purging of expired single-use tokens,
  audit attributions cleared once due, bounded log retention with address redaction, the lawful basis and notice
  version recorded per identity, restore reconciliation against the erasure ledger, and security signal detection.
- Audit logging of write operations, rate limiting, liveness and detailed health checks, and Prometheus metrics on a
  private port.
- Deployment with Docker Compose against a host PostgreSQL, applying pending EF Core migrations at container
  start-up.

[Unreleased]: https://github.com/artur-rios/heimdall-api/compare/v1.4.0...HEAD
[1.4.0]: https://github.com/artur-rios/heimdall-api/compare/v1.3.0...v1.4.0
[1.3.0]: https://github.com/artur-rios/heimdall-api/compare/v1.2.0...v1.3.0
[1.2.0]: https://github.com/artur-rios/heimdall-api/compare/v1.1.1...v1.2.0
[1.1.1]: https://github.com/artur-rios/heimdall-api/compare/v1.1.0...v1.1.1
[1.1.0]: https://github.com/artur-rios/heimdall-api/compare/v1.0.0...v1.1.0
[1.0.0]: https://github.com/artur-rios/heimdall-api/releases/tag/v1.0.0
