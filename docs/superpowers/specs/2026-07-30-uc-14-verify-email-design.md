# UC-14: Verify Email — Design

## Summary

Implement UC-14 (Verify Email, FR-EV-03): confirm a person's email address by spending the
time-limited token UC-06 already issues at person creation, through one endpoint:

| Method | Endpoint | Actors |
| --- | --- | --- |
| POST | `/api/auth/verify-email` | Anonymous (no authentication required) |

The postcondition is two writes: `Person.EmailVerified = true` and the token marked `Used`.

**Almost everything this use case needs already exists.** `EmailVerificationToken`,
`EmailVerificationTokenDbMap`, the `email_verification_token` table in `InitialCreate`,
`Person.EmailVerified`, `EmailVerificationService`, and the two senders all landed with UC-06. What is
missing is the half that spends the token — the exact mirror of what UC-13 does for
`PasswordResetToken`. **No migration is required.**

## Shape

| Artifact | File |
| --- | --- |
| `VerifyEmailCommand` | `…Command/Input/VerifyEmailCommand.cs` |
| `VerifyEmailCommandValidator` | `…Command/Input/Validation/VerifyEmailCommandValidator.cs` |
| `VerifyEmailCommandHandler` | `…Command/Handlers/VerifyEmailCommandHandler.cs` |
| `VerifyEmailCommandOutput` | `…Command/Output/VerifyEmailCommandOutput.cs` |
| Messages + status map | `…Shared/Messages/AuthMessages.cs`, `AuthMessageMap.cs` |
| Endpoint | `…WebApi/Controllers/AuthController.cs` |
| DI | `…WebApi/Startup.cs` |

The command carries the token and nothing else — no email, no scope — for the same reason UC-13's
does: a 48-character random string issued to one person identifies them on its own.

## Handler flow

Checks run in the order UC-14's main flow states, which is also UC-13's order:

| Step | Behavior | Flow |
| --- | --- | --- |
| 0 | Validate input shape (NFR-10) | see Decision 3 |
| 1 | Find the token by exact match, `Include(x => x.Person)` | UC-14 step 2 |
| 2 | Not found → `Invalid token.` | AF-14c |
| 3 | `ExpiresAt <= now` → `Token expired.` | AF-14a |
| 4 | `Used` → `Token already used.` | AF-14b |
| 5 | `person.EmailVerified = true`, `person.UpdatedAt = now`, update | UC-14 step 3, FR-EV-03 |
| 6 | Consume every live verification token the person holds | UC-14 step 4, Decision 2 |
| 7 | Return `Email verified.` | UC-14 step 5 |

All three rejections answer `400`, each named, exactly as UC-13's do. Nothing is disclosed by naming
them: the token identifies no account to a caller who does not already hold it.

## Decisions

1. **The three token-rejection messages are shared with UC-13, and the constants are renamed to say
   so.** UC-14's AF-14a/b/c specify the same three strings UC-13's AF-13a/b/c do — "Token expired",
   "Token already used", "Invalid token" — and `AuthMessageMap.StatusCodes` is a dictionary **keyed by
   the message string**, so two constants holding the same value would throw a duplicate-key exception
   at static initialization. Reuse is therefore not merely tidier, it is the only option that keeps the
   spec's wording. `ResetTokenInvalid` / `ResetTokenExpired` / `ResetTokenAlreadyUsed` are renamed to
   `TokenInvalid` / `TokenExpired` / `TokenAlreadyUsed`, since they now belong to both use cases. The
   rename is mechanical (one handler, one map, two UC-13 test files) and changes no string value, so
   UC-13's behavior and its tests' assertions are untouched.

   *Alternative rejected:* UC-14-specific strings ("Invalid verification token.") would avoid the
   rename but deviate from the specification's stated wording for no gain.

