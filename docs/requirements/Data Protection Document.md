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
identity provider that stopped verifying passwords on withdrawal would simply be broken.

It is also **not selectable**, not merely unused. A scope that declared consent would be recording a
basis this system cannot honour — GDPR Art. 7(3) requires withdrawal to be as easy as giving it, and
there is no withdrawal path. The validators refuse the value and the recorder refuses it again on
the way out, because a guard in one direction only is one migration from being bypassed.

**The basis is recorded per identity** at creation, with the privacy notice version then in force
(NFR-23). The version is stored rather than looked up later: what a person was told is a fact about
a moment, and reading the current notice would answer a different question. A scope may declare the
basis for identities within it, since the tenant is usually their controller (§3); where it declares
none, contract performance applies.

**Identities created before NFR-23 read `Unrecorded`.** Contract performance almost certainly applied
to all of them, and the migration deliberately did not write it: asserting in this record something
nobody checked for those rows would demonstrate that a migration ran, not that a basis applied.
They are visibly a thing to fix rather than silently indistinguishable from a row recorded properly.

No special categories of personal data (GDPR Art. 9, LGPD Art. 5 II) are processed. There is no
automated decision-making producing legal effects (GDPR Art. 22, LGPD Art. 20).

## 5.1 Every column, and why it is held

§5 groups the data by category. This is the same data at the granularity a minimisation review needs
(GDPR Art. 5(1)(c) and Art. 25, LGPD Art. 6 III): one row per column that holds personal data, each
with the purpose that justifies holding it. A column with no purpose is a column to remove.

### `PERSON`

| Column | Purpose |
| --- | --- |
| `PublicId` | The identifier every other table and every token refers to. Random, so it discloses nothing by itself |
| `Name` | Addressing the person in email, and downstream profile provisioning by client systems |
| `Email` | The address the account is reached at, and the identifier a person signs in with |
| `PasswordHash`, `Salt` | Verifying the person is present. Never reversible to the password |
| `IsDeleted` | Suspending an identity without destroying it |
| `EmailVerified` | Whether the address has been proven reachable (FR-EV-01) |
| `FailedLoginAttempts`, `LockedOutUntil` | Bounding credential guessing per account (FR-AU-09) |
| `RoleId`, `ScopeId` | Authorisation, and the per-scope uniqueness index FR-PE-09 needs |
| `CreatedAt`, `UpdatedAt` | Ordinary record keeping; `UpdatedAt` is why `DeletedAt` had to exist separately |
| `DeletedAt`, `DeletionKind`, `AnonymisedAt` | Measuring and enforcing the retention window (NFR-20) |
| `ErasureRequestedAt`, `ErasureDueAt`, `ErasureBlockedReason` | Honouring an erasure request within its deadline, and showing that it was honoured (UC-42, UC-43) |
| `LegalBasis`, `PrivacyNoticeVersion`, `BasisRecordedAt` | Demonstrating the lawful basis and what the person was told when the account was created (NFR-23). Not data *about* the person so much as about the processing of them, and held for the same accountability duty that requires this document |
| `ProcessingRestrictedAt`, `RestrictionGround`, `RestrictionLiftNotifiedAt` | Honouring a restriction of processing and evidencing that Art. 18(3)'s notification happened before it was lifted (NFR-24) |

### `GOOGLE_USER`

| Column | Purpose |
| --- | --- |
| `PublicId`, `ScopeId`, `IsDeleted`, `CreatedAt`, `UpdatedAt` | As above |
| `GoogleId` | Google's `sub`. The only stable way to resolve a returning sign-in to a stored identity (FR-GO-08) |
| `Name`, `Email`, `EmailVerified` | As on `PERSON` |
| `ProfilePictureUrl` | Downstream profile provisioning — see the decision below |
| `DeletedAt`, `DeletionKind`, `AnonymisedAt`, `ErasureRequestedAt`, `ErasureDueAt`, `ErasureBlockedReason`, `LegalBasis`, `PrivacyNoticeVersion`, `BasisRecordedAt`, `ProcessingRestrictedAt`, `RestrictionGround`, `RestrictionLiftNotifiedAt` | As above |

