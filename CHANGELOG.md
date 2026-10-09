# Changelog

All notable changes to the Heimdall API are recorded in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Changed

- A System Admin may own a scope (FR-SC-08). `POST /api/scopes` and `POST /api/scopes/{scopeId}/owners/{personId}`
  accept a live `SystemAdmin` as an owner as well as a `ScopeAdmin`, so a System Admin can create a scope with
  themselves as its owner when no Scope Admin exists yet — before, the request was refused with "One or more owners
  do not reference an existing, non-deleted ScopeAdmin." A System Admin who owns a scope may also own its
  applications (FR-AP-03). `GET /api/persons/scope-admins` lists System Admins too when a System Admin calls it;
  a Scope Admin still sees Scope Admins only. The owner-validation messages name both roles. NFR-12 counts a
  System Admin owner like any other: deleting, hard-deleting, or erasing the last owner of a scope is refused
  whatever their role.

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

[Unreleased]: https://github.com/artur-rios/heimdall-api/compare/v1.1.1...HEAD
[1.1.1]: https://github.com/artur-rios/heimdall-api/compare/v1.1.0...v1.1.1
[1.1.0]: https://github.com/artur-rios/heimdall-api/compare/v1.0.0...v1.1.0
[1.0.0]: https://github.com/artur-rios/heimdall-api/releases/tag/v1.0.0
