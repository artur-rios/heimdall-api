---
title: "Data Protection Document"
linkTitle: "Data Protection Document"
weight: 46
description: "Who controls what, on what basis, shared with whom, and across which borders — the record LGPD Art. 37 and GDPR Art. 30 require."
---

# Data Protection Document — Heimdall API

## 1. Purpose

This is the **record of processing activities** LGPD Art. 37 and GDPR Art. 30 require, together with
the two things that record depends on and that nothing else in this repository states: who controls
the data, and where it goes.

It is kept here rather than in a filing cabinet because the schema is the source of truth and the
schema changes. Keeping the record next to the requirements means it is reviewed in the same pull
request as the migration that outdates it.

Retention periods are **not** repeated here. They live in the
[Data Retention Schedule](Data%20Retention%20Schedule%20Document.md), which Art. 30(1)(f) is
satisfied by reference to.

## 2. Controller and Encarregado

| | |
| --- | --- |
| **Controller** | Artur Rios |
| **Contact** | <arturdev@duck.com> |
| **Encarregado / DPO** | Artur Rios — <arturdev@duck.com> |
| **Hosting region** | Brazil |

LGPD Art. 41 §1 requires the Encarregado's identity and contact to be publicly disclosed. That is
what §2 of the [Privacy Notice](Privacy%20Notice.md) does, and this row is its source.

The controller and the Encarregado are the same person. That is permitted, and for a project of this
size it is the honest arrangement rather than an invented separation — but it is worth naming the
consequence: there is no independent check on the controller's own decisions. Where the project
takes on a client whose scale makes that unacceptable, the Encarregado should be somebody else.

> **On small processing agents.** ANPD Resolution CD/ANPD nº 2/2022 gives *agentes de tratamento de
> pequeno porte* a simplified regime — notably, appointing an Encarregado is optional provided a
> communication channel is published. This project appoints one anyway, because the channel has to
> exist either way and naming a person is clearer than naming an inbox. Whether the project qualifies
> as a small processing agent is a determination for the controller, and it does not change anything
> in this document.

## 3. Controller and processor, per scope

Heimdall is multi-tenant. Each `Scope` belongs to a different client system, and that client decides
why its users' data is processed. The allocation follows from that and has to be stated, because
almost every downstream obligation depends on which side of it a given row falls.

| Data | Role | Why |
| --- | --- | --- |
| Persons and Google Users **within a tenant scope** | Heimdall is **processor**; the tenant organisation is **controller** | The tenant decides the purpose — its own users, its own service. Heimdall stores and authenticates them on the tenant's instructions |
| System Admin accounts | Heimdall is **controller** | They exist to operate Heimdall itself; no tenant determines their purpose |
| Audit trail (`AUDIT_LOG`) | Heimdall is **controller** | Kept for Heimdall's own accountability under LGPD Art. 37 and GDPR Art. 5(2). A processor may hold records it needs to demonstrate its own compliance, and this is one |
| Application logs, security telemetry | Heimdall is **controller** | Same reasoning: operating and defending the service is Heimdall's own purpose, not a tenant's instruction |

Two consequences that are easy to miss:

- **A data subject request from a tenant's user reaches a processor, not their controller.** Heimdall
  answers it under UC-41/UC-42 where it can, and the Data Processing Agreement sets out when and how
  it must instead be routed to the tenant.
- **Heimdall must notify the tenant of a breach affecting their scope**, without undue delay (GDPR
  Art. 33(2), LGPD Art. 48). That obligation is owed to the tenant, not the public.

## 4. Categories of data subject

| Category | Where |
| --- | --- |
| Persons — `User`, `ScopeAdmin`, `SystemAdmin` | `PERSON` |
| Google-authenticated users | `GOOGLE_USER` |

`APPLICATION` rows are out of scope: an application is a non-human identity representing another
system. Its `OwnerId` names a person, but the row describes the system.

## 5. Categories of personal data, purposes, and legal basis

| Category | Fields | Purpose | LGPD basis | GDPR basis |
| --- | --- | --- | --- | --- |
| Identity | `Name`, `Email` | Identify the account and address it | Art. 7 V (execução de contrato) | Art. 6(1)(b) contract |
| Authentication material | `PasswordHash`, `Salt`, `TwoFactorAuth.TotpSecretEncrypted`, recovery code hashes | Verify that the account holder is present | Art. 7 V, Art. 46 (segurança) | Art. 6(1)(b), Art. 32 |
| Third-party identifiers | `GoogleUser.GoogleId`, `ProfilePictureUrl` | Resolve a Google sign-in to a stored identity | Art. 7 V | Art. 6(1)(b) |
| Behavioural / security | `FailedLoginAttempts`, `LockedOutUntil`, `AUDIT_LOG` | Defend accounts against brute force; demonstrate what the system did | Art. 7 IX (legítimo interesse), Art. 37 | Art. 6(1)(f) legitimate interests, Art. 5(2) |
| Deletion and erasure state | `DeletedAt`, `DeletionKind`, `AnonymisedAt`, `ErasureRequestedAt`, `ErasureDueAt`, `ErasureBlockedReason` | Honour erasure within the statutory deadline and show that it was honoured | Art. 7 II (obrigação legal) | Art. 6(1)(c) legal obligation |
| Network | Rate limiter partition key (`RemoteIpAddress`) | Bound anonymous request rates | Art. 7 IX | Art. 6(1)(f) |
| Delivery | Recipient address passed to Mailgun | Send verification, reset and second-factor emails | Art. 7 V | Art. 6(1)(b) |

