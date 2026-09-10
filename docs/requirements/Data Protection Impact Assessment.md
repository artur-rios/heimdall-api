---
title: "Data Protection Impact Assessment"
linkTitle: "Data Protection Impact Assessment"
weight: 44
description: "The risks this system creates for the people in it, and what is done about them — GDPR Art. 35, LGPD Art. 38."
---

# Data Protection Impact Assessment — Heimdall API

**Version 1.2 — 10 September 2026** · Assessed by Artur Rios, controller and Encarregado

| Version | Date | Change |
| --- | --- | --- |
| 1.2 | 10 September 2026 | GDPR confirmed to apply. R-01 re-rated Medium → Low on confirmation of encryption at rest, for the volume and the backups. EEA hosting resolved: direct sign-up is not a Chapter V transfer, and an EEA tenant's is covered by SCCs now incorporated in the DPA |
| 1.1 | 10 September 2026 | R-07 re-rated High → Low: the Google transfer was found not to exist, and the email transfer's ground established under LGPD Art. 33 IX |
| 1.0 | 9 September 2026 | First assessment |

## 1. Why this exists, and why the Threat Model is not it

GDPR Art. 35 requires a DPIA before processing likely to result in a high risk to data subjects,
expressly including large-scale processing and systematic monitoring. LGPD Art. 38 gives the ANPD
power to require a *Relatório de Impacto à Proteção de Dados*, and its guidance treats identity
platforms serving multiple organisations as within scope.

This repository already has a good [Threat Model](Threat%20Model%20Document.md), and it is the wrong
instrument for this obligation. The difference is not depth but direction:

| | Threat Model | This document |
| --- | --- | --- |
| Asks | What can an attacker do to the system? | What can the system do to a person? |
| Organised by | Trust boundary | Risk to a data subject |
| A control working as designed is | Not a threat | Sometimes exactly the risk |

That last row is the reason both are needed. A lockout is a control functioning correctly and a
denial of a person's access to every system they use; the threat model is right not to list it, and
this document would be negligent to omit it.

## 2. The processing, described

Heimdall is a centralised identity provider. One database holds the credentials, addresses,
second-factor secrets and complete action history of every user of every client system that adopts
it. Each `Scope` is a tenant, usually the controller for its own users, with Heimdall as processor
(Data Protection Document §3).

**The concentration is the product.** The vision document offers a single source of truth for
identity across many systems, and that is genuinely valuable — and it is also precisely what makes
the risk high. A compromise here is not a compromise of one product; it is a compromise of every
tenant at once, and of every person in them.

The full description — categories of subject, categories of data, purposes, legal bases, recipients,
transfers, retention — is the
[Data Protection Document](Data%20Protection%20Document.md), and is not repeated here. What follows
assesses it.

## 3. Necessity and proportionality

| Test | Assessment |
| --- | --- |
| **Lawful basis** | Contract performance for identity and credentials; legitimate interests for security data; legal obligation for erasure records. Recorded per identity since NFR-23. Consent is used for nothing and is not selectable, because it would be withdrawable and an identity provider that stopped verifying passwords on withdrawal would be broken rather than compliant |
| **Purpose limitation** | Every column has a stated purpose (Data Protection Document §5.1), enforced by a test that fails the build when a new one has none |
| **Data minimisation** | Reviewed. One field — `GoogleUser.ProfilePictureUrl` — is recorded as the weakest justification in the system and marked for removal if no client reads it |
| **Storage limitation** | Every category has a period; all but the backup regime are enforced by scheduled passes |
| **Alternatives considered** | Federating to each tenant's own directory would remove the concentration and the product with it. Not holding password hashes would mean not being an identity provider. The concentration is inherent to the purpose rather than incidental to the design |

Proportionate, on the condition that the risks in §4 are held where they are. The assessment is not
"this is safe" but "this is worth doing, given these controls, and would not be without them."

## 4. Risks to data subjects

Rated for the **person**, not the system. Likelihood and severity are the assessor's judgement.

### R-01 · One compromise exposes every tenant's users at once

| | |
| --- | --- |
| **Risk to the person** | Credentials, addresses and a full action history disclosed. Because the same identity spans every client system, a single disclosure follows them everywhere they use it |
| **Severity** | High · **Likelihood** Low · **Residual** **Low** |

