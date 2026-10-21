# Changelog

All notable changes to the Heimdall API are recorded in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- A scope now holds at most `HEIMDALL_REGISTRATION_MAX_PENDING_PER_SCOPE` (default 1000) live pending
  self-registrations, whatever their addresses (FR-SR-15, AF-47m). `POST /auth/register` had only the per-IP limit
  against a caller with many source addresses, and each request stored a row and mailed a link, so the table grew
  without bound. At the cap the request is answered exactly as one that was stored, stores and mails nothing, and
  is logged at Warning with the marker `REGISTRATION_CAP_REACHED` and the scope's public id, never the address.
  The existing bound of three live registrations per address (FR-SR-13) is unchanged. The variable is in the
  `docker/*.env.example` templates, the README and `operations.md` (#206).
- A scheduled pass that fails now logs at Error with the marker `SCHEDULED_PASS_FAILED` and the pass name, where
  it was logged at Information and no alert on Warning or above could fire; a pass that throws carries the marker
  too. Each pass also publishes `heimdall_scheduled_pass_rows_processed_total` and
  `heimdall_scheduled_pass_last_success_timestamp` (unix seconds, set only by a run that completed without
  failure), labelled by `pass`, so an operator can alert on "no successful run in N hours". Both are described in
  `operations.md` (#214).
- `GET /api/healthcheck/ready`, anonymous, answers `200` while the database responds and `503` when it does not,
  with only the bare status (`Healthy` or `Unhealthy`) in the body, so an orchestrator can tell an instance that
  has lost its database from one that is up. The container health check in `docker-compose.yml` now calls it
  (`/healthcheck/ready` at the root), so Traefik stops routing to such an instance. The answer is cached for 5
  seconds, so the probe costs the database one read per window however often it is called; `/healthcheck` stays
  the liveness probe and reads no database. `operations.md` explains which to use for what (#215).
- Every `5xx` a handler returns is now logged at Error with the route template, the status code and the trace
  id, and nothing of the query string, body or caller; a `503`, which is always a deliberate answer here, is
  logged at Warning. An unhandled exception was already logged, so it is not logged twice (#227).

### Fixed

- Login, password recovery, registration and Google sign-in no longer read the whole `person` table to find an
  address. They compare `LOWER(email)` without `is_deleted`, which neither the index on the raw column nor the
  partial unique indexes could serve, so each lookup was a sequential scan before the password hash. A migration
  builds `ix_person_lower_email` on `LOWER(email)` with `CREATE INDEX CONCURRENTLY`, so writes are not blocked,
  and drops the unused `ix_person_email`. On 150,000 rows a lookup went from a sequential scan of about 29 ms to an
  index scan of well under 1 ms (#207).
- The token purge, the identity anonymisation and the audit pseudonymisation now drain a backlog: each repeats its
  batch while the batch comes back full, up to `HEIMDALL_RETENTION_MAX_BATCHES_PER_TICK` batches (default 20) per
  table in one run, where one batch a run capped a table at 12,000 rows a day and a busy audit trail could fall
  behind for good. Each run's log line now ends with the rows still due after it, so a backlog that is not
  draining is visible. A run that fails part-way still counts the rows its earlier batches processed, and the
  anonymisation's erasure ledger still names every identity the run anonymised, from every batch, even when a
  write throws; if only counting what is still due fails afterwards, the run stays a success and its line says an
  unknown number is still due. The retry of blocked erasure requests takes its batches the same way, each after the last
  request of the one before, so requests still blocked can no longer keep newer ones from being retried. The
  variable is in the `docker/*.env.example` templates and `operations.md` (#206).
- The audit pseudonymisation pass no longer reads the whole attribution window on every run. It evaluated up to
  four correlated subqueries for every attributed entry, so a run with nothing due still examined every entry of
  the last eighteen months, on every instance. It now asks each ground separately, projecting ids: entries past
  the period through two new partial indexes on `audit_log(created_at)` (over the entries that still have an
  actor, and over those that still have a target), and entries naming an anonymised or hard-deleted identity
  from those identities. What it clears is unchanged. On a 305,000-entry trail with nothing due the selection
  went from 430 ms to about 80 ms, most of it the re-check of old entries whose target is not a person, which
  keep their target for good. The `AddAuditRetentionIndexes` migration builds the indexes with
  `CREATE INDEX CONCURRENTLY`, so audit writes are not blocked while it runs (#206).
- One blocked erasure request that cannot be retried no longer stops every anonymisation. The retry now runs
  best-effort: its failure is logged and reported with the run's errors, and the identities that are due are still
  anonymised. The retry reads blocked requests `PurgeBatchSize` at a time, oldest first, where it loaded them all
  (#210).

- A login the saturated password-hash gate sheds with `503` (and any other command that throws instead of
  answering) now leaves a failed audit entry, and the exception still propagates: the gate's refusal records
  `AuthenticationTemporarilyUnavailable`, anything else a fixed reason, never the exception's text. The
  `CREDENTIAL_VERIFICATION_SHEDDING` signal counts that reason, so it can now fire; before, nothing wrote it
  (#201).
- A first-time Google sign-up whose ID token carries no email claim, as when it was issued without the `email`
  scope, is now answered with AF-25a's `401 Unauthorized` — "Google authentication failed." — and creates nothing,
  where it used to fail with a 500 (#208). Use case 25's AF-25a now names the case.
- `PUT /api/persons/{id}` refuses an email longer than 256 characters with the length message creation uses, where
  it used to reach the database and fail on the index's row-size limit with a generic error (#223).
- The master System Admin the API seeds on an empty database now has its legal basis recorded (NFR-23): contract
  performance, the current privacy notice version and the time. A new migration records the same on System Admin
  rows still at Unrecorded (`legal_basis = 0`). The migration cannot tell the seeded master from another System
  Admin that predates NFR-23, so it records all of them; a row created since then is never at 0 (#213).
- `docs/content/en/docs/operations.md` described the rolling log files as `<yyyy/MM>/log-<date>.json`, a folder per
  month. They are `<HEIMDALL_LOG_DIRECTORY>/log-<yyyyMMdd>.json`, one per day, flat in the directory, as the
  retention section of the same page already said (#216).
- Confirming two-factor setup (`POST /api/auth/2fa/confirm`) checks the email code before the authenticator code, so
  a wrong email code no longer spends a right app code's 30-second step and the caller can retry with the same one
  (#225). Use case 37 now says so.
- A change of an email address that differs only in case (`PUT /api/persons/{id}`) keeps `EmailVerified = true`: it
  is the same mailbox, as sign-in, recovery and uniqueness already treat it. It is still a change for everything
  else — it asks for the password, revokes the person's tokens and notifies the previous address (#225). Use case
  8 now says so.
- The personal data export (`POST /api/auth/data-export`) now includes the audit entries where the caller is the
  target as well as those they made, such as an administrator's update of their account (#225). Each entry gains an
  `involvement` of `Actor` or `Target`; a `Target` entry does not name who acted.
- A single-use link or code is now spent only together with the write it unlocks. Resetting a password, verifying
  an address, confirming a pending registration and confirming two-factor setup each spent the token, code or
  registration first and wrote afterwards, so a database failure in between cost the caller their link and
  answered with an error; for a pending registration, a refusal after the claim (the scope closed, the address
  taken) lost the registration for good. Each now spends and writes in one transaction, and a failure leaves the
  link, code or registration usable. Retiring a person's other links, or an address's other pending
  registrations, still runs after the write, and a failure there is logged at Warning instead of turning a reset
  or verification that happened into an error. A reset or verification refused because the person changed
  meanwhile still spends the link (FR-PR-08). Use cases 13, 14 and 37 say so (#209).
- Hard-deleting a person (`DELETE /api/persons/{id}/hard`) or a scope (`DELETE /api/scopes/{id}/hard`) is now all
  or nothing. Each step — the tombstones, the applications, tokens, Google Users and Users, then the person or
  scope — committed on its own, so a failure at the last step left the dependents gone while the person or scope
  survived, with a tombstone for an identity that still existed. The steps now run in one transaction, and a
  failure removes nothing; the audit entry recording the failed attempt is still written. Use cases 5 and 10 and
  the Data Retention Schedule say so (#212).
- Deleting a scope (`DELETE /api/scopes/{id}`) is now all or nothing, and repeating it finishes a cascade an
  earlier attempt left unfinished. The scope was flagged deleted first and its Users, Google Users and
  applications in separate saves after, so a failure part-way left a deleted scope with live members, and a retry,
  finding the scope deleted, skipped the cascade and answered `200`: those members could not sign in, but still
  showed in lists and counts, held their addresses, and never started the anonymisation window. The flag and the
  cascade now run in one transaction, and an already-deleted scope has the cascade run again for any member not
  yet deleted (AF-04b). The cascade is one `UPDATE … WHERE NOT is_deleted` per kind of member instead of loading
  every member, password hash included; a hard delete of a scope reads only its members' ids and removes them
  2,000 to a statement, inside its one transaction: the 5,000 Users of a scope are deleted in one statement, and
  hard-deleted in three. Use cases 4 and 5 say so (#204).
- A scope owner whose processing is restricted, or who has requested erasure, no longer counts as the co-owner
  that lets another owner go (NFR-12). Neither can administer the scope, so removing an owner
  (`DELETE /api/scopes/{scopeId}/owners/{personId}`), deleting or hard-deleting a person, or suspending them for
  erasure could leave a scope whose only remaining owners could not act on it. These are now refused with `409`
  "This change would leave a scope without an owner." (AF-22b, AF-09e, AF-10b), and an erasure request waits as
  blocked (AF-42d), as when the co-owner is deleted. The rule is in one shared guard, and NFR-12 and the use cases
  name it (#211).
- Two requests that each take out one of a scope's last two owners at the same moment can no longer both succeed
  and leave the scope with no owner (NFR-12). Removing an owner, deleting or hard-deleting a person, an erasure
  request and the anonymisation pass's retry of a blocked one now make the last-owner check and their write in
  one transaction that first locks the rows of the scopes concerned (`SELECT … ORDER BY id FOR UPDATE`, in id
  order), and deleting or hard-deleting a scope locks its row too, so a second such request waits for the first
  and is then refused with `409` (AF-22b, AF-09e, AF-10b) or blocked (AF-42d). The runtime login needs no new
  grant (#202).
- The system now always keeps an active System Admin (NFR-32): one not deleted, not restricted and not waiting on
  an erasure. Deleting or hard-deleting the last one (`DELETE /api/persons/{id}`, `DELETE /api/persons/{id}/hard`)
  is refused with `409` "This change would leave the system without an active System Admin. Add another System
  Admin first." (AF-09f, AF-10d), where two administrators deleting each other at once both succeeded. The last
  one's erasure request (`POST /api/auth/erasure-request`) is recorded with its deadline but blocked, as a last
  scope owner's is (AF-42e), and the anonymisation pass releases it once another active System Admin exists. Each
  decision holds one transaction-scoped advisory lock on the System Admin set, taken after the scope row locks. A
  restriction on processing is never refused, and `PUT /api/persons/{id}` never demotes a System Admin (#203).

### Changed

- The detailed health check logs the exception behind "Database: Unhealthy" at Warning, so the cause is in the log
  and not only the status (#226).
- Promoting a Scope Admin to System Admin (`PUT /api/persons/{id}` with `roleId` 1) keeps their scope ownerships,
  as FR-SC-08 lets a System Admin be a named owner, and removes only a scope membership. The promotion used to
  delete the ownership rows, so the applications the person owned were left with an owner who owned no scope, and
  it was refused with `409` when the person was a scope's only owner (AF-08g); it is now accepted, and AF-08g is
  retired. FR-PE-10 and UC-08 step 5 say a System Admin is a member of no scope but may own scopes (#224).

## [4.1.0] - 2026-10-21

### Upgrading

- **Operators:** nothing changes until you opt in. Without `HEIMDALL_MIGRATIONS_CONNECTIONSTRING` the entrypoint
  migrates, and the API runs, as the one login it always has. To make the audit triggers hold against the API's
  credentials, separate the roles with the operations guide's "Separating the migration owner from the runtime
  login": back up, run `scripts/sql/separate-owner.sql`, set the owner's password, store the owner's connection
  string as `HEIMDALL_MIGRATIONS_CONNECTIONSTRING`, redeploy, and run `scripts/sql/check-runtime-login.sql`. Once a
  database is separated, a container that migrates without the variable fails, so it is stored before the next
  deploy. The API, the HTTP API and the OpenAPI document are unchanged.

### Added

- `scripts/sql`, the database roles of NFR-29 as psql scripts, parameterised by the variables `owner`, `runtime`
  and `database`, holding no password, and safe to run again. `new-deployment.sql` creates an owner role, a
  runtime login, the database owned by the owner, the `heimdall` schema and the grants. `separate-owner.sql` moves
  a database the API's login owns to a new owner in one transaction, with a five-second `lock_timeout`: the
  database, then `REASSIGN OWNED` for the schema, tables, sequences, trigger functions and migration history. Both
  share `runtime-grants.sql`, which revokes everything the login holds and grants again `SELECT`, `INSERT`,
  `UPDATE` and `DELETE` on the `heimdall` tables, `audit_log` without `DELETE`, sequence `USAGE` and `SELECT`, and
  `SELECT` on the migration history, with default privileges for the owner so tables later migrations add are
  granted as they are created. They refuse, changing nothing, a login that is a superuser, may create roles, is a
  member of the owner or still owns anything; a database that belongs to another role; and, for
  `separate-owner.sql`, a login that also owns another database or a tablespace, which `REASSIGN OWNED` would
  move too. `check-runtime-login.sql`, run as the runtime login, fails listing every statement it is not refused
  for want of privilege, and rolls everything back.
- `scripts/migrations.py` migrates with `HEIMDALL_MIGRATIONS_CONNECTIONSTRING` when the chosen environment file
  sets it, as the entrypoint does, and with `HEIMDALL_DATA_CONNECTIONSTRING` otherwise.

### Changed

- The deployment guide, the README and the operations guide create each database with
  `scripts/sql/new-deployment.sql` instead of `createdb --owner heimdall_svc`, and the operations guide's
  "Database roles" now has the runbook for an existing deployment. The env templates and `docker-compose.yml`
  point at the scripts.
- The functional suite sets its database up with `new-deployment.sql` and runs the API as the runtime login, so a
  privilege the grants lack fails it.

### Security

- TH-18 holds in the two-role deployment: the API's login owns nothing, and cannot disable, drop or replace the
  audit triggers or their functions, nor delete or truncate audit rows. `DatabaseRoleTests` applies both scripts
  to PostgreSQL 18, the second to a database migrated the old way, and is refused each of those statements as the
  runtime login for want of privilege. In a single-login deployment, where the API's login owns the tables, the
  guarantee still does not hold; the Threat Model rates that Medium.

## [4.0.1] - 2026-10-21

### Changed

- The operations guide's "Ending the overlap" and the deployment guide's troubleshooting table now say that a
  container started from the 4.0 compose file never sees `HEIMDALL_AUTH_TOKEN_SECRET` or `_PREVIOUS`, so it never
  logs `{Variable} is set but no longer read; remove it`. Under yggdrasil, check the store with
  `ygg vars list <scope> --keys` instead.

### Fixed

- The deployment guide's PowerShell recipe for the signing key now deletes `signing.pem` once it is encoded, so the
  private key is not left on disk. The bash recipe pipes the key straight to `base64` and never writes the file.

## [4.0.0] - 2026-10-20

### Upgrading from 3.x to 4.0

- **Operators:** remove `HEIMDALL_AUTH_TOKEN_SECRET` and `_PREVIOUS`; they are no longer read, and a warning names
  them while set. Remove them from the environment after this release. In yggdrasil, fortuna-api's
  `FORTUNA_AUTH_TOKEN_SECRET` is a `${ref:}` to Heimdall's value, so Fortuna's references are removed first.
  Nothing else changes: `HEIMDALL_AUTH_SIGNING_KEY` signs as before.
- **Consumers:** HS256 tokens are refused; verify through `/.well-known/jwks.json` (fortuna-api 4.0.0 does).
  Heimdall has issued none since 3.0.0.
- **Rolling back:** to 3.x needs nothing, since 3.x does not require the secret; without it, 3.x refuses HS256
  tokens too.

### Changed

- The operations guide's "Bounding a distributed flood at the edge" now describes the credential-route limit
  yggdrasil 0.7.4 ships: the `heimdall-api-<environment>-auth` router covers `/api/auth/` on both hosts for every
  method but `GET`, `HEAD` and `OPTIONS`, 10 a minute with a burst of 10 per source. The relay router it proposed
  for Fortuna is gone: a client system on the same host calls `http://heimdall-api.<environment>:8080/` and never
  crosses the edge, as fortuna-api 3.1.0 does. A new paragraph tells the edge's `429` (plain text, `X-Retry-In`)
  from the API's (JSON, no `X-Retry-In`). Trusted caller headers may also travel over a private network nothing
  public routes to. The HTTP API and the OpenAPI document are unchanged.

### Removed

- `HEIMDALL_AUTH_TOKEN_SECRET` and `HEIMDALL_AUTH_TOKEN_SECRET_PREVIOUS`: the compose file no longer passes them,
  and start-up no longer reads or checks them, so a value 3.x refused to start with no longer stops it. While
  either is set, start-up logs `{Variable} is set but no longer read; remove it` at Warning, naming the variable
  and never its value.
- The overlap after the switch to ES256: no reader (bearer, two-factor challenge, application) accepts an HS256
  token any more, whatever it is signed with, and the `Verified a legacy HS256 {Reader} token` line is gone. The
  start-up line `Tokens: signed ES256 …; HS256 tokens {LegacyState}` keeps its template, with `refused`.

### Security

- TH-43 closed: the shared signing secret no longer exists. Only Heimdall holds a key that can sign its tokens,
  and consumers hold public keys only.

## [3.0.0] - 2026-10-19

### Upgrading from 2.x to 3.0

- **Operators:** set `HEIMDALL_AUTH_SIGNING_KEY` before deploying, one key per environment: generate it with
  `openssl genpkey -algorithm EC -pkeyopt ec_paramgen_curve:P-256 -out signing.pem` and set the variable to
  `base64 -w0 signing.pem`. It is required: start-up refuses to run without a P-256 private key there, naming the
  variable and never its value. Keep `HEIMDALL_AUTH_TOKEN_SECRET` and `_PREVIOUS` as they are through the
  overlap: while set, the HS256 tokens issued before the upgrade stay valid. Remove them no earlier than 24 hours
  after the production deploy, and only once every consumer verifies through the key set and no legacy token has
  been verified for the longest token lifetime; 4.0.0 stops reading them. In yggdrasil, fortuna-api's
  `FORTUNA_AUTH_TOKEN_SECRET` is a `${ref:}` to Heimdall's value, so Fortuna's references are removed first, and
  its 4.0.0 release must stop reading the secret, before Heimdall's value is unset (the 4.0.0 procedure, Part E).
- **Consumers:** every token (person, subject-rights-only, two-factor challenge and application) is now signed
  ES256 and carries a `kid`, the RFC 7638 thumbprint of its key. Claims, issuer, audiences and lifetimes are
  unchanged. Verify tokens with the keys at `GET /.well-known/jwks.json` (inside yggdrasil
  `http://heimdall-api.<environment>:8080/.well-known/jwks.json`; anyone else
  `https://<PUBLIC_HOST>/.well-known/jwks.json`), accept ES256 only, and fetch the set again, at most once a
  minute, when a token names a `kid` you do not know. A consumer that verifies HS256 with the shared secret
  refuses every token issued after this upgrade.
- **alexandria-api and cerberus-api** verify HS256 only (alexandria-api#136, cerberus-api#120), so they cannot
  verify 3.0.0 tokens. Both are out of scope for this release and neither is deployed against Heimdall.
- **Deploy order:** fortuna-api 3.0.0 or later first.
- **Rolling back:** rolling Heimdall back to 2.x signs out everything 3.0.0 issued: person and subject-rights
  sessions, challenges in flight, and application tokens. At Fortuna 3.0.0, ES256 tokens keep verifying from its
  cached key set until they expire. After a Fortuna restart the key-set fetch fails and they are refused. HS256
  tokens issued after the rollback verify through Fortuna's legacy secret. Fortuna must stay at 3.0.0 or later
  while Heimdall runs 3.x. A rollback to 2.x is possible only while `HEIMDALL_AUTH_TOKEN_SECRET` is still set,
  because 2.0.0's compose requires it.

### Added

- `GET /.well-known/jwks.json`: the public ES256 keys, as a JSON Web Key Set (RFC 7517, not the usual envelope),
  anonymous, under the anonymous rate limit, with `Cache-Control: public, max-age=300`. It lists the current key
  and, during a rotation, the previous one.
- `HEIMDALL_AUTH_SIGNING_KEY` (required) and `HEIMDALL_AUTH_SIGNING_KEY_PREVIOUS` (optional): base64 of a P-256
  PKCS#8 PEM. The previous key is only published and accepted, never used to sign.

### Changed

- Tokens are now signed ES256 and carry a `kid`; claims, issuer, audiences and lifetimes are unchanged.
- `HEIMDALL_AUTH_TOKEN_SECRET` is optional. While it is set (with `_PREVIOUS`), HS256 tokens signed with it are
  still accepted, but none is issued, and each accepted one is logged without the token or the identity.
  `_PREVIOUS` without the secret stops start-up.

### Security

- Only Heimdall holds a key that can sign its tokens (Threat Model TH-43; the threat closes when 4.0.0 removes the
  shared secret). Each key verifies its own algorithm only, so a token's `alg` header never selects the kind of key.

## [2.0.0] - 2026-10-19

### Upgrading from 1.x to 2.0

- **Clients:** a Scope Admin's `POST /api/scopes/{scopeId}/owners/{personId}` answers `202` with `invited: true`
  and an invitation instead of `201` and an added owner; the person becomes an owner only when they accept. Treat
  `202` (or `invited: true`) as "invited", not "added". A System Admin's request is unchanged.
- **Deploy order:** deploy the heimdall-ui release that carries scope invitations before this one.
- **Operators:** nothing is required. `HEIMDALL_SCOPE_INVITATION_LIFETIME_IN_SECONDS` is optional (default 7 days),
  and the one migration only adds the `scope_owner_invitation` table, covered by the runtime role's schema-wide
  grants.

### Added

- Scope owner invitations. New endpoints: `GET /api/scopes/{scopeId}/owner-invitations` and `DELETE
  /api/scopes/{scopeId}/owner-invitations/{invitationId}` (`204`) for the scope's owners and System Admins, to list and
  withdraw open invitations; `GET /api/persons/me/scope-invitations`, `POST
  /api/scope-invitations/{invitationId}/accept` and `…/decline` for the invited administrator. Accepting ends the
  invitation and creates the ownership in one transaction. New messages: "The invitation was sent." (`202`),
  "The invitation was not found or is no longer open." (`404`, for an unknown, ended, expired or somebody else's
  invitation, and for one voided because its scope was deleted or its invitee is no longer an eligible
  administrator), "The invitation was accepted." and "The invitation was declined." (`200`).
- `HEIMDALL_SCOPE_INVITATION_LIFETIME_IN_SECONDS`, optional: how long an invitation can be accepted, 604800 (seven
  days) by default, 3600 to 2592000; anything else falls back to the default.
- A migration, `AddScopeOwnerInvitation`, adds the `scope_owner_invitation` table, whose `public_id` is guarded like the
  other entity tables'. Invitations are purged 90 days after they ended, or 90 days after they expired if they never
  ended. Each invitation created, withdrawn, accepted, declined or voided is audited.
- An invitation notice and an answer notice, neither with a link or token. The invitee is mailed "<inviter name>
  invited you to co-own the scope <scope name>. Sign in to Heimdall to accept or decline. The invitation expires on
  <date>."; the notice is skipped for a repeat while the invitation is open, for someone invited within
  `HEIMDALL_ADMIN_EMAIL_COOLDOWN_IN_SECONDS` and for someone whose processing is restricted. The inviter is told of
  either answer, unless they are deleted, anonymised or processing-restricted.
- Erasing a person voids the unended invitations made to those anonymised in that run, clears them as inviter, and
  audits each void; every scheduled run also catches up anonymised persons still named.

### Changed

- **Behaviour change:** a Scope Admin's `POST /api/scopes/{scopeId}/owners/{personId}` used to answer `201` and add
  the owner at once. It now answers `202` "The invitation was sent." with `invited: true`, `invitationId` and
  `invitationExpiresAt`, writes no ownership, and opens an invitation the person must accept; a person who already
  owns the scope still gets `200` "Person is already an owner of this scope.". **Clients:** a client that treats any
  `2xx` from this endpoint as "added" must change; treat `202` as "invited". A System Admin's request is unchanged:
  `201`, or `200` for an existing owner, with `invited: false`. The endpoint now shares the account-creation rate limit
  (thirty a minute per administrator and per address) for every caller, a System Admin included. **Deploy order:**
  deploy the heimdall-ui release that carries scope invitations before the heimdall-api release that carries them: an
  older UI shows the `202` as a plain success while the person does not appear among the owners. A Scope Admin who
  owns no live scope cannot sign in (AF-11e), so cannot accept an invitation; a System Admin adds such a person
  directly. **Operators:** no setting is required; the runtime role's schema-wide grants and default privileges cover
  the new table.

### Fixed

- An audit log entry that fails to save is now logged as a warning, naming its action and the database error. The
  repository reports a failed insert in its result rather than throwing, and the writer discarded that result, so
  the entry was lost without a trace. The request's answer is unchanged.

### Security

- A Scope Admin can no longer make another existing administrator a co-owner of a scope on their own: the ownership
  starts only when that person accepts, signed in as themselves. This closes the acceptance residual of Threat Model
  TH-41 (residual closed); the threat itself stays on the register.

## [1.7.0] - 2026-10-19

### Fixed

- A person's lifecycle columns are no longer written by whole-row saves (#237). The address and its verified flag,
  the role and scope, the login lockout, and the deletion, erasure-request, restriction and anonymisation columns are
  each written by a conditional write of their own, so a request that read the person before another changed them
  can no longer put the earlier values back: a profile edit no longer undoes a restriction, a deletion or a lockout
  made meanwhile. Adding and removing a scope owner now write only the ownership row, and removing one re-checks
  that a live co-owner remains in the same statement. **Clients:** only requests that race another change to the
  same person answer differently. An address or role change through `PUT /api/persons/{id}` or a promotion through
  `POST /api/scopes/{scopeId}/users/{personId}/promote` whose person changed meanwhile answers `409` with
  "Concurrency conflict: the record was modified or removed by another process."; a restriction, lift, erasure
  request, deletion or ownership change that lost such a race answers as the request that won leaves it, with the
  existing message for that state.

### Security

- A Google ID token is accepted once (FR-GO-21). After it is verified, and before anything is created or issued on its
  strength (at sign-in, before any scope or account is read; for an erasure request, once its subject has been
  compared with the Google User's), its use is recorded under the SHA-256 digest of its `jti` claim (of its signed
  part, the header and payload, when it has none) in the new `google_id_token_use` table; a token already recorded is
  refused exactly as an invalid one, and of two simultaneous presentations one is refused. The records are purged with
  the single-use tokens once the token has expired and the grace period has passed. This closes the replay of a copied
  token within its hour of validity (Threat Model TH-19). **Clients:** obtain a new ID token from Google for every
  call that takes one, a retry included: a verified token is spent even when the call then fails, for example with a
  `403` (scope unavailable, unverified address), a `409` or a `5xx`. Presenting a token a second time — to
  `POST /api/auth/google`, or as the proof of presence of `POST /api/auth/erasure-request` — now answers `401` with
  "Google authentication failed." or "The credential presented was not accepted." respectively. The optional `nonce`
  is unchanged. **Operators:** the migration adds one table; the runtime role's schema-wide grants and default
  privileges cover it, so no grant changes.

## [1.6.0] - 2026-10-16

### Security

- Second-factor failures at sign-in are now counted per account, across every challenge (FR-AU-12). App codes, email
  codes and recovery codes all count, a correct password no longer resets the count, and only a successful second
  factor does. The tenth failure locks second-factor verification for 15 minutes, twice as long at each repeat, up to
  24 hours; the repeat count resets after a successful second factor. While locked, `POST /api/auth/2fa/verify`
  refuses every code, the right one included, with its usual `401` and "The code or recovery code is missing,
  incorrect, or already used.", so a client sees nothing new.
- Re-authentication under a bearer token has a per-account budget of its own (FR-AU-13): `POST /api/auth/2fa/enable`,
  `2fa/confirm`, `2fa/disable`, `2fa/recovery-codes/regenerate`, `erasure-request` (a person's password) and an own
  email change through `PUT /api/persons/{id}`. Five failures within 15 minutes pause these checks for the account
  for 15 minutes. The budget never touches the login lockout or the second-factor budget, so the owner can still
  sign in. A wrong or missing password, a wrong, missing or used code, and a paused budget all answer `401` with one
  message, "The password or code is incorrect.". **Clients:** this replaces "The current password is incorrect." and
  "The code or recovery code is missing, incorrect, or already used." on those endpoints, and on `2fa/confirm` it
  replaces the `400` answers "The authenticator app code is missing or incorrect." and "The email code is missing,
  incorrect, expired, or already used." with the same `401`. A client that treats a `401` as the end of its session
  must recognise the new message as a refused credential instead. A Google User's erasure request still answers
  "The credential presented was not accepted.", but a person who sends no credential at all now gets the new message
  and is charged like a wrong password. A new migration, `AddFactorBudgets`, adds six columns to `person`, all
  starting empty; only targeted writes change them, and anonymisation clears them. The data export
  (`POST /api/auth/data-export`) now includes all six in `security`, beside `failedLoginAttempts` and
  `lockedOutUntil`. **Deploy order:** deploy the heimdall-ui release that recognises "The password or code is
  incorrect." and the `401` from `2fa/confirm` **before this API, or together with it**. Against an older UI, a
  mistyped password or code on any of these endpoints signs the user out. That UI release only adds the message, so
  it is safe to deploy first. Third-party clients need the same change before upgrading. **Operators:** no setting
  changes; the operations guide has SQL to release a second-factor lock or a re-authentication pause for a verified
  person.
- A person's own requests now share one account-email budget (FR-PE-16): asking for the verification link again (`POST
  /api/auth/resend-verification`), starting two-factor setup with the `Email` method (`POST /api/auth/2fa/enable`) and
  an own email change (`PUT /api/persons/{id}`, whose notice goes to the previous address). At most one such email per
  `HEIMDALL_ADMIN_EMAIL_COOLDOWN_IN_SECONDS` (five minutes by default) and five in each day-long window starting at
  the first send, reserved by one conditional write before anything is sent, so simultaneous requests cannot exceed
  it. Both refusals carry `Retry-After`, the seconds until a send would be accepted. A refusal is `429` and changes
  and sends nothing: "An email of this kind was sent to you recently. Try again in a few minutes." inside the
  cooldown, "You have reached the limit for account emails. Try again later." once the window's five are spent.
  Two-factor setup checks it before the password and charges it only after, and `App` alone is never charged; an email
  change charges it after the password and second factor, and a refusal spends no recovery or email code (an accepted
  authenticator code's time step is recorded, as for any accepted code). Administrator-initiated mail (UC-51, UC-52)
  is not charged, so a person whose account was just created by an administrator, or whose budget is spent, can still
  be helped. `resend-verification` is now also under the credential endpoints' rate limit, whose `429` is "Too many
  requests." with `Retry-After`. A new migration, `AddAccountEmailBudget`, adds three columns to `person`, starting
  empty; they are in the data export and cleared by anonymisation. **Clients:** treat both new `429`s as "wait and try
  again", not as a session end or a refused credential; the wait can be hours, and `Retry-After` gives it. An own
  email change counts toward the budget too, so asking for the new address's verification link straight after the
  change waits out the cooldown.
- Anonymous password recovery (`POST /api/auth/password-recovery`) sends at most one reset link per person per
  `HEIMDALL_ADMIN_EMAIL_COOLDOWN_IN_SECONDS`, held to the reset link's lifetime, counting links sent by an
  administrator too (FR-PR-07). The answer is unchanged.
- A person can no longer change their own email while their email sign-in method is enabled, active or pending
  (FR-PE-18): `PUT /api/persons/{id}` answers `409` "Turn off the email sign-in method before changing your email
  address." once the password and second factor are accepted. The re-authentication budget is cleared, as after any
  accepted check, and an accepted authenticator code's time step is recorded; no account-email budget is charged, no
  recovery or email code is spent, and the email does not change. **Clients:** offer to turn the method off first;
  it can be set up again at the new address. An administrator's change is unaffected.
- Creating a person with a password (`POST /api/persons`, `POST /api/scopes/{id}/persons`,
  `POST /api/scopes/{id}/owners`) is limited to 30 requests a minute per acting administrator and 30 a minute per
  address; a rejection is `429` "Too many requests." with `Retry-After`. Only administrators' access tokens count
  against the address, so requests without a token, or with a User's, a two-factor challenge or a subject-rights
  token, from an address administrators share cannot use it up.
- Self-registration bounds the mail one address can be sent (FR-SR-13, FR-SR-14): at most three live pending
  registrations per address in a scope, and at most three "someone tried to register your address" notices per
  address a day, across every scope. A request beyond either stores and sends nothing, and is answered `202` exactly
  as before. The notice count is kept under a SHA-256 digest of the address in a new table, `attempt_notice_budget`
  (migration `AddAttemptNoticeBudget`), and purged with the single-use tokens once its day has ended.
- The credential endpoints' rate limiter counts an IPv6 caller by its `/56` rather than its `/64`, so a subscriber
  handed a `/56` is one caller instead of 256. Trusted callers' named IPv6 addresses are counted the same way.
  **Operators:** the operations guide now shows how to add a rate limit for the credential routes at Traefik,
  alongside the API's own limits.
- Two-factor recovery codes now carry 80 bits (FR-2F-20): ten random bytes written as sixteen characters of
  Crockford's base32 (the digits and the uppercase letters without I, L, O and U) and returned by
  `POST /api/auth/2fa/confirm` and `POST /api/auth/2fa/recovery-codes/regenerate` as `XXXX-XXXX-XXXX-XXXX`. Each is
  stored as the SHA-256 of its sixteen characters. Wherever a recovery code is accepted (`2fa/verify`, `2fa/disable`,
  `2fa/recovery-codes/regenerate`, the `twoFactorCode` of an own email change) it is matched ignoring hyphens,
  whitespace and case, and an O, I or L in a sixteen-character code reads as 0, 1 and 1. Codes of the earlier
  `XXXX-XXXX` shape keep working, in any case and with or without the hyphen, until used or regenerated.
  **Users:** everyone who enabled two-factor authentication before this release should regenerate their recovery
  codes to get the stronger ones; the old set stops working once you do.
  **Clients:** accept and show codes of 19 characters as well as 9, and do not restrict their alphabet beyond letters,
  digits, hyphens and spaces. No migration: the column already holds a digest.
- A first Google sign-in (`POST /api/auth/google`) now requires the ID token's `email_verified` to be `true`
  (FR-GO-07). A token where it is `false` or absent creates no Google User and is answered `403` with the new
  message "Google sign-up requires a verified email address.", before the address is compared with the scope's.
  A Google User that already exists signs in as before, whatever the claim says.
- `POST /api/auth/google` accepts an optional `nonce` (FR-GO-20): the value the client sent Google when it asked for
  the ID token. When it is present and not empty, the token's `nonce` claim must equal it exactly, or the request is
  answered `401` "Google authentication failed.", as for any token that fails verification. Without it nothing is
  compared. The nonce is a client-side check that the token it presents was issued for its own sign-in request;
  whether a token is accepted rests on its signature, issuer, audience and expiry. **Clients:** generate a fresh
  random nonce for each Google sign-in, pass it to Google, and send the same value here.
- A password-reset or verification link is checked against its account again when it is followed (FR-PR-08,
  FR-EV-08): if the person has since been restricted, deleted or anonymised, or can no longer sign in through their
  scope, `POST /api/auth/password-reset` and `POST /api/auth/verify-email` answer `400` "Invalid token.", exactly
  as for an unknown token, and nothing is written. The new password and the verified flag are written only while
  the person still holds the address they were read with; the new password and a fresh security stamp are written by
  one statement, and once a person row exists its password hash and salt are written only by such targeted writes, so
  no whole-row save of an older copy can put an old password back. An email change, by the person or an
  administrator, now retires every live reset link as well as every verification link the person holds, before the
  new address is saved and again straight after (FR-PE-17); following one afterwards answers `400` "Token already
  used.". **Clients:** no new messages; "Invalid token." and "Token already
  used." keep their meaning of "this link no longer works, ask for a new one".
- A Scope Admin reading another person now sees only the scope ownerships the two share (FR-PE-19). In
  `GET /api/persons/{id}`, `GET /api/scopes/{id}/owners`, `GET /api/scopes/{id}/persons` and the response of
  `PUT /api/persons/{id}`, `ownedScopeIds` lists, for a Scope Admin reading somebody else, only the scopes the
  Scope Admin owns too; a scope's owners are each listed with that scope among them. A System Admin, and a person
  reading themselves, still see every scope. `GET /api/persons/scope-admins` is unchanged: identifier, name and
  email. **Clients:** no status or message changes; do not read another person's `ownedScopeIds` as the full list
  of what they own.
- On an instance that serves a public host, EF Core's sensitive data logging and detailed errors and the developer
  exception page are off, whatever the environment, unless `HEIMDALL_ALLOW_SENSITIVE_DIAGNOSTICS` is `true`
  (NFR-28). One decision at start-up sets all three, the exception page the host adds by itself in Development
  included, and the instance starts as usual with one warning naming the hosts. A host is public when `PUBLIC_HOST`
  or `UI_HOST`, the host of `HEIMDALL_EMAIL_VERIFICATION_URL` or `HEIMDALL_PASSWORD_RESET_URL`, or a
  `HEIMDALL_CORS_ALLOWED_ORIGINS` entry, with or without a scheme, names anything but `localhost` or a loopback
  address; `docker-compose.yml` now passes `PUBLIC_HOST`, `UI_HOST` and the new variable into the container.
  **Operators:** nothing to change; set `HEIMDALL_ALLOW_SENSITIVE_DIAGNOSTICS=true` in an environment's env file
  only where it holds made-up people and its diagnostics are wanted. Production and a local run are unaffected.
  **Clients:** a public-host instance does not answer with the developer exception page unless the override is
  `true`; a failed request gets the usual `500` "Internal server error, please try again later".
- The supported database setup runs the migrations as an owner role and the API as a role that owns nothing
  (NFR-29). The container entrypoint migrates with `HEIMDALL_MIGRATIONS_CONNECTIONSTRING` when it is set, and with
  `HEIMDALL_DATA_CONNECTIONSTRING` as before when it is not. It hands the connection string to the migration
  bundle through the bundle's environment rather than its command line, and removes the migrations string before
  the API starts, which never reads it. **Operators:** optional, and applied by you: the operations guide's
  "Database roles" has the grants, the default privileges for tables later migrations add, the steps for a new
  database and for switching an existing one over, and the checks. Deploy this release before switching. The env
  templates list the new variable; `docker-compose.yml` passes it through.
- Every token the API reads is now checked for its issuer and audience as well as its signature and lifetime
  (NFR-30). A bearer token must name `HEIMDALL_AUTH_TOKEN_ISSUER` as its issuer and exactly one of the person,
  `.2fa-challenge` and `.subject-rights` audiences; the challenge token at `POST /api/auth/2fa/verify` and
  `2fa/challenge/resend`, the challenge audience; an application token at `GET /api/applications/self`, the
  application audience. A token Heimdall did not issue for that reader is refused however it is signed, with the
  answer the endpoint already gives a token it cannot use: as a bearer token, `401` "Invalid token", as for a bad
  signature; at `2fa/verify`, `401` "The two-factor challenge is invalid or has expired. Log in again."; at
  `2fa/challenge/resend`, the usual `200` "If the challenge is still valid, a new code has been sent.", with nothing
  sent; at `GET /api/applications/self`, `401` "The application token was not accepted.". **Clients:** tokens
  Heimdall issues are unaffected, and no new message is introduced.
- Tokens are accepted under HS256 only, the algorithm they are signed with, now that the API is on
  `ArturRios.Jwt` 1.3.0, declared directly in the Web API project. A token under HS384 or HS512 is refused as a
  bearer token with `401` "Invalid token", and at the two challenge endpoints and `GET /api/applications/self` as
  above.
- A browser front end on an origin in `HEIMDALL_CORS_ALLOWED_ORIGINS` can now read `Retry-After`: the API lists it in
  `Access-Control-Expose-Headers`, so the wait a `429` gives reaches a cross-origin client too.
- Start-up checks the signing secrets (NFR-31): `HEIMDALL_AUTH_TOKEN_SECRET`, and `HEIMDALL_AUTH_TOKEN_SECRET_PREVIOUS`
  when it is set, must each be at least 32 bytes and ASCII only, or the API refuses to start with a message that
  names the variable and never the value. A previous secret that is unset, blank or `""` means no previous key.
  **Operators:** before deploying, check that every environment's secrets meet the rule; `openssl rand -base64 48`
  gives 64 bytes.

## [1.5.0] - 2026-10-15

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

[Unreleased]: https://github.com/artur-rios/heimdall-api/compare/v4.1.0...HEAD
[4.1.0]: https://github.com/artur-rios/heimdall-api/compare/v4.0.1...v4.1.0
[4.0.1]: https://github.com/artur-rios/heimdall-api/compare/v4.0.0...v4.0.1
[4.0.0]: https://github.com/artur-rios/heimdall-api/compare/v3.0.0...v4.0.0
[3.0.0]: https://github.com/artur-rios/heimdall-api/compare/v2.0.0...v3.0.0
[2.0.0]: https://github.com/artur-rios/heimdall-api/compare/v1.7.0...v2.0.0
[1.7.0]: https://github.com/artur-rios/heimdall-api/compare/v1.6.0...v1.7.0
[1.6.0]: https://github.com/artur-rios/heimdall-api/compare/v1.5.0...v1.6.0
[1.5.0]: https://github.com/artur-rios/heimdall-api/compare/v1.4.0...v1.5.0
[1.4.0]: https://github.com/artur-rios/heimdall-api/compare/v1.3.0...v1.4.0
[1.3.0]: https://github.com/artur-rios/heimdall-api/compare/v1.2.0...v1.3.0
[1.2.0]: https://github.com/artur-rios/heimdall-api/compare/v1.1.1...v1.2.0
[1.1.1]: https://github.com/artur-rios/heimdall-api/compare/v1.1.0...v1.1.1
[1.1.0]: https://github.com/artur-rios/heimdall-api/compare/v1.0.0...v1.1.0
[1.0.0]: https://github.com/artur-rios/heimdall-api/releases/tag/v1.0.0