**Consent is not a basis anywhere in this table**, and that is deliberate. Every processing operation
here is necessary to run an identity service the account holder is a party to, or is required of the
controller by law. Consent would be the wrong basis for all of it: it is withdrawable, and an
identity provider that stopped verifying passwords on withdrawal would simply be broken. Recording
which basis applies to a given identity at creation is [#94](https://github.com/artur-rios/heimdall-api/issues/94).

No special categories of personal data (GDPR Art. 9, LGPD Art. 5 II) are processed. There is no
automated decision-making producing legal effects (GDPR Art. 22, LGPD Art. 20).

## 6. Recipients

| Recipient | What reaches them | Why |
| --- | --- | --- |
| **Mailgun** (Sinch) | Recipient email address, message body | Delivering verification, password reset and second-factor emails |
| **Google** | The ID token presented by the caller | Verifying a Google sign-in; Google is the source of `GoogleId`, `Email`, `Name`, `ProfilePictureUrl` |
| **Tenant scope organisations** | The identities within their own scope | They are the controller for those; see §3 |
| **Hosting provider** | Everything, at rest | Running the database and the API |

The current sub-processor list is maintained in the
[Data Processing Agreement](Data%20Processing%20Agreement.md) §7, which is also where the
notification commitment for changing it lives.

## 7. International transfers

This is the section the deployment facts make non-trivial, and it contains one finding that needs a
decision.

| Leg | From | To | Governed by | Status |
| --- | --- | --- | --- | --- |
| Email delivery | Brazil | United States (Mailgun) | LGPD Art. 33 | ⚠️ Mechanism required — see §7.1 |
| Google ID token verification | Brazil | United States (Google) | LGPD Art. 33 | ⚠️ Mechanism required — see §7.1 |
| Serving EU data subjects, if any | EEA | Brazil (hosting) | GDPR Art. 44–49 | ⚠️ See §7.2 — **Brazil holds no EU adequacy decision** |

### 7.1 Brazil → United States

Both outbound flows leave Brazil. LGPD Art. 33 permits an international transfer only on one of its
listed grounds; the workable one here is Art. 33 II — *cláusulas-padrão contratuais* — for which the
ANPD approved standard clauses in **Resolution CD/ANPD nº 19 of 23 August 2024**.

Required, and not yet done:

1. Execute the ANPD standard contractual clauses, or verify that the provider's own terms already
   incorporate them, with **Mailgun** and with **Google**.
2. Record the outcome in this section with the date and the version of the clauses relied on.

Mailgun's US region is a **configuration choice**, not a given: Mailgun also offers an EU region.
Moving to it would not remove the transfer — the data still leaves Brazil — so the choice is about
which second jurisdiction is involved, and the US is the one this deployment has picked. That choice
is recorded in `docs/content/en/docs/operations.md` next to `MAILGUN_API_KEY` so it is made
deliberately rather than inherited.

### 7.2 The EEA question, which the controller must answer

**Brazil has no adequacy decision from the European Commission.** If Heimdall processes the data of
people in the EEA, hosting in Brazil is itself a restricted transfer under GDPR Chapter V, and
requires Art. 46 safeguards — Standard Contractual Clauses plus a transfer impact assessment — before
it is lawful.

Whether that applies is a question about the service, not the code: GDPR Art. 3(2) reaches a
controller outside the EU only where it offers goods or services to people in the EU, or monitors
their behaviour. **The controller must determine and record whether any tenant scope serves EEA data
subjects.**

- **If no**, GDPR does not apply, this document's GDPR columns are informative rather than binding,
  and §7.2 closes with that determination recorded and a date.
- **If yes**, SCCs and a transfer impact assessment are required for the EEA → Brazil leg, and the
  onward Brazil → US legs in §7.1 must be covered too.

This is written as an open question rather than an assumption because guessing either way would be
worse than asking: assuming "no" understates an obligation, and assuming "yes" would have this
repository claim safeguards it does not have.

## 8. Security measures

Not restated here. LGPD Art. 46 and GDPR Art. 32 are addressed by the controls the
[Threat Model](Threat%20Model%20Document.md) documents and NFR-02, NFR-03, NFR-13, NFR-15, NFR-16
and NFR-17 require. What that model deliberately does not cover — encryption at rest, backups, and
breach detection — is
[#106](https://github.com/artur-rios/heimdall-api/issues/106) and
[#105](https://github.com/artur-rios/heimdall-api/issues/105).

## 9. Data subject rights

| Right | LGPD | GDPR | Where |
| --- | --- | --- | --- |
| Confirmation and access | Art. 18 I–II | Art. 15 | ⚠️ [#90](https://github.com/artur-rios/heimdall-api/issues/90) |
| Portability | Art. 18 V | Art. 20 | ⚠️ [#90](https://github.com/artur-rios/heimdall-api/issues/90) |
| Correction | Art. 18 III | Art. 16 | ✅ UC-08, self-service |
| Erasure / elimination | Art. 18 VI | Art. 17 | ✅ UC-42, completed by NFR-20 |
| Restriction of processing | Art. 18 IV | Art. 18 | ⚠️ [#93](https://github.com/artur-rios/heimdall-api/issues/93) |
| Information about sharing | Art. 18 VII | Art. 15(1)(c) | §6 of this document; in the export at [#90](https://github.com/artur-rios/heimdall-api/issues/90) |
| Objection | Art. 18 § | Art. 21 | ⚠️ [#93](https://github.com/artur-rios/heimdall-api/issues/93) |

Deadlines: **LGPD Art. 19 §2 — 15 days** for confirmation and access; **GDPR Art. 12(3) — one
month**. The shorter one governs where both apply.

## 10. Reviewing this document

Reviewed when a migration adds or removes a column holding personal data; when a recipient or
sub-processor changes; when the hosting region changes; when a transfer mechanism is executed or
lapses; and when §7.2's EEA determination is made or changes.
