# UC-15: Resend Verification Email — Design

## Summary

Implement UC-15 (Resend Verification Email, FR-EV-04): let an authenticated person ask for a fresh
verification link when the first one is gone, expired, or never arrived, through one endpoint:

| Method | Endpoint | Actors |
| --- | --- | --- |
| POST | `/api/auth/resend-verification` | User, Scope Admin, System Admin (authenticated, any role) |

The postcondition is two writes and a send: every outstanding verification token the caller holds is
retired, one fresh token is written, and it is mailed.

This is the last of the four `/api/auth` endpoints UC-11 opened the controller for, and the first one
on that controller that is **not** anonymous — the caller is the subject of the request, so there is
no email, no scope, and no token in the body. It is also the first `/api/auth` endpoint whose request
carries no body at all.

**Everything the send half needs already exists.** `EmailVerificationToken`,
`EmailVerificationService`, `EmailVerificationOptions`, and the two senders landed with UC-06;
UC-14 built the half that spends a token. What is missing is the half that *reissues* one.
**No migration is required.**

## Shape

| Artifact | File |
| --- | --- |
| `ResendVerificationEmailCommand` | `…Command/Input/ResendVerificationEmailCommand.cs` |
| `ResendVerificationEmailCommandHandler` | `…Command/Handlers/ResendVerificationEmailCommandHandler.cs` |
| `ResendVerificationEmailCommandOutput` | `…Command/Output/ResendVerificationEmailCommandOutput.cs` |
| Messages + status map | `…Shared/Messages/AuthMessages.cs`, `AuthMessageMap.cs` |
| Endpoint | `…WebApi/Controllers/AuthController.cs` |
| DI | `…WebApi/Startup.cs` |

The command carries nothing but `IActorScoped`'s two fields, set by the controller from the bearer
token. Anything a caller could put in a body here — an email, a person id — would be a way to ask for
someone else's link.

## Handler flow

| Step | Behavior | Flow |
| --- | --- | --- |
| 1 | Find the person by `PublicId == command.ActingPersonId` | UC-15 step 1 |
| 2 | Not found → `Person not found.` (404) | see Decision 3 |
| 3 | `person.EmailVerified` → `Email already verified.` (400) | AF-15a |
| 4 | Retire every live verification token the person holds | UC-15 step 3 |
| 5 | `IEmailVerificationService.IssueAndSendAsync(person)` | UC-15 steps 4 and 5 |
| 6 | Return `Verification email sent.` (200) | UC-15 step 6 |

Steps 4 and 5 are exactly the two halves FR-EV-04 asks for, and step 5 is the same call UC-06 makes
at creation — the token's length, alphabet, and lifetime are that service's business, not this
handler's.

## Decisions

1. **No validator.** UC-15's request has no caller-supplied input: the person is read from the token
   and nothing else is read at all. NFR-10 requires input to be validated, and there is none to
   validate. This matches `DeletePersonCommand` and `HardDeletePersonCommand`, the other two commands
   whose entire content comes from the route and the actor — neither has a validator either.

   *Alternative rejected:* an empty validator registered for symmetry with UC-11…UC-14. It would
   assert nothing and still need a DI registration and a test file.

2. **Reissuing retires every live token the person holds, not just the most recent one.** UC-15 step 3
   says "invalidates any existing verification tokens", and the plural is the point: UC-06 issues one
   at creation and every resend issues another, so a person can hold several live links at once. After
   a resend, only the newest link should work — otherwise "resend" would mean "add another way in"
   rather than "replace the one you have".

   The boundary is UC-14's, for UC-14's reason: **already-expired tokens are left alone** (AF-14a
   refuses them either way, and rewriting them would only make a dead token report a different reason
   for being dead), and another person's tokens are never touched.

3. **A token naming a person who no longer exists answers 404.** Authentication runs in `ClaimsOnly`
   mode — no database read per request — so a well-formed, unexpired bearer token outlives the person
   it names, which is exactly what happens when someone is hard-deleted (UC-10) while holding one. The
   caller is authenticated but there is no address to send to, so this cannot answer 200. UC-15 defines
   no alternative flow for it because the specification's precondition assumes the actor exists; 404
   with `Person not found.` is the answer UC-07 AF-07a, UC-09 AF-09a, and UC-10 AF-10a already give for
   the same fact.

   The constant is a new `AuthMessages.PersonNotFound`, holding the same string as
   `PersonMessages.PersonNotFound`. The two message maps are separate dictionaries, so the shared value
   is not the duplicate-key problem UC-14 Decision 1 ran into within a single map.

   *Alternative rejected:* 401. The token is valid and was validated; the middleware's job is done.
   Answering 401 for a data condition would tell a caller to re-authenticate, which would produce the
   same token again.