### `AUDIT_LOG`

| Column | Purpose |
| --- | --- |
| `ActorPersonId`, `ActorRole` | Attributing an action, for as long as NFR-21 permits and no longer |
| `Action`, `TargetId`, `Succeeded`, `FailureReason`, `CreatedAt`, `PublicId` | What happened. Not personal data once the attribution is cleared |

> **This table is why NFR-23's columns are here.** They were added after the table existed, and
> `PersonalDataPurposeTests` failed the build until each was given a purpose — which is the control
> working rather than a formality. `SCOPE.DefaultLegalBasis` and `SCOPE.PrivacyNoticeUri` are absent
> deliberately: a scope is an organisation, not a person, so neither is personal data.

### Two decisions this review settled

**`ProfilePictureUrl` stays, and it is the weakest case here.** The issue that prompted this review
proposed dropping it unless a consumer could be identified. One can: the same downstream
profile-provisioning purpose that puts `DisplayName` in the auth token. A client system rendering a
signed-in user wants a name and an avatar, and serving both from the identity provider is why the
identity provider holds them.

It is recorded as the weakest case because the reasoning is thinner than for any other column. It is
a URL to a photograph of a person, held for a convenience a client system could satisfy by asking
Google itself, and required by FR-GO-05 rather than by anything the API does with it. **If no client
system reads it, it should be dropped** — that is a question about deployments rather than about
code, and it belongs to the controller. Removing it would change FR-GO-05 and break the two Google
User read endpoints' contract, which is why this review does not do it unilaterally.

**The display name in the auth token is confirmed, with its consequence stated.** A JWT travels
further than a response body: client systems store it, put it in headers, and log it. Putting a name
in one spreads that name into places this system does not control and cannot clean up. The purpose —
letting a client provision a profile without a second call — is real, and the alternative of a
profile endpoint would mean every client making an extra request on every sign-in. The decision
stands; the consequence is recorded so that a future proposal to add anything *else* to the token is
weighed against the same test.

## 6. Recipients

| Recipient | What reaches them | Why |
| --- | --- | --- |
| **Mailgun** (Sinch) | Recipient email address, message body | Delivering verification, password reset and second-factor emails |
| **Tenant scope organisations** | The identities within their own scope | They are the controller for those; see §3 |
| **Hosting provider** | Everything, at rest | Running the database and the API |

The current sub-processor list is maintained in the
[Data Processing Agreement](Data%20Processing%20Agreement.md) §7, which is also where the
notification commitment for changing it lives.

### 6.1 Google is a source, not a recipient

An earlier version of this document listed Google as a recipient. **That was wrong**, and the
correction matters because it removes an international transfer that was recorded as needing a
mechanism.

`GoogleIdTokenVerifier` calls `GoogleJsonWebSignature.ValidateAsync`, which **validates the token
offline**: it verifies the signature against Google's published certificates, which the library
caches and refreshes hourly, and checks the issuer, audience and expiry locally. **The token is
never sent to Google.** The only outbound request is for Google's public certificates, which carry
no personal data.

The direction of travel is the opposite of what a recipient means. Google *issues* the token to the
person's browser, which passes it to a client system, which presents it here — so Google is where
`GoogleId`, `Email`, `Name` and `ProfilePictureUrl` **come from**, and GDPR Art. 14 and LGPD Art. 9
make that something the data subject is told. It is disclosed in the Privacy Notice and in every
UC-41 export's `sources`.

The person's own sign-in at Google is a separate matter, between them and Google under Google's own
terms. Enabling Google Sign-In for a scope makes it possible; it does not make Heimdall the party
transferring anything.

## 7. International transfers