Controls: Argon2id with per-person salts, TOTP secrets encrypted at rest, tokens stored as digests,
scope isolation re-read per request, `PublicId` values only across the boundary — and, confirmed by
the controller on 10 September 2026, **encryption at rest for the database volume**. That was the
gap that held this at Medium: everything not protected at the application level sits in clear text
within the database, and only the volume beneath it stands between a stolen disk and all of it.

Backup encryption was confirmed separately on the same date, so the copy of the database that lives
outside the server is protected too.

**Remaining:** *where* the backup service holds them. A backup is a full copy of every category in
the record, so if it leaves Brazil the exposure is not a single address in transit but the whole
database in another jurisdiction. That is a transfer question rather than a security one, and it is
tracked in [#125](https://github.com/artur-rios/heimdall-api/issues/125).

*This is the risk the product's value creates. It cannot be designed out without abandoning the
purpose, which is why the controls around it carry more weight than they would elsewhere.*

### R-02 · Lockout denies a person access to everything at once

| | |
| --- | --- |
| **Risk to the person** | Ten wrong passwords locks the account for fifteen minutes — and with it every client system that authenticates through it. Somebody targeted, or simply unlucky, loses access to their working life rather than to one product |
| **Severity** | Medium · **Likelihood** Medium · **Residual** Medium |

Controls: the window is short and self-clearing; the per-IP limiter makes targeting expensive;
`LOCKOUT_SPIKE` (NFR-26) now surfaces mass lockout.

**This is a control working exactly as designed, and it is still a risk to the person.** The threat
model is right not to list it — it is not something an attacker does to the system — and that is
the clearest illustration of why this document exists. Accepted: the alternative is weakening the
brute-force defence, which harms subjects more.

### R-03 · The audit trail is a behavioural record

| | |
| --- | --- |
| **Risk to the person** | Every action they take, timestamped, in an append-only table nothing could remove from. Over years that is a detailed record of when a person works, from where, and how often |
| **Severity** | Medium · **Likelihood** High (it is inherent) · **Residual** Low |

Controls: attribution cleared at 18 months, or immediately on erasure (NFR-21), enforced by a
database trigger the application cannot talk its way around. Once cleared the entry relates to no
identifiable person.

*Consequence recorded rather than discovered: an active person's entries are pseudonymised at 18
months while they are still active, capping how far back a current account can be investigated. That
is the intended trade-off.*

### R-04 · Erasure that does not erase

| | |
| --- | --- |
| **Risk to the person** | Asking to be forgotten and not being. Before this year's work, logical deletion kept name, address and password hash indefinitely, and nothing purged them |
| **Severity** | High · **Likelihood** Low · **Residual** Low |

Controls: anonymisation on a schedule (NFR-20), self-service request (UC-42), audit attribution
cleared with it (NFR-21), restore reconciled against the erasure ledger (NFR-25). **Gap:** the
reconciliation is implemented and tested but has never run against a real restore; the workflow's
quarterly review exists to change that.

### R-05 · A tenant administrator sees more than the relationship warrants

| | |
| --- | --- |
| **Risk to the person** | A Scope Admin reads the identities in their scope. Where the tenant is an employer, that is an employment relationship with an inherent power imbalance |
| **Severity** | Medium · **Likelihood** Medium · **Residual** Medium |

Controls: scope isolation; minimal projections in listings; erasure requests visible only to a System
Admin, so an administrator does not learn which of their users asked to leave; restricted identities
withheld from tenant listings (NFR-24).

*Not fully mitigable here. What a tenant may do with its own users is the tenant's lawfulness to
establish, which is what the [DPA](Data%20Processing%20Agreement.md) exists to bind.*

### R-06 · The subject cannot exercise rights without the controller's cooperation

| | |
| --- | --- |
| **Risk to the person** | Rights that exist on paper and require an administrator's goodwill in practice |
| **Severity** | Medium · **Likelihood** Was High · **Residual** Low |

Controls: access and portability (UC-41), erasure (UC-42), restriction and objection (UC-44), all
self-service and none requiring an administrator. Blocked erasures are surfaced with deadlines
(UC-43) rather than sitting unnoticed.

### R-07 · Data crosses borders

| | |
| --- | --- |
| **Risk to the person** | Their address reaches the United States on every transactional email, into a jurisdiction whose protections differ from Brazil's |
| **Severity** | Medium · **Likelihood** High (it happens on every send) · **Residual** **Low** |

Controls: LGPD **Art. 33 IX resting on Art. 7 V** — the transfer is necessary to perform the service
the person is party to, since the email that activates or restores their account *is* the service
being performed. Where GDPR applies, Sinch's Data Processing Agreement incorporates the EU Standard
Contractual Clauses automatically, with no separate signature.

**This was the highest residual risk in version 1.0, rated High, and it has come down for two
reasons — one a correction and one a decision.**

The correction: Google was recorded as a second transfer and is not one. The ID token is validated
offline against cached public certificates and is never sent to Google, so there is nothing to
transfer. Google is a *source* of personal data. That removed half the exposure by establishing it
had never existed.

The decision: the controller declined to pursue new contractual arrangements, which closed the Art.
33 II route through the ANPD's own clauses. Art. 33 IX is a self-standing statutory ground requiring
no counterparty agreement, and it rests on the same Art. 7 V basis §5 of the record already records
for the underlying processing — which is what makes it coherent rather than a ground found for the
occasion.

*Residual is Low rather than absent because Art. 33 IX is a necessity test, and necessity is a
judgement that could be read more narrowly than it is here. If the ANPD recognises the EU SCCs as
equivalent, Art. 33 II becomes available through Sinch's existing DPA with nothing to sign, and is
the stronger ground.*

The EEA → Brazil question in R-07's original framing remains open and is tracked separately: Brazil
holds no EU adequacy decision, so if any scope serves EEA subjects, the **hosting** is a restricted
transfer. See [#124](https://github.com/artur-rios/heimdall-api/issues/124).

### R-08 · A breach nobody notices

| | |
| --- | --- |
| **Risk to the person** | Not being told their data was exposed, because nobody realised. Both notification clocks run from awareness, and until NFR-26 nothing read the signals the system recorded |
| **Severity** | High · **Likelihood** Was Medium · **Residual** Medium |

Controls: signal detection (NFR-26), the [Incident Response Document](Incident%20Response%20Document.md),
the Art. 33(5) register.

**Gap, and it is the live one.** As at 10 September 2026 **nothing collects the signals**: they are
written to the log and no shipper, mail relay or pager reads them. The detection therefore notices
and tells nobody, which for the purpose of Art. 33's clock is indistinguishable from not detecting.
Closing it needs no new code — a scheduled grep that mails what it finds is enough — but it does
need somebody to receive the result.

## 5. Summary

| Risk | Residual |
| --- | --- |
| R-02 · Lockout as denial of access | Medium (accepted) |
| R-05 · Tenant administrator visibility | Medium |
| R-08 · Breach detection routing unconfirmed | Medium |
| R-01 · Concentration | Low |
| R-03 · Behavioural record | Low |
| R-04 · Erasure | Low |
| R-06 · Exercising rights | Low |
| R-07 · Cross-border email delivery | Low |

**No residual risk is rated High**, and Art. 36 prior consultation is not required.

**One action remains** ([#125](https://github.com/artur-rios/heimdall-api/issues/125)), now down to
two questions: **where the backup service holds the copies** — the only one that could change an
obligation rather than a practice — and **where `SECURITY_SIGNAL` lines are collected**, which is
R-08's residual Medium. Nothing collects them today, so the detection built for NFR-26 currently
notices things and tells nobody; that is the gap, not the detecting.

**What changed since version 1.0.** R-07 was the highest risk and is now Low. Half of it was a
factual error on my part — Google was recorded as a recipient when the ID token is validated offline
and never leaves the server — and the other half was resolved by relying on LGPD Art. 33 IX rather
than on contractual clauses the controller had decided not to pursue. Nothing was signed, and
nothing about the system changed; what changed was that the assessment now describes it accurately.

## 6. When this is revisited

On a new purpose, category of data, recipient or transfer; on a change of hosting region; after any
incident; when any of §5's actions completes; and otherwise on the cadence in the
[Development Workflow](Development%20Workflow%20Document.md). A DPIA describing a system that has
moved on is worse than none, because it is relied upon.