4. **An already-verified address is refused, and that is the whole of AF-15a.** It is the only
   alternative flow UC-15 defines, and unlike UC-14's idempotent success there is a real reason to
   refuse: a verification link mailed to an address that is already verified is a link that can do
   nothing when clicked. The check runs before step 3, so a refused request retires no tokens.

5. **A logically deleted person is still sent a link.** UC-15 defines exactly one alternative flow, so
   a second rejection would be one this design invented. The same reasoning UC-14 Decision 5 recorded
   applies: the person is asking about their own address, verifying it grants nothing on its own, and
   UC-11 refuses their login by AF-11c regardless. This deliberately does *not* follow UC-12's
   `MayRecover`, which withholds a reset link from a deleted person — UC-12 must answer identically
   either way for anti-enumeration reasons, so withholding costs it nothing; here, withholding would
   have to be a named error the specification does not define.

   This is the one place where the design takes a position the specification leaves genuinely open.

6. **The handler does nothing about a send failure, because `MailgunSender` already has.** Delivery
   failures — a refused send, a timeout, a bad API key — are logged and swallowed inside the sender,
   deliberately, so UC-12's AF-12a cannot be turned into an enumeration oracle. `IssueAndSendAsync`
   therefore does not fail for UC-15 either, and the endpoint answers 200 whether or not the mail
   actually went out. The token is persisted before delivery is attempted, so a caller who never
   receives the mail can simply call this endpoint again — which is the use case's whole point.

   No try/catch is added here. `CreateAdminCommandHandler` calls the same service the same way.

7. **The output is empty.** The token is mailed, never returned — the same reason UC-12's output is
   empty. A response carrying the token would make the endpoint a way to verify an address without
   ever reading the mailbox it belongs to, which is the one thing verification exists to prove.

8. **Authenticated, with no `[RoleRequirement]`.** The authorization matrix gives Email Verification to
   all three roles and withholds it from Anonymous, and §5.4 marks this endpoint "Authenticated". A
   role attribute would add a gate the matrix does not ask for. There is no per-actor rule to enforce
   either: the caller can only ever act on themselves.

## Alternative flows → failure paths

| Flow | Condition | Path | Response |
| --- | --- | --- | --- |
| AF-15a | Email already verified | `person.EmailVerified` | `400` `Email already verified.` |
| (precondition) | Not authenticated | middleware, no `[AllowAnonymous]` | `401` |
| (Decision 3) | Token names no existing person | lookup returns `null` | `404` `Person not found.` |

UC-15 is the first authentication use case with an authorization flow to cover — the `401` an
anonymous caller gets — since UC-11…UC-14 are all open to anonymous callers.

## Test coverage

**Unit — `ResendVerificationEmailCommandHandlerTests`:** main flow (a fresh token issued and sent,
asserted through a Moq `IEmailVerificationService`); AF-15a, including that it retires nothing and
sends nothing; the person-not-found path; outstanding live tokens retired (Decision 2); an expired
sibling left alone and another person's token untouched (Decision 2's boundaries); a logically deleted
person still served (Decision 5); a person holding no tokens at all served normally.

**Functional — `AuthControllerResendVerificationEmailTests`:** main flow for a `User`, a `ScopeAdmin`,
and a `SystemAdmin`, asserting the response *and* the token rows; AF-15a; the anonymous `401`; the
404; sibling retirement and its two boundaries; and one test driving **UC-06 → UC-15 → UC-14 end to
end** — create a person through the API, resend, confirm the first token is dead by AF-14b and the
second verifies the address.

## Not in scope

- **Rate limiting the resend.** Nothing in UC-15, FR-EV-04, or the non-functional requirements asks
  for one, and adding a throttle would add an alternative flow — and a status code — that no document
  defines.
- **Refusing login when the email is unverified.** UC-11's alternative flows do not include it, as
  UC-14 already recorded.
- **A resend on someone else's behalf.** An admin resending a User's verification email is not what
  UC-15 describes ("Authenticated person requests a new verification email"), and it would need an
  authorization rule the matrix does not define.