| Leg | From | To | Governed by | Status |
| --- | --- | --- | --- | --- |
| Email delivery | Brazil | United States (Mailgun) | LGPD Art. 33 IX + Art. 7 V | ✅ Ground established — §7.1 |
| Email delivery, for EEA subjects if any | EEA | United States (Mailgun) | GDPR Art. 46(2)(c) | ✅ Sinch's SCCs — §7.1 |
| Direct sign-up by an EEA subject | — | Brazil (hosting) | Not a Chapter V transfer | ✅ No exporter — §7.2 |
| An EEA tenant's users | EEA (tenant) | Brazil (hosting) | GDPR Art. 46(2)(c) | ✅ SCCs Module Two in the DPA — §7.2 |
| ~~Google ID token verification~~ | — | — | — | **Not a transfer** — §6.1 |

Mailgun is the only outbound flow carrying personal data. The Google leg was recorded here in
error and is corrected in §6.1: the token is validated offline against cached public certificates
and is never sent to Google.

### 7.1 Email delivery to Mailgun

**LGPD: Art. 33 IX, resting on Art. 7 V.** Art. 33 IX permits a transfer *"quando necessário para
atender as hipóteses previstas nos incisos II, V e VI do art. 7º desta Lei"*, and Art. 7 V is
*"quando necessário para a execução de contrato ou de procedimentos preliminares relacionados a
contrato do qual seja parte o titular, a pedido do titular dos dados"*.

Every email this API sends is one of exactly three things: the verification that activates an
account somebody asked for, the reset that restores access to it, or the second factor that lets
them in. Each is necessary to perform the service the person is party to, and each is sent at their
own request. The address reaching a mail provider is not incidental to that service — it *is* the
service being performed.

This is the same basis §5 already records for the underlying processing, which is what makes it
coherent rather than opportunistic: the transfer rests on the ground the processing itself rests on,
not on a different one found for the occasion.

**Why not Art. 33 II.** The *cláusulas-padrão contratuais* route would require the ANPD's own
clauses (Resolution CD/ANPD nº 19 of 23 August 2024) in the Mailgun contract. Sinch's published DPA
is automatically incorporated and carries the **EU** Standard Contractual Clauses, with a Brazil
annex that references the LGPD — but not the ANPD clauses. The ANPD may recognise foreign clauses as
equivalent, and doing so requires a published Resolution of its Conselho Diretor; absent one for the
EU SCCs, Art. 33 II would rest on an equivalence nobody has declared. The transition period for
incorporating the ANPD clauses closed on **23 August 2025**.

Art. 33 IX is a self-standing statutory ground that needs no counterparty agreement, which is why it
is the one relied on.

**GDPR, if it applies at all (§7.2).** Sinch's Data Processing Agreement forms part of the services
agreement automatically, with no separate signature, and incorporates the EU Standard Contractual
Clauses (Commission Decision 2021/914/EU, Modules 2 and 3) with Sinch as data importer. The EEA → US
leg is therefore covered by Art. 46(2)(c) safeguards already in force.

**Nothing needs to be signed.** Neither ground requires negotiating or executing an agreement:
Art. 33 IX is statutory, and Sinch's SCCs are incorporated by their own terms.

**The Mailgun region remains a configuration choice.** Mailgun offers an EU region; moving to it
would not remove the transfer, since the data still leaves Brazil. It would change which second
jurisdiction is involved. Recorded next to `MAILGUN_API_KEY` in the operations guide so the choice
is made deliberately rather than inherited.

> **Review trigger.** If the ANPD publishes a Resolution recognising the EU SCCs as equivalent, Art.
> 33 II becomes available through Sinch's existing DPA with nothing to sign, and is the stronger
> ground. Worth checking when this document is next reviewed.

### 7.2 GDPR applies — determined 10 September 2026

**The controller has determined that the service is open to EEA data subjects.** Nothing restricts
access by geography once it is published, so GDPR is treated as applying under Art. 3(2). Recorded
here with its date, and revisited if the service's reach changes.

