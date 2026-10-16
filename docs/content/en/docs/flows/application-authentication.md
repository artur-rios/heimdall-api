+++
title = 'Application authentication'
linkTitle = 'Application authentication'
weight = 80
description = 'UC-53 to UC-57 — credentials for a registered application, the token they buy, and the live re-check consumers make.'
+++

An application authenticates to Heimdall as itself: an administrator issues it a secret (UC-53), the
application exchanges the secret for a short-lived token (UC-56), and a consumer service checks that
token live (UC-57). This page is written for the people building that consumer. The authoritative
descriptions live in the
[Use Case Specification Document](../../requirements/use-case-specification-document/).

## Who this is for

A service that must know *which registered application* is calling it, not *which person*. Cerberus
is the first. Such a service never sees a person's token for this purpose and never holds a role:
an application token carries the application's identity and nothing else.

## Provisioning and rotation

A System Admin, or the Scope Admin who owns the application and still owns its scope, issues a
credential. The `expiresAt` in the body is optional; a value without an offset is read as UTC.

```bash
curl -X POST http://localhost:5177/api/scopes/$SCOPE_ID/applications/$APPLICATION_ID/credentials \
  -H "Authorization: Bearer $ADMIN_TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"expiresAt":"2030-01-01T00:00:00Z"}'
```

The answer is `201 Created`:

```json
{
  "data": {
    "id": "6f1c0d52-8f0e-4a43-9c52-2d6f6a9b1d11",
    "secret": "hds_q3y0lV8mJ1k0d9yq5Qh2a8Xv7nTz4RbC6uWe1sPoKfM",
    "createdAt": "2026-10-14T12:00:00Z",
    "expiresAt": "2030-01-01T00:00:00Z"
  },
  "messages": ["Application credential issued. Store the secret now: it is never shown again."],
  "errors": [],
  "success": true
}
```

**The secret appears in this response and nowhere else.** Heimdall keeps only its SHA-256 hash, so no
later call, backup or log can return it. Put it in the consumer's secret store now. A lost secret is
replaced, not recovered: issue another and revoke the lost one.

An application holds at most **two live credentials** (not revoked, not expired). That limit is what
makes rotation free of downtime:

1. issue a second credential (`POST …/credentials`);
2. deploy its secret to the application;
3. revoke the first (`DELETE …/credentials/{credentialId}`).

With two already live, a third is refused with `409 Conflict`; revoke one first. `GET …/credentials`
lists them newest first with `id`, `createdAt`, `expiresAt`, `revokedAt`, `lastUsedAt` and `live`, so
you can see the new secret in use (`lastUsedAt`) before you revoke the old one.

## Obtaining a token

The application exchanges a secret for a token. The endpoint is anonymous: the secret authenticates.

```bash
curl -X POST http://localhost:5177/api/applications/token \
  -H "Content-Type: application/json" \
  -d "{\"applicationId\":\"$APPLICATION_ID\",\"scopeId\":\"$SCOPE_ID\",\"secret\":\"$APPLICATION_SECRET\"}"
```

```json
{
  "data": {
    "id": "0c9a3e7e-5a53-4a61-8e57-6b3f0e0d7a10",
    "token": "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9…",
    "expiresAt": "2026-10-14T12:10:00Z"
  },
  "messages": ["Application token issued."],
  "errors": [],
  "success": true
}
```

Request a new token shortly before `expiresAt` and reuse it until then. **Never request one per
call**: the endpoint is rate-limited per client address like person sign-in, and each exchange is
audited.

Every refusal is the same `401 Unauthorized` — "The application credentials were not accepted." — so
the endpoint reveals nothing about which applications, scopes or credentials exist. When you see it,
check, in order:

- the `applicationId` is the application the credential was issued for;
- the `scopeId` is that application's scope;
- the credential is live: not revoked, not past its `expiresAt`, and the application and scope are
  not deleted (`GET …/credentials` shows `live`);
- the secret is exactly as issued. Surrounding whitespace is trimmed (a trailing newline from a
  file, for one); a truncated or altered secret is not accepted.

A request that is missing a field, carries a secret over 100 characters, or is not readable JSON
(or carries an id that is not a GUID) is a `400` instead: the field's message, or
"The request is malformed." for a body that cannot be read.

## The token

An application token is a JWT signed with HMAC-SHA256 under the same keys as person tokens, valid for
ten minutes by default (`HEIMDALL_APPLICATION_TOKEN_EXPIRATION_IN_SECONDS`).

