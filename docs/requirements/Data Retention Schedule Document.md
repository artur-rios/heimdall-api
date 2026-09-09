---
title: "Data Retention Schedule Document"
linkTitle: "Data Retention Schedule Document"
weight: 45
description: "How long each category of personal data is kept, why, and which periods the API enforces itself."
---

# Data Retention Schedule Document — Heimdall API

## 1. Purpose

This document states **how long the Heimdall API keeps each category of personal data, and why**.
It is the record NFR-19 requires, and it is the source of truth the retention passes are configured
against.

Two obligations make it necessary. GDPR Art. 5(1)(e) and LGPD Art. 15 require that personal data be
kept in identifiable form no longer than the purpose requires. GDPR Art. 13(2)(a) and Art. 30(1)(f)
require that the period, or the criteria used to determine it, be *stated* — to the data subject at
collection, and in the record of processing activities.

The second obligation is the reason this is a document rather than a set of constants. A period that
lives only in code is not stated to anybody.

> **A period this document states and nothing enforces is marked "not enforced" and names the issue
> that will enforce it.** A schedule that describes behaviour the system does not have is worse than
> no schedule, because it is relied upon.

## 2. Scope

Every store that holds personal data — meaning data relating to an identified or identifiable
natural person (GDPR Art. 4(1), LGPD Art. 5 I). That is the tables in the `heimdall` schema, and
the stores outside it that copy or record them: the application logs and the database backups.

The two outside the schema are the easiest to forget and among the least bounded, which is why they
are rows here rather than an operational footnote. A backup in particular is a full copy of every
category below, and is subject to the same limits as the live database.

`APPLICATION` is out of scope: an application is a non-human identity representing another system.
Its `OwnerId` points at a person, but the row describes the system, not the owner, and the owner's
own record is covered below.

## 3. The schedule