2. **Verifying consumes every live verification token the person holds, not only the one presented.**
   UC-06 issues a token per creation and UC-15 will issue more on request, so a person can hold
   several live links. Once one of them has verified the address, the others verify an address that is
   already verified — they are dead weight that survives in an inbox. This mirrors UC-13's decision
   exactly, including its boundary: already-expired tokens are left alone (AF-14a refuses them either
   way, and rewriting them would only make a dead token report a different reason for being dead), and
   another person's tokens are never touched.

   Note this is *not* the same as UC-15 step 3, which invalidates existing tokens before issuing a new
   one. That remains UC-15's job.

3. **A validator, with a token-required rule only.** UC-14 defines no alternative flow for a malformed
   request — unlike UC-13's AF-13d — but NFR-10 requires every input to be validated, and every other
   command in the repository has a validator. The rule answers `400` with `Token is required.`, which
   is the same status an empty token would get by falling through to AF-14c, so no specified flow is
   contradicted. The existing `AuthMessages.TokenRequired` is reused as-is.

4. **An already-verified person is verified again, idempotently.** UC-14 defines no alternative flow
   for it — that is UC-15's AF-15a, about *requesting* a new email, not about spending a token. So a
   live token presented for an already-verified person sets a `true` flag to `true`, consumes the
   token, and answers `200`. Inventing a rejection here would be inventing an alternative flow the
   specification does not define.

5. **A logically deleted person's email is still verified.** Same posture, and the same reasoning
   UC-13 recorded: UC-06 will not have issued a token to someone already deleted, so this only arises
   when the deletion lands between the email and the click, and verification grants nothing —
   UC-11 refuses the login by AF-11c regardless.

6. **The token lookup is case-sensitive**, unlike every email comparison in this system. The token is
   a random secret; folding its case would throw away part of its alphabet. This matches UC-13 and is
   what the unique index on `email_verification_token.token` already enforces.

7. **The output is empty.** The caller has proved they hold a verification token, which is not
   authentication. The response says the email was verified and nothing about whose it was. A token is
   obtained at `/api/auth/login`, as before.

8. **`[AllowAnonymous]`.** The actor is "Anonymous (via email link)" — the person clicks a link in
   their mail client, which holds no bearer token.

## Alternative flows → failure paths

| Flow | Condition | Path | Response |
| --- | --- | --- | --- |
| AF-14a | Token expired | `token.ExpiresAt <= now` | `400` `Token expired.` |
| AF-14b | Token already used | `token.Used` | `400` `Token already used.` |
| AF-14c | Token not found | lookup returns `null` | `400` `Invalid token.` |
| (NFR-10) | Token missing | validator | `400` `Token is required.` |

UC-14 defines no authorization flow — the endpoint is anonymous — so unlike the scope and person use
cases there is no `403` to cover. The functional suite still pins that the endpoint answers without a
bearer token.

## Test coverage

**Unit — `VerifyEmailCommandHandlerTests`:** main flow (flag set, token consumed); AF-14a; AF-14b;
AF-14c; validation failure; sibling live tokens consumed; expired sibling untouched; another person's
token untouched; already-verified person (Decision 4); logically deleted person (Decision 5); the
failure paths leave `EmailVerified` and the token alone.

**Unit — `VerifyEmailCommandValidatorTests`:** token present passes, empty/whitespace token fails with
`TokenRequired`.

**Functional — `AuthControllerVerifyEmailTests`:** main flow asserting response *and* the two rows;
AF-14a; AF-14b; AF-14c, including a token differing only in case; missing token; anonymous access;
sibling consumption and its two boundaries; already-verified; logically deleted; and one test driving
**UC-06 → UC-14 end to end** — create a person through the API, read back the token UC-06's service
wrote (no response ever carries it, and the suite has no Mailgun credentials), then spend it.

## Not in scope

- **UC-15 (Resend Verification Email)** — its own use case, its own issue, its own branch.
- **Refusing login when the email is unverified.** UC-11's alternative flows do not include it, and
  adding it here would change an implemented use case's behavior from outside its specification.