| Claim | Value |
| --- | --- |
| `iss` | `HEIMDALL_AUTH_TOKEN_ISSUER`, when one is configured |
| `aud` | `HEIMDALL_APPLICATION_TOKEN_AUDIENCE` — never the person audience |
| `exp`, `iat`, `nbf` | Expiry, issue time and not-before |
| `tokenKind` | `application` |
| `id` | The application's identifier |
| `scopeId` | The identifier of the application's scope |
| `credentialId` | The credential the token was issued from |
| `name` | The application's name |

There is **no role claim**. An application token never grants a role, and Heimdall's own endpoints
refuse it: any token carrying `tokenKind` is refused where a person token is expected.

## Validating offline

A consumer can validate the token without calling Heimdall. It must check all of these:

- the algorithm is HS256, and nothing else is accepted;
- the signature, against **both** signing keys while a rotation is in progress
  (`HEIMDALL_AUTH_TOKEN_SECRET` and `HEIMDALL_AUTH_TOKEN_SECRET_PREVIOUS`);
- the issuer, when Heimdall is configured with one (Heimdall itself requires its own issuer, or none
  when none is configured, and the application audience as the token's only audience — NFR-30);
- **the audience is the application audience**, the value of `HEIMDALL_APPLICATION_TOKEN_AUDIENCE`;
- the lifetime, with **zero clock skew**;
- `tokenKind` is `"application"`.

> **If the consumer also validates person tokens, it must pin the person audience.** Both kinds are
> signed with the same keys. A consumer that checks the signature but not the audience would accept
> an application token as a person's. Heimdall refuses to start when the two audiences are equal for
> exactly this reason. Pinning it also refuses Heimdall's 2FA challenge and subject-rights-only tokens,
> which carry audiences of their own ([Login: audiences](../login/#audiences-what-a-consumer-must-pin)).

Heimdall itself validates with zero skew, so instances whose clocks differ can refuse a freshly issued
token for the length of the drift. Keep clocks synchronised (NTP).

## Checking live

Offline validation says the token was issued and has not expired. It cannot say the credential is
still wanted. For that, present the token to Heimdall:

```bash
curl http://localhost:5177/api/applications/self \
  -H "Authorization: Bearer $APPLICATION_TOKEN"
```

```json
{
  "data": {
    "applicationId": "0c9a3e7e-5a53-4a61-8e57-6b3f0e0d7a10",
    "name": "Cerberus",
    "scopeId": "3b1e5d46-1d3c-4f4e-8a35-0f6d6b6f8e22",
    "credentialId": "6f1c0d52-8f0e-4a43-9c52-2d6f6a9b1d11",
    "expiresAt": "2026-10-14T12:10:00Z"
  },
  "messages": ["Application token accepted."],
  "errors": [],
  "success": true
}
```

The answer is `200 OK` only when the token validates (as above) **and** its credential is live and
its application and scope are live and match the token. Anything else is one `401 Unauthorized` —
"The application token was not accepted." — whatever failed.

## Revocation policy

Revoking or expiring a credential, or deleting the application or its scope, takes effect **at the
next live check** for every token already issued. A token's own `exp` is fixed when it is issued, so
it **stays valid offline for up to its whole lifetime**, ten minutes by default, after its credential
expires or is revoked. Only the live check reflects that at once. So a consumer **re-checks before
every sensitive release**, and treats the offline check as a fast path for everything else.

## Status codes

| Endpoint | Status | When |
| --- | --- | --- |
| `POST …/credentials` | `201` | Credential issued; the secret is in the body |
| | `400` | `expiresAt` is not in the future, is not a date-time, or the body is not JSON (AF-53c) |
| | `401` / `403` | Anonymous; a `User`, or a Scope Admin who does not own the application or no longer owns its scope (AF-53b) |
| | `404` | No live application with that id in the addressed, live scope (AF-53a) |
| | `409` | Two live credentials already (AF-53d) |
| `GET …/credentials` | `200` | The credentials, newest first; an empty list when there are none (AF-54c) |
| | `401` / `403` / `404` | As above (AF-54a, AF-54b) |
| `DELETE …/credentials/{credentialId}` | `200` | Credential revoked |
| | `401` / `403` / `404` | As above, and `404` when the credential is not on this application (AF-55a to AF-55c) |
| | `409` | Already revoked (AF-55d) |
| `POST /api/applications/token` | `200` | Token issued |
| | `400` | A field missing, the secret over 100 characters, or a body that is not readable (AF-56a) |
| | `401` | Any other reason, always the same message (AF-56b) |
| | `429` | Rate limit; the `Retry-After` header gives the seconds to wait |
| `GET /api/applications/self` | `200` | Token valid and still live |
| | `401` | Any reason, always the same message (AF-57a) |