| Category | Where it lives | Retention period | Why that period | Enforced |
| --- | --- | --- | --- | --- |
| Active identity | `PERSON`, `GOOGLE_USER` | Life of the account | Performance of the service the account exists for. An identity provider cannot authenticate an identity it has deleted | By deletion (UC-09/UC-10, UC-28/UC-29) |
| Logically deleted identity | `PERSON`, `GOOGLE_USER` where `is_deleted` | **30 days** where the subject asked, **90 days** where an administrator did — §4 | A soft delete is a restriction of processing, not an erasure, so it needs a terminal state. The shorter deadline is the law's, not a choice; the longer one is a reversal window. Both end in anonymisation | ✅ Scheduled anonymisation (§4.4) |
| Authentication material | `PERSON.password_hash`, `PERSON.salt`, `TWO_FACTOR_AUTH`, `TWO_FACTOR_RECOVERY_CODE` | Life of the account | A security measure under GDPR Art. 32; it lives and dies with the credential it protects. Removed by the cascades of NFR-11 and the `ON DELETE CASCADE` foreign keys | By deletion cascade |
| Single-use tokens | `PASSWORD_RESET_TOKEN`, `EMAIL_VERIFICATION_TOKEN`, `TWO_FACTOR_EMAIL_CODE` | Expiry **+ 7 days** (default, configurable) | §5 | ✅ Scheduled purge (§6) |
| Audit trail | `AUDIT_LOG` | **18 months** identifiable, then pseudonymised and kept — §7 | Accountability (GDPR Art. 5(2)) needs the trail for as long as a claim could be raised against it. A pseudonymised entry is no longer personal data, so storage limitation stops applying and the forensic value survives | ❌ Not enforced — [#97](https://github.com/artur-rios/heimdall-api/issues/97) |
| Application logs | Serilog file sink | **12 months** — conditional, see §8 | Security detection lag, not operational debugging: these are the telemetry [#105](https://github.com/artur-rios/heimdall-api/issues/105) reads, and a shorter period deletes the evidence of a breach before anyone knows to look for it | ❌ Not enforced — [#98](https://github.com/artur-rios/heimdall-api/issues/98) |
| Database backups | The backup store, outside the schema | **No number of its own** — bounded by §9 | A backup is a full copy of every category above. Its period cannot be set independently of the erasure deadlines it would otherwise undo | ❌ Not enforced — [#106](https://github.com/artur-rios/heimdall-api/issues/106) |
| Network data | Rate limiter partition key (`RemoteIpAddress`) | The fixed window, in memory only | An IP address is personal data under both laws. It is never persisted and never logged; the window is one minute and the key is discarded with it | By construction |
| Data Protection key ring | `DATA_PROTECTION_KEYS` | Life of the encrypted material | Not personal data itself, but the TOTP secrets of NFR-16 are undecryptable without it. Listed so nobody purges it as housekeeping | Never purged, deliberately |

Every period is decided, and two are now enforced. Three rows are decided but **not yet enforced**,
and each says so with the issue that will enforce it: the audit trail (§7), the application logs
(§8), and the backups (§9). That is the rule in §1 working, not an oversight — the alternative is a
document that reads as though the system already did these things.

Backups are the one row still carrying no number, and deliberately: their period follows from a
strategy choice §9 sets out, so a figure here would prejudge it.

## 4. Logically deleted identities: two deadlines, because there are two reasons

Setting `IsDeleted` removes an identity from default queries and stops it authenticating. Under both
laws that is a **restriction of processing**, not an erasure — a legitimate intermediate state, and
one the system needs. It simply cannot be the terminal one, which is what it is today: nothing
anywhere removes a soft-deleted row, so a person "deleted" in 2026 is still fully identifiable in
2036.

The terminal state depends on why the record was deleted, because the two cases rest on different
legal footing.

### 4.1 The subject asked: 30 days

Not a period chosen here. GDPR Art. 12(3) requires the controller to act on an erasure request
without undue delay and **at most one month**; LGPD Art. 19 §2 sets 15 days for the confirmation and
access response that usually accompanies it. Thirty days is the outer limit, not the target — the
erasure completes as soon as it can, and the deadline is what it must not exceed.

One consequence for the request path: NFR-12 refuses to strip a scope of its last owner, so an
erasure can be **blocked** by a rule that needs a human to resolve, by transferring ownership. A
blocked erasure is not an extended one. The deadline keeps running, which means the pending-request
view has to surface what is overdue rather than merely what is queued.

**Nothing writes this kind yet.** The anonymisation pass applies this deadline to any record
carrying it, and the deadline is enforced today — but the only way to be deleted at present is
administratively, so in practice every record currently takes §4.2's window. The self-service
erasure request is UC-42 ([#91](https://github.com/artur-rios/heimdall-api/issues/91)), and it
writes through the seam this leaves. The mechanism is complete; the trigger for the shorter deadline
is what is missing.

### 4.2 An administrator did: 90 days

No subject asked, so the deadline is the controller's own and the purpose is different: a reversal
window. An administrator who deleted the wrong person needs to undo it, and the client systems in
that person's scope need time to notice and reconcile.

Ninety days rather than the thirty most consumer services use, because a deletion here is not
confined to one product: Heimdall is the identity provider every client system authenticates
against, so one deletion propagates everywhere at once and is correspondingly more expensive to
discover late.

### 4.3 Both end in anonymisation, not deletion

NFR-07 requires every foreign key in the schema to still resolve after a deletion, and removing the
row breaks that. Anonymising in place does not: the identifying columns go — `Name` and `Email`
overwritten, `PasswordHash` and `Salt` zeroed, the behavioural counters reset, and for a Google User
`GoogleId` and `ProfilePictureUrl` cleared — while the row keeps its structural role. The person's
single-use tokens and their two-factor configuration go with them, the latter taking its recovery
codes and email codes by cascade.

That satisfies erasure. GDPR Recital 26 and LGPD Art. 12 both put anonymised data outside the law's
scope, so an anonymised row is no longer personal data being retained. Hard deletion stays available
as UC-10 for the cases that genuinely want the row gone.

### 4.4 How the anonymisation runs

`IdentityAnonymisationService` dispatches `AnonymiseExpiredDeletionsCommand` on the same interval as
the token purge, and the pass shares that one's properties: bounded to a batch per run, needing no
coordination between instances, audited so each run leaves the evidence that erasure happened, and
treating a run with nothing due as a success.

Three things are specific to it.

**The deadline comes from the record, not the pass.** Each row carries `DeletionKind`, and the
cutoff applied is the one that kind names. A row with no kind — deleted before this mechanism
existed, and backfilled by the migration — is treated as administrative, because guessing that
somebody had requested erasure would invent a request that may never have been made and apply the
shorter deadline to it.

**A row with no `DeletedAt` is skipped, not taken.** A window that cannot be shown to have elapsed
is not one to act on. The migration backfills `DeletedAt` from `UpdatedAt` for rows deleted before
the column existed; that proxy is equal to or later than the true deletion, never earlier, so a
backfilled window closes on time or late and can never erase a record early.

**The dependents go first, and a failure there stops the run.** Anonymising the person while leaving
their two-factor secret behind would erase the label and keep the material — and the next run would
never return to it, because it selects on records not yet anonymised.

## 5. Single-use tokens: why the period is expiry plus a grace period

A password reset token, an email verification token, and a two-factor email code all stop serving
their purpose the moment they expire or are consumed. Storage limitation says to remove them, and
their retention is the shortest in the schedule.

It is not zero, and the reason is UC-13. A presented token has three distinct outcomes:

| Outcome | When |
| --- | --- |
| `TokenInvalid` | No row matches the presented value |
| `TokenExpired` | The row exists and `ExpiresAt` has passed |
| `TokenAlreadyUsed` | The row exists and `Used` is set |

Purging on expiry alone collapses the second and third into the first. A person following a link
from an old email would be told their token never existed, rather than that it ran out — a worse
answer, and a misleading one. The grace period keeps the truthful answer available for as long as
anyone plausibly still holds the link, and no longer.

Seven days is the default because the tokens themselves live in minutes or hours, so a week is
comfortably longer than any link is useful and far shorter than any purpose could justify keeping
them. It is configurable, and it cannot be set to zero or a negative value — the options type
refuses both and falls back to the default.

The purge selects on `ExpiresAt <= now - grace`. That single condition covers a used row and an
expired-unused row alike, and it is the only condition applicable to all three tables: neither token
table records *when* it was consumed, and only `TWO_FACTOR_EMAIL_CODE` carries a `CreatedAt`. Expiry
is a sound proxy in any case, since each row is issued with a lifetime measured in minutes.

**A live token is never at risk.** The cutoff is strictly in the past, so a token that has not yet
expired cannot match however the grace period is configured.

## 6. How the purge runs

A hosted service (`TokenRetentionService`) dispatches `PurgeExpiredTokensCommand` on an interval,
hourly by default. Three properties are worth stating, because each is a decision rather than an
implementation detail.

**It is bounded.** Each run removes at most `PurgeBatchSize` rows from any one table, oldest first.
SRD §6.3.2 measured the write path degrading with the size of the table under sustained insert
pressure, and a purge is sustained *delete* pressure on tables the login and recovery paths write
to. A backlog drains over several runs instead of one long transaction competing with live traffic.

**It needs no coordination between instances.** NFR-06 requires that no instance assume it is alone,
and every instance runs its own copy. Nothing is claimed or locked: a run selects a batch, and the
delete re-reads those ids and removes whatever still exists, so an instance whose rows another
already removed simply deletes fewer than it selected and reports the true count. Deleting an
already-deleted row is the only concurrent outcome this pass can have, and it is harmless. The
alternative — electing one instance to purge — fails in the worse direction, because an instance
that assumed another was purging would stop purging the moment it ran alone.

**It is audited.** The command is registered like every other, so each run writes one audit entry
(NFR-09). The entry is as much the point as the deletion: it is the evidence that the schedule was
enforced, and when. The run is dispatched by a scheduler rather than a caller, so it is recorded as
an anonymous write.

A run that removes nothing is a success, not a failure — most runs find nothing to do, and treating
a no-op as an error would fill the trail with refusals that never happened.

The first tick falls one whole interval after start-up, which keeps the purge clear of migrations
and seeding, and means a container restarting repeatedly never turns start-up into delete pressure.
A failed run is logged and the loop continues: the pass is maintenance, not part of serving a
request, and the next tick retries whatever was missed, because a row past its retention period
stays past it.

## 7. The audit trail: 18 months identifiable, then pseudonymised and kept

`AUDIT_LOG` is where two obligations pull hardest against each other. Accountability (GDPR Art.
5(2), Art. 32; LGPD Art. 37) is why the trail exists, why it is append-only, and why
`ActorPersonId` is a bare `PublicId` rather than a foreign key — so an entry survives a hard-deleted
person. Storage limitation says an indefinitely retained record naming a person who has been erased
is personal data processed with no remaining basis.

The period settles the first half of that, and the mechanism settles the second.

**Eighteen months identifiable.** Most security baselines land on twelve — PCI DSS requires a year
of audit history — and breach discovery lag routinely exceeds it, so twelve is a floor rather than a
comfortable answer. Eighteen covers a full compliance cycle plus that lag. Beyond it, the
accountability value of *who acted* falls away sharply while the value of *what happened, when, and
how often* does not — and that half survives pseudonymisation intact.

**Then pseudonymised, and kept indefinitely.** Once `ActorPersonId` no longer resolves to a natural
person the entry is not personal data (Recital 26, LGPD Art. 12), so storage limitation stops
applying to it and the trail keeps its shape, ordering and correlation for as long as it is useful.

**No row is updated or deleted to achieve that.** The pseudonym derives from a per-subject key, and
it is the *key* that is destroyed — crypto-shredding. The append-only triggers installed by
`20260817113453_MakeAuditLogAppendOnly` stay exactly as they are, and Threat Model TH-18's tests
keep passing unchanged. A design that dropped or worked around those triggers would trade a closed
threat for a compliance fix, which is not a trade this document is asking for.

**Erasure does not wait for the clock.** When a data subject is erased under §4, their entries are
pseudonymised immediately — the same mechanism, triggered by the erasure rather than by eighteen
months elapsing.

One consequence worth stating plainly rather than discovering later: an **active** person's entries
are pseudonymised at eighteen months while they are still an active person. That caps how far back a
current account can be investigated. It is the intended trade-off of storage limitation, and it is
recorded here so it is a decision rather than a surprise.

## 8. Application logs: why 12 months, and on what condition

Twelve months rather than the ninety days an operational-debugging period would justify, because
debugging is not what these logs are for. The audit trail is the record of what the API *did*; the
application logs are the telemetry a breach is detected from, and
[#105](https://github.com/artur-rios/heimdall-api/issues/105) makes that explicit by reading them.

Breach discovery is routinely measured in months rather than weeks. A ninety-day period would
therefore delete the evidence of an incident before anyone knew to look for it, which is the one
outcome a security log must not have — and GDPR Art. 33's seventy-two-hour clock starts at
*awareness*, so a log that expired before awareness never contributed to meeting it.

**The condition.** Twelve months is defensible for logs that identify a person only by `PublicId`.
It is not defensible for the logs as they stand, which carry raw email addresses:
`MailgunSender` writes the recipient on every verification, reset and 2FA email, and
`DatabaseSeeder` writes the master administrator's address at every start-up. Quadrupling the life
of a file full of addresses is a larger exposure than the shorter period it replaces, not a smaller
one.

So the two halves of [#98](https://github.com/artur-rios/heimdall-api/issues/98) are ordered, and
the order is not negotiable: **the redaction lands before, or in the same change as, the retention
limit.** Enforcing the limit first would mean the first thing this schedule achieved for logs was a
longer life for identifiable data. Until the redaction lands, ninety days is the period that
applies.

One implementation note, because it is the reason the row reads "not enforced" rather than
"unbounded by oversight": the Serilog sink is wrapped in `WriteTo.Map` keyed per month, which
creates a *new sink per month*. A `retainedFileCountLimit` bounds files within one sink, so it
would bound each month's directory and never remove a month. Whatever #98 does has to survive that
wrapper.

## 9. Database backups: why the period is bounded, not chosen

A backup is a full copy of every category in §3, so it inherits all of their limits at once. It also
creates the one failure that makes every other row in this document theoretical:

> **An erasure that the next restore silently undoes is not an erasure.**

Backups are not edited. Editing them destroys the integrity that is their entire purpose, and no
backup regime worth running permits it. That leaves exactly two workable answers, and
[#106](https://github.com/artur-rios/heimdall-api/issues/106) has to pick one and implement it:

1. **Backup retention shorter than the shortest erasure deadline**, which §4 now fixes at **30
   days**. An erased record cannot survive in a backup past the deadline, because the backup holding
   it is gone first. Simple, and it needs no reconciliation — but it caps disaster recovery at 30
   days, which for most operators is far too short.
2. **A restore re-applies every erasure completed since the backup was taken.** Keeps the backups as
   long as recovery needs, at the cost of a step that must run on every restore and must be tested
   like any other part of the recovery procedure.

The choice determines the period rather than following from it, which is why this row carries no
number of its own. Note the direction of the constraint: a regime keeping backups longer than 30
days — which is nearly every regime — makes the re-application step **mandatory, not optional**.
Choosing (1) by default and discovering later that recovery needs ninety days is how an erasure
quietly comes back.

Backup encryption belongs to the same issue and is not restated here.

## 10. Configuration

| Variable | Default | Accepted range | Meaning |
| --- | --- | --- | --- |
| `HEIMDALL_RETENTION_TOKEN_GRACE_DAYS` | `7` | more than 0, up to 3650 | Days a single-use token is kept past expiry |
| `HEIMDALL_RETENTION_PURGE_INTERVAL_MINUTES` | `60` | 1 to 10080 (7 days) | Interval between purge runs |
| `HEIMDALL_RETENTION_PURGE_BATCH_SIZE` | `500` | any positive integer | Most rows removed from one table per run |
| `HEIMDALL_RETENTION_PURGE_ENABLED` | `true` | `true` / `false` | Set `false` to stop scheduling the purge |
| `HEIMDALL_RETENTION_ERASURE_DEADLINE_DAYS` | `30` | more than 0, up to 30 | Days before a requested erasure is anonymised |
| `HEIMDALL_RETENTION_DELETION_WINDOW_DAYS` | `90` | more than 0, up to 730 | Days before an administrative deletion is anonymised |
| `HEIMDALL_RETENTION_ANONYMISATION_ENABLED` | `true` | `true` / `false` | Set `false` to stop scheduling the anonymisation |

The erasure deadline is the one setting with a ceiling that is not a sanity check: 30 days is the
statutory limit, so a larger value is not a policy choice but a compliance failure, and it is
refused rather than applied.

The other ranges are enforced too, and each bound is there for a reason. A grace period beyond ten years is a
stray unit rather than a policy. An interval below a minute turns the purge into continuous delete
pressure on the tables the login path writes to; one above seven days is past what `PeriodicTimer`
accepts, and the exception it would throw inside the hosted service stops the host by default — so
without the ceiling, a stray digit in a retention setting would take the API down.

Every value has a default that is safe to run with, because a deployment that sets none of these
must still enforce a period: an unset variable meaning "keep forever" is precisely the state NFR-19
exists to end.

A malformed or out-of-range value falls back to the default and is named in a start-up warning,
rather than failing start-up. Refusing to start would turn a typo in a retention setting into an
outage of the identity provider every client system authenticates against — a far worse failure than
running on the documented default for one interval. Falling back *silently* would be worse than
either, since an operator would believe a setting was applied, so the warning names each variable it
ignored.

Switching the purge off is a deliberate deployment decision and is logged as a warning, because it
leaves personal data in place past its retention period.

## 11. Reviewing this document

The schedule is reviewed when:

- a migration adds or removes a column holding personal data;
- a new category of data subject or recipient appears;
- one of the "not yet enforced" rows is implemented, at which point its row states the period and
  the mechanism and drops the marker;
- the record of processing activities is reviewed, since GDPR Art. 30(1)(f) requires the periods to
  appear there too;
- a new store outside the schema starts holding personal data, or an existing one changes what it
  holds — §8's twelve months rests on the logs no longer carrying addresses, and a change that put
  them back would invalidate the period without touching a single table;
- the backup or recovery regime changes, since §9's two answers trade against the recovery window
  and a longer window can turn the reconciliation step from optional into mandatory.

