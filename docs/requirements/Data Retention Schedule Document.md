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

Every table in the `heimdall` schema that holds personal data, meaning data relating to an
identified or identifiable natural person (GDPR Art. 4(1), LGPD Art. 5 I).

`APPLICATION` is out of scope: an application is a non-human identity representing another system.
Its `OwnerId` points at a person, but the row describes the system, not the owner, and the owner's
own record is covered below.

## 3. The schedule

| Category | Table(s) | Retention period | Why that period | Enforced |
| --- | --- | --- | --- | --- |
| Active identity | `PERSON`, `GOOGLE_USER` | Life of the account | Performance of the service the account exists for. An identity provider cannot authenticate an identity it has deleted | By deletion (UC-09/UC-10, UC-28/UC-29) |
| Logically deleted identity | `PERSON`, `GOOGLE_USER` where `is_deleted` | **Not yet decided** | A soft delete is a restriction of processing, not an erasure; it needs a terminal state and has none | ❌ Not enforced — [#92](https://github.com/artur-rios/heimdall-api/issues/92) |
| Authentication material | `PERSON.password_hash`, `PERSON.salt`, `TWO_FACTOR_AUTH`, `TWO_FACTOR_RECOVERY_CODE` | Life of the account | A security measure under GDPR Art. 32; it lives and dies with the credential it protects. Removed by the cascades of NFR-11 and the `ON DELETE CASCADE` foreign keys | By deletion cascade |
| Single-use tokens | `PASSWORD_RESET_TOKEN`, `EMAIL_VERIFICATION_TOKEN`, `TWO_FACTOR_EMAIL_CODE` | Expiry **+ 7 days** (default, configurable) | §4 | ✅ Scheduled purge (§5) |
| Audit trail | `AUDIT_LOG` | **Not yet decided** | Accountability (GDPR Art. 5(2)) against storage limitation, complicated by the append-only triggers | ❌ Not enforced — [#97](https://github.com/artur-rios/heimdall-api/issues/97) |
| Application logs | Serilog file sink | **Not yet decided** | Operational necessity; currently unbounded, and the files contain email addresses | ❌ Not enforced — [#98](https://github.com/artur-rios/heimdall-api/issues/98) |
| Network data | Rate limiter partition key (`RemoteIpAddress`) | The fixed window, in memory only | An IP address is personal data under both laws. It is never persisted and never logged; the window is one minute and the key is discarded with it | By construction |
| Data Protection key ring | `DATA_PROTECTION_KEYS` | Life of the encrypted material | Not personal data itself, but the TOTP secrets of NFR-16 are undecryptable without it. Listed so nobody purges it as housekeeping | Never purged, deliberately |

Three rows read "not yet decided" rather than carrying a number chosen here. Each is a decision for
the controller, not for this document, and each has an issue where the decision and the mechanism
are worked out together — a period fixed here while the mechanism is unresolved would be a promise
this repository cannot keep.

## 4. Single-use tokens: why the period is expiry plus a grace period

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

## 5. How the purge runs

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

## 6. Configuration

| Variable | Default | Accepted range | Meaning |
| --- | --- | --- | --- |
| `HEIMDALL_RETENTION_TOKEN_GRACE_DAYS` | `7` | more than 0, up to 3650 | Days a single-use token is kept past expiry |
| `HEIMDALL_RETENTION_PURGE_INTERVAL_MINUTES` | `60` | 1 to 10080 (7 days) | Interval between purge runs |
| `HEIMDALL_RETENTION_PURGE_BATCH_SIZE` | `500` | any positive integer | Most rows removed from one table per run |
| `HEIMDALL_RETENTION_PURGE_ENABLED` | `true` | `true` / `false` | Set `false` to stop scheduling the purge |

The ranges are enforced, and each bound is there for a reason. A grace period beyond ten years is a
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

## 7. Reviewing this document

The schedule is reviewed when:

- a migration adds or removes a column holding personal data;
- a new category of data subject or recipient appears;
- one of the "not yet enforced" rows is implemented, at which point its row states the period and
  the mechanism and drops the marker;
- the record of processing activities is reviewed, since GDPR Art. 30(1)(f) requires the periods to
  appear there too.