That settles the question this section was opened for. What follows is the consequence, and it is
narrower than it first appears.

#### Hosting in Brazil is not, by itself, a restricted transfer

Brazil holds no adequacy decision, so the instinct is that storing EEA subjects' data here needs
Art. 46 safeguards. For **the people who sign up directly**, it does not.

EDPB Guidelines 05/2021 set three cumulative criteria for a Chapter V transfer: an exporter subject
to the GDPR, disclosing to a separate importer, in a third country. The guidelines are explicit that
*"there will not be a transfer when the data are disclosed directly by the data subject to the
recipient as there will be no exporter"*. A person in the EEA creating an account here is sending
their own data; they are not an exporter, and the first criterion fails.

So GDPR applies to that processing in full under Art. 3(2) — every right, every obligation in this
document — but Chapter V does not engage, and no safeguard is required for the hosting itself.

#### A tenant established in the EEA *is* an exporter

Where a tenant organisation subject to the GDPR uses a scope for its own users, all three criteria
are met: the tenant is the exporter and controller, Heimdall is the importer and processor, and the
importer is in a third country. That is a restricted transfer and needs Art. 46 safeguards.

**The safeguard belongs in the [Data Processing Agreement](Data%20Processing%20Agreement.md)**,
which is Heimdall's own template rather than a third party's, so it requires no negotiation with
anybody: §7.1 of that agreement now incorporates the EU Standard Contractual Clauses, **Module Two
(controller to processor)**, with the tenant as exporter and Heimdall as importer. An EEA tenant
executing the DPA receives the safeguard by doing so.

A transfer impact assessment remains the exporting tenant's to make — it is an assessment of the
importer's jurisdiction from the exporter's position, and the exporter is the only party who can
make it. §7.2 of the DPA sets out what Heimdall supplies to support one.

#### Consequences elsewhere

- The **Brazil → United States** email flow (§7.1) is unaffected: Art. 33 IX carries it under the
  LGPD, and Sinch's own DPA carries the EU leg under Art. 46(2)(c).
- The [Incident Response Document](Incident%20Response%20Document.md) §4 no longer needs its
  provisional instruction to assume GDPR applies. It does apply, and the shorter of the two
  notification clocks governs.

> **The risks this processing creates for the people in it**, rated for them rather than for the
> system, are the [Data Protection Impact Assessment](Data%20Protection%20Impact%20Assessment.md).

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
| Confirmation and access | Art. 18 I–II | Art. 15 | ✅ UC-41, self-service |
| Portability | Art. 18 V | Art. 20 | ✅ UC-41 — structured JSON, machine-readable |
| Correction | Art. 18 III | Art. 16 | ✅ UC-08, self-service |
| Erasure / elimination | Art. 18 VI | Art. 17 | ✅ UC-42, completed by NFR-20 |
| Restriction of processing | Art. 18 IV | Art. 18 | ✅ UC-44, self-service |
| Information about sharing | Art. 18 VII | Art. 15(1)(c) | ✅ §6 of this document, and in every UC-41 export |
| Objection | Art. 18 § | Art. 21 | ✅ UC-44 with the `ObjectionPending` ground, which restricts processing while the objection is weighed |

Deadlines: **LGPD Art. 19 §2 — 15 days** for confirmation and access; **GDPR Art. 12(3) — one
month**. The shorter one governs where both apply.

## 10. Reviewing this document

Reviewed when a migration adds or removes a column holding personal data; when a recipient or
sub-processor changes; when the hosting region changes; when a transfer mechanism is executed or
lapses; and when §7.2's EEA determination is made or changes.

**§5.1 is enforced rather than trusted.** `PersonalDataPurposeTests` reflects over the entities that
hold personal data and fails if a property is missing from the table above. A record of processing
that silently falls behind the schema is worse than none, because it is relied on; this makes a new
column arrive with a stated purpose or not arrive at all.
