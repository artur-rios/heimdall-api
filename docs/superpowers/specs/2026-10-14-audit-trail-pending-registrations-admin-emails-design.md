# Audit trail, pending self-registrations and admin-initiated emails — design

**Issues:** heimdall-api #174 (list the audit trail), #175 (list and discard pending
self-registrations), #177 (let an administrator resend verification or send a password reset).
**Origin:** the admin-gaps analysis, §8 of heimdall-ui's
`docs/superpowers/specs/2026-10-13-scope-sign-in-settings-and-admin-gaps-design.md`. #176 (list
System Admins) was done separately as a bounded change.
**Out of scope:** the Heimdall UI screens for these endpoints (each follows in its own heimdall-ui
issue) and any release.

## 1. Decisions taken with the owner

| Question | Decision |
| --- | --- |
| Who reads the audit trail | System Admins only. `audit_log` has no scope column, and old rows cannot be attributed to a scope reliably (targets may be deleted or anonymised), so a Scope Admin view is not offered. |
| Pending self-registrations | List and discard, for the scope's owners and System Admins. A discard is audited. A discarded registrant may simply sign up again. |
| Throttling admin-initiated email | A per-person cooldown: at most one email of each kind per person per 5 minutes (configurable), whoever asked. A repeat inside the window is refused with `429` and nothing is sent. |

## 2. Shared conventions

