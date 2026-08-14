# Auth responses report the signed-in person's email verification status

**Date:** 2026-08-14
**Status:** Approved

## Problem

Nothing in the API's authentication responses tells a caller whether the signed-in
person's email address is verified. `POST /api/auth/login`, `POST /api/auth/2fa/verify`,
and `POST /api/auth/google` return a token and its expiry and nothing else. The token's
own claims (`IdentityUserMapper`) carry the person's `PublicId`, role, scope ids, flagged
scope permissions, and the `mfaPending` marker — none of them verification status.

`EmailVerified` is published on `PersonOutput` alone, reachable only through the person
view and list endpoints. A `User` who has just signed in therefore has no way to learn
that their address is unverified, and so no way to know it should prompt the person or
call `POST /api/auth/resend-verification` — the endpoint that exists precisely for that
case (UC-15, FR-EV-04).

A second, related defect surfaced while scoping this. `GoogleSignInCommandHandler` writes
`GoogleUser.EmailVerified` only when it creates the row (sign-up, FR-GO-09). On every
later sign-in (FR-GO-10) the column is read but never refreshed, so a returning Google
User whose address was verified at Google after their first sign-in here keeps a stale
`false` in this database indefinitely.

## Scope

In scope:

1. Publishing `emailVerified` on the three authentication success responses.
2. Refreshing `GoogleUser.EmailVerified` from the verified ID token on each sign-in.
3. The requirements, use case, OpenAPI, and user-facing documentation those two imply.

Out of scope: refreshing `GoogleUser.Name`, `Email`, or `ProfilePictureUrl` from the ID
token; gating any endpoint on verification status; a `GET /api/auth/me` endpoint.

## Design

### Contract change

Three command outputs gain one field, named to match `PersonOutput.EmailVerified`:

| Type | Field | Rationale |
| ---- | ----- | --------- |
| `LoginCommandOutput` | `bool? EmailVerified` | Nullable, and left `null` on the AF-11g two-factor challenge shape, alongside `Token` and `ExpiresAt`, which are already null there. |
| `VerifyTwoFactorAuthCommandOutput` | `bool EmailVerified` | Non-nullable — this response exists only on success. |
| `GoogleSignInCommandOutput` | `bool EmailVerified` | Non-nullable, same reason. |

All three paths that hand out a full authentication token report the same thing, so a
client applies one rule regardless of how the person signed in.

The field is omitted on the challenge shape deliberately. A caller holding only a
challenge token has passed the password check but not the second factor, and is not yet
authenticated; telling them an account detail at that point discloses more than UC-11's
success response should. They receive the value from `POST /api/auth/2fa/verify` once the
second factor checks out.

The change is additive and the new field is optional to read, so no existing client
breaks.

### Where each value comes from

- **Login** — `person.EmailVerified`, from the `Person` the handler already loaded for the
  password check. Set only on the full-token branch; `IssueChallengeAsync` leaves it
  `null`.
- **Two-factor verify** — `person.EmailVerified`, from the `Person` the handler already
  loads to build the token subject.
- **Google sign-in** — `payload.EmailVerified`, from the Google ID token verified in this
  same request, not from the stored column. The payload is the freshest available truth
  and costs nothing extra to read.

### Refreshing the stored Google column

On a sign-in that resolves an existing, not-logically-deleted Google User, the handler
compares `googleUser.EmailVerified` with `payload.EmailVerified` and, when they differ,
writes the payload's value back through `googleUserWriter`. Nothing is written when the
values agree, so the ordinary sign-in path stays read-only.

A failed write does not fail the sign-in. The caller has proved who they are and the
token is theirs to receive; a stored flag that could not be updated is a data-freshness
problem, not an authentication one. The response still reports the payload's value, which
is correct regardless of whether the write landed. This mirrors how
`LoginCommandHandler.IssueFreshEmailCodeAsync` treats a delivery failure it cannot
usefully surface to the caller.

Only `EmailVerified` is refreshed. Name, email, and picture drift is the same class of
problem but a wider change — the email in particular is subject to the per-scope
uniqueness rule of FR-GO-07 and cannot be overwritten without re-checking it.

## Requirements

Two new requirement rows, so neither behaviour exists in code with nothing requiring it:

- **FR-EV-05** — On successful authentication, the system shall report whether the
  authenticated person's email address is verified. Priority: Medium. Added to section
  3.6 Email Verification.
- **FR-GO-19** — On each sign-in with an existing Google User, the system shall refresh
  the stored `EmailVerified` from the verified Google ID token claims. Priority: Medium.
  Added to section 3.8 Google Sign-In.

The traceability table at the end of the System Requirements Document lists these groups
by range (`FR-EV-01 through FR-EV-04`, `FR-GO-01 through FR-GO-18`); both ranges are
extended.

## Testing

Tests are written before the implementation, following the project's convention.

Handler tests:

- `LoginCommandHandlerTests` — a verified person yields `true`; an unverified person
  yields `false`; a person with active two-factor authentication yields a challenge
  response whose `EmailVerified` is `null`.
- `VerifyTwoFactorAuthCommandHandlerTests` — the issued token response reports the
  person's stored value.
- `GoogleSignInCommandHandlerTests` — a sign-up reports the payload's value; a returning
  user whose payload disagrees with the stored column is reported from the payload **and**
  has the column updated; a returning user whose values agree triggers no write.

Controller tests, asserting the field on the serialized response, in
`AuthControllerLoginTests`, `AuthControllerVerifyTwoFactorAuthTests`, and
`AuthControllerGoogleSignInTests`.

`OpenApiContractTests` runs against the regenerated document; the three schemas must carry
the new property.

## Documentation

- `docs/openapi/heimdall.json` — regenerated with `scripts/openapi.py`. CI's
  `check-openapi.yml` fails on drift.
- `docs/requirements/System Requirements Document.md` — FR-EV-05, FR-GO-19, and the two
  traceability ranges.
- `docs/requirements/Use Case Specification Document.md` — UC-11, UC-25, and UC-38 each
  draw the response as `200 OK { token, expiresAt }` in their sequence diagrams; all three
  are updated, as is UC-11's postcondition. UC-25 also gains the refresh step.
- `docs/content/en/docs/getting-started.md` — the sentence describing the login response
  fields.