- Each issue is its own branch from `develop` and its own pull request closing its issue:
  `feature/uc-48-list-audit-trail` (#174, also carries this spec and the plan),
  `feature/uc-49-pending-registrations` (#175), `feature/uc-51-admin-account-emails` (#177).
- New use cases, numbered in this order: **UC-48** List the Audit Trail, **UC-49** List Pending
  Registrations, **UC-50** Discard a Pending Registration, **UC-51** Send a Verification Email on a
  Person's Behalf, **UC-52** Send a Password Reset on a Person's Behalf. Each is written in the Use
  Case Specification Document's table + sequence diagram + numbered flow + `AF-nn` style and added
  to the traceability table.
- New requirements: **FR-AT-01…** (a new "Audit Trail" group), **FR-SR-11…** (pending
  registrations), **FR-EV-06…** (admin verification email), **FR-PR-05…** (admin password reset).
- Every write goes through `AddAuditedCommandHandler`, so it is audited with the affected entity as
  `TargetId`. Reads are not audited, as today.
- Every message a handler returns has a message-map entry citing its UC/AF. OpenAPI is regenerated
  (`python scripts/openapi.py`) and CHANGELOG `[Unreleased]` gains an entry. `api-client/http`,
  `api-client/bruno` and `docs/content/en/docs/*` are extended wherever comparable endpoints are
  listed there.
- Definition of Done as the Development Workflow Document states it: unit tests for each handler's
  main flow and every AF, functional tests for each endpoint's main flow and every AF including
  authorization, and the Step 4.1 personal-data check.

## 3. #174 — List the audit trail (UC-48)

**Endpoint.** `GET /api/audit-log`, `[RoleRequirement(SystemAdmin)]`, in a new `AuditController`.

**Query** (`ListAuditLogQuery : BaseQuery`), all filters optional and combined with AND:

| Parameter | Meaning |
| --- | --- |
| `From`, `To` | Inclusive bounds on when the entry was recorded (UTC). |
| `ActorId` | The actor's public id. |
| `Action` | Exact action name, as recorded: the command's type name, e.g. `CreateScopeCommand`. |
| `TargetId` | The affected entity's public id. |
| `Succeeded` | `true` for completed writes, `false` for refused ones. |
| `PageNumber`, `PageSize` | As every listing (`PaginatedQueryValidator`). |

Validation: `To` must not be earlier than `From`; `Action` at most 200 characters (the column's
length); paging as `PaginatedQueryValidator`. Refusals are `400` with the validator's messages.

**Answer.** `PaginatedOutput<AuditLogEntryOutput>`, newest first (`CreatedAt` descending, then
`Id` descending as the tie-breaker):

| Field | Source |
| --- | --- |
| `id` | `PublicId` |
| `recordedAt` | `CreatedAt` |
| `actorId`, `actorRole` | `ActorPersonId`, `ActorRole` — **`null`** when the entry had no actor or the retention pass cleared it (attribution period over, or the actor anonymised) |
| `action` | `Action` |
| `targetId` | `TargetId` (nullable) |
| `succeeded` | `Succeeded` |
| `failureReason` | `FailureReason` (nullable; canonical messages only, by construction) |

The listing shows what the table holds and never reconstructs a cleared actor — no join to persons,
no inference from other entries (NFR-09, Data Retention Schedule §7.1). Filtering by `ActorId`
therefore cannot find entries whose actor has been cleared; the use case says so.

**Schema.** A migration adds an index on `audit_log.TargetId`, so "what happened to this entity?"
does not scan the table. The append-only triggers are untouched (adding an index is DDL, not a row
change). No new columns, so no new personal-data purpose row; the Data Protection Document notes
that the audit trail is now readable by System Admins through the API.

**AFs.** AF-48a filters invalid (`400`); AF-48b caller not a System Admin (`403`); AF-48c no
entries match (`200` with an empty page).

## 4. #175 — Pending self-registrations (UC-49, UC-50)

**Schema.** A migration adds `PublicId uuid NOT NULL` with a unique index to
`pending_registration`. Existing rows are filled by the migration with generated values
(`gen_random_uuid()`); the entity defaults it to `Guid.NewGuid()`. The id is random and carries no
personal data; the Data Protection Document's purpose row for the table names it as the
administrative reference, and the Data Retention Schedule is unchanged (the row's lifetime is not).

**UC-49 — List.** `GET /api/scopes/{id}/pending-registrations`, for System Admins and the scope's
owners (`ScopeOwnershipChecker.ActorMayManageScopeAsync`). Paginated, newest first (`CreatedAt`
descending, `Id` tie-breaker). Each item:

| Field | Source |
| --- | --- |
| `id` | `PublicId` |
| `name`, `email` | `Name`, `Email` |
| `createdAt`, `expiresAt` | `CreatedAt`, `ExpiresAt` |
| `attemptsLeft` | `MaxConfirmationGuesses − FailedAttempts`, never below 0 |
| `expired` | `ExpiresAt <= now` — such rows remain until `PurgeExpiredTokens` removes them |

Never exposed: `PasswordHash`, `Salt`, `TokenHash`. AFs: scope not found (`404`, the scope
endpoints' existing message; a logically deleted scope is treated as the scope's other
sub-resource endpoints, such as its owners, treat it), caller neither System Admin nor an owner (`403`,
`NotScopeOwner`-style message as the other scope endpoints use), none pending (`200`, empty page).

**UC-50 — Discard.** `DELETE /api/scopes/{id}/pending-registrations/{registrationId}`, same access
rule. Removes the row, so its link stops working (following it then behaves as for any unknown
token). Audited; the output carries the registration's `PublicId` as `Id` so it is the audit
target. AFs: scope not found (`404`), not authorized (`403`), registration not found **in that
scope** (`404` — a registration of another scope is indistinguishable from a missing one).

**Anti-enumeration.** FR-SR-06's guarantee is about the anonymous `POST /api/auth/register`, which
keeps answering identically for free and taken addresses; nothing here changes it. These endpoints
are authenticated and limited to the scope's owners, who would see the same name and address in
their user list once the registration is confirmed. A taken address never produces a pending row,
so the listing reveals nothing about existing accounts.

## 5. #177 — Admin-initiated account emails (UC-51, UC-52)

**Endpoints** (in `PersonController`, under `api`):

- `POST /api/persons/{id}/verification-email` — UC-51.
- `POST /api/persons/{id}/password-reset` — UC-52.

No body. `{id}` is the person's public id. Both are `[RoleRequirement(SystemAdmin, ScopeAdmin)]`.

**Who may act on whom.** A System Admin on any person. A Scope Admin only on a `User` whose scope
they own (`ScopeOwnershipChecker`); any other target is refused `403` with a dedicated message, the
way `NotAuthorizedToViewPerson` refuses reads. Google Users are not persons and are not reachable.
An unknown or hard-deleted id is `404` `PersonNotFound`.

**UC-51 — Verification email.** Refused when the person is logically deleted or processing is
restricted (`409`, one message each — sending would be processing a restricted identity, NFR-24),
or the email is already verified (`400`, the existing `EmailAlreadyVerified`). Otherwise it marks
the person's live verification tokens used, as UC-15 does, and calls
`IEmailVerificationService.IssueAndSendAsync(person)`, so the link uses the scope's
`EmailVerificationUrl` exactly as self-service does. Answers `200` with a fixed message.

**UC-52 — Password reset.** Eligibility is UC-12's (`PasswordRecoveryProcessor.MayRecover`):
refused when restricted or logically deleted (`409`), when a User's scope is logically deleted, or
a Scope Admin has no live scope (`409`, one message for "the account cannot sign in"). The rule is
moved into a shared service so both callers use one definition. Otherwise it calls
`IPasswordResetService.IssueAndSendAsync(person)` directly — not through the anonymous recovery
queue: the caller is authenticated and is owed a real answer. Answers `200` with a fixed message.
Existing live reset tokens are left as `IssueAndSendAsync` leaves them for UC-12.

**Neither endpoint** returns, logs or accepts a token or a password; the link goes only to the
person's own stored address. `MailgunSender` swallowing delivery failures is unchanged: `200`
means the email was handed to the sender.

**Cooldown (FR-EV-07 / FR-PR-06).** At most one email of each kind per person per
`AdminEmailCooldown` (default 5 minutes, configurable through an environment variable in the
Operations document's style). It is derived, not stored: a token of that kind issued for the person
within the window means the cooldown is running, whether that token has since been used or
superseded. A token's issue time is `ExpiresAt − TokenLifetime` for its kind's options, so
self-service sends (UC-12, UC-15) count too, and no column or migration is needed. (Changing a
`TokenLifetime` shifts the derived times of tokens already issued; the effect on a five-minute
window is accepted.) Inside the window the request is refused `429` with a message saying an email was sent
recently, and nothing is sent. The cooldown applies only to these two admin endpoints; UC-12 and
UC-15 are unchanged.

**Audit.** Both commands are registered with `AddAuditedCommandHandler`; the outputs carry the
person's public id as `Id`, so each entry names the actor (the admin) and the target (the person).
Refusals are audited too, as for every command.

**AFs** (each use case): AF-a not found (`404`); AF-b caller may not act on this person (`403`);
AF-c deleted or restricted (`409`); AF-d already verified (UC-51, `400`) / cannot sign in (UC-52,
`409`); AF-e cooldown running (`429`).

## 6. Testing

- Unit: each handler's main flow and every AF above; the cooldown boundary (just inside and just
  outside the window, with a fake clock if the services take one, otherwise by seeding tokens with
  computed `ExpiresAt`); the audit listing's filters, ordering and `null` actor projection; the
  registration listing's `attemptsLeft` floor and `expired` flag.
- Functional: each endpoint's main flow and every AF, including `401` anonymous and `403` for the
  wrong role or a non-owning Scope Admin; the audit listing shows an entry written by an earlier
  request in the same test; a discarded registration's link no longer confirms; the admin emails
  reach the logging sender with the person's own address.
- Data: the two migrations apply to an existing database (rows get a `PublicId`; the index exists).
- `PersonalDataPurposeTests` and `SchemaTests` stay green.

## 7. Order of work

#174 first (it carries this spec and the plan), then #175, then #177. They touch different
controllers and tables, so their pull requests can merge in any order; CHANGELOG `[Unreleased]`
and the requirement documents will conflict trivially between them and are resolved by keeping
both sides.
