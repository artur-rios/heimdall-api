---
title: "Data Processing Agreement"
linkTitle: "Data Processing Agreement"
weight: 48
description: "The terms on which Heimdall processes a tenant's users' data — LGPD Art. 39, GDPR Art. 28."
---

# Data Processing Agreement — Heimdall API

**Template version 1.0 — 9 September 2026**

## 0. How to read this

This is the agreement between **Heimdall's controller** (Artur Rios — the *Processor* below) and an
**organisation whose client system uses a Heimdall scope** (the *Controller* below), covering the
personal data of that organisation's users.

It is a template. Executing it means naming the organisation, the scope or scopes, and the date, and
signing it. Everything else is drafted to match what Heimdall actually does — the clauses cite the
mechanisms in this repository rather than describing a system in general terms, so a clause that
stops being true becomes a failing document rather than a comfortable one.

GDPR Art. 28(3) lists what such an agreement must contain; LGPD Art. 39 requires the processor to
follow the controller's instructions. Sections 2 to 9 map onto Art. 28(3)(a) to (h) in order.

---

## 1. Parties, subject matter, duration

| | |
| --- | --- |
| **Controller** | *[organisation name]* |
| **Processor** | Artur Rios — <arturdev@duck.com> |
| **Scope(s)** | *[scope name(s) and `PublicId`(s)]* |
| **Subject matter** | Identity management, authentication and authorisation for the Controller's users |
| **Duration** | For as long as the Controller has a live scope, and through the deletion steps in §9 |
| **Nature and purpose** | Storing identities; verifying credentials; issuing tokens; sending transactional email; recording an audit trail |
| **Categories of data subject** | The Controller's end users, and its scope administrators |
| **Categories of personal data** | Name, email address, password hash and salt, two-factor material, Google account identifier and profile picture, sign-in failure counters, audit records |
| **Special categories** | None |

---

## 2. Processing only on documented instructions — Art. 28(3)(a)

2.1 The Processor processes the Controller's personal data only on the Controller's documented
instructions. **The instructions are the API**: each endpoint the Controller calls is an instruction,
and the [API Reference](https://artur-rios.github.io/heimdall-api/docs/api-reference/) with the
[Use Case Specification](Use%20Case%20Specification%20Document.md) is the documented set of them.
Anything outside that set requires a written instruction.

2.2 The Processor additionally performs, without a per-instance instruction, the processing this
agreement itself provides for: the retention passes in §6, the audit trail in §4.3, and the security
measures in §4.

2.3 If the Processor considers an instruction to infringe the LGPD, the GDPR or another applicable
law, it will say so before acting on it (GDPR Art. 28(3), second paragraph).

2.4 **The Processor does not use the Controller's data for its own purposes.** It is not sold, not
used for advertising, not used to train anything, and not used to build any product other than the
service provided to the Controller.

---

## 3. Confidentiality — Art. 28(3)(b)

3.1 Everybody authorised to process the Controller's data is bound to confidentiality. At present
that is the Processor alone; adding anyone brings them within this clause before they are given
access.

3.2 Administrative access to production data is limited to what an operational task requires, and
every write is recorded by the audit trail (§4.3).

---

## 4. Security measures — Art. 28(3)(c), Art. 32, LGPD Art. 46

4.1 The Processor maintains the measures set out in the
[Threat Model](Threat%20Model%20Document.md) and required by NFR-02, NFR-03, NFR-13, NFR-15,
NFR-16 and NFR-17. Specifically:

- passwords stored as Argon2id hashes with a per-person salt;
- TOTP secrets encrypted at rest; recovery codes stored only as hashes;
- password reset and email verification tokens stored only as SHA-256 digests;
- Google ID tokens verified for signature, issuer, audience and expiry before any claim is trusted;
- internal database identifiers never exposed; only random `PublicId` values cross the boundary;
- per-account lockout and per-IP rate limiting against credential guessing;
- TLS on inbound connections.

4.2 **Known gaps are disclosed rather than glossed.** As at this template version:

- Encryption at rest for the database and its backups is **required and documented** (NFR-25) but
  **not verified from the repository** — it is a property of the hosting, and the Controller should
  ask the Processor to evidence it before executing.
- The database connection warns at start-up when it does not require TLS, rather than refusing to
  start. Confirming TLS and turning that into a refusal is outstanding.
- There is no automated breach detection
  ([#105](https://github.com/artur-rios/heimdall-api/issues/105)). A Controller for whom this is
  material should not execute this agreement until it is closed.

4.3 Every attempted write produces an audit entry recording the actor, the operation, the outcome and
— on a refusal — the reason (NFR-09). The table is append-only, enforced by database triggers.

---

## 5. Sub-processors — Art. 28(2) and 28(3)(d)

5.1 The Controller gives general authorisation for the sub-processors listed in §7.

5.2 The Processor will give the Controller **at least 30 days' notice** before adding or replacing a
sub-processor, during which the Controller may object. If the objection cannot be resolved, the
Controller may terminate and take the deletion or return in §9.

5.3 Each sub-processor is bound by terms no less protective than these, and the Processor remains
fully liable to the Controller for their performance.

---

## 6. Assisting the Controller — Art. 28(3)(e) and (f)

6.1 **Data subject rights.** Where a request reaches the Processor directly, it is handled as
follows:

| Request | Handled by |
| --- | --- |
| Erasure | The Processor, through UC-42, completed automatically within 30 days |
| Correction | The Controller's own users, through UC-08 |
| Access and portability | ⚠️ Not yet available — [#90](https://github.com/artur-rios/heimdall-api/issues/90). Until it lands, the Processor extracts the data manually on request |
| Restriction and objection | ⚠️ Not yet available — [#93](https://github.com/artur-rios/heimdall-api/issues/93). Referred to the Controller |

6.2 A request the Processor cannot satisfy is **forwarded to the Controller within 3 working days**,
leaving the Controller the remainder of the 15-day (LGPD Art. 19 §2) or one-month (GDPR Art. 12(3))
period to answer. The Processor does not answer a data subject on the Controller's behalf without
instruction.

6.3 **Retention is enforced automatically**, on the schedule in the
[Data Retention Schedule](Data%20Retention%20Schedule%20Document.md), and the Controller cannot
lengthen it beyond what that schedule permits. Shortening it for a scope is available on written
instruction.

6.4 The Processor assists the Controller with data protection impact assessments and prior
consultation (Art. 35, 36) by supplying the documentation in this repository, which is public.

---

## 7. Sub-processor list

| Sub-processor | Purpose | Location | Transfer mechanism |
| --- | --- | --- | --- |
| **Mailgun** (Sinch) | Transactional email delivery | United States | ⚠️ ANPD standard contractual clauses to be executed — Data Protection Document §7.1 |
| **Google** | Google ID token verification | United States | ⚠️ ANPD standard contractual clauses to be executed — Data Protection Document §7.1 |
| **Hosting provider** | Database and API hosting | Brazil | No transfer; data remains in Brazil |

Personal data is stored in **Brazil**. The two outbound flows leave Brazil for the United States; the
mechanism for each is being executed and is recorded in
[§7 of the Data Protection Document](Data%20Protection%20Document.md), which is the live version of
this table.

**If the Controller is established in the EEA, or its users are**, note that Brazil holds no EU
adequacy decision, so the Controller's transfer to the Processor is itself a restricted transfer
requiring Art. 46 safeguards. That has to be resolved between the parties before this agreement is
executed — see Data Protection Document §7.2.

---

## 8. Breach notification — Art. 28(3)(f), Art. 33(2), LGPD Art. 48

8.1 The Processor notifies the Controller of a personal data breach affecting the Controller's data
**without undue delay and in any event within 24 hours** of becoming aware of it.

8.2 The 24 hours is deliberately shorter than the Controller's own deadline. The Controller has 72
hours under GDPR Art. 33 and 3 working days under ANPD Resolution CD/ANPD nº 15/2024, both counted
from **the Controller's** awareness — which a notification from the Processor is what creates. A
processor deadline equal to the controller's would leave the controller no time at all.

8.3 The notification describes what is known: the nature of the breach, the categories and
approximate number of data subjects and records, the likely consequences, and the measures taken.
Where not all of it is available at once, it is given in phases rather than withheld until complete.

8.4 The Processor does not notify a supervisory authority or the data subjects on the Controller's
behalf unless instructed in writing.

---

## 9. Deletion or return at the end — Art. 28(3)(g)

9.1 On termination, at the Controller's election:

- **Return** — the Processor exports the scope's identities and provides them to the Controller; or
- **Delete** — the Processor hard-deletes the scope, which cascades to its users, Google Users,
  applications and permissions (NFR-08, NFR-14).

9.2 The election is made within **30 days** of termination. Absent an election, the Processor
**deletes**, because retaining personal data with no controller and no purpose is the worse default.

9.3 **Two things are retained after deletion, and both are disclosed rather than assumed:**

- **Audit entries** naming the scope's operations, for which the Processor is controller (Data
  Protection Document §3), retained for 18 months and then stripped of anything identifying a
  person.
- **Backups** taken before termination, until they age out of the backup retention period —
  proposed at 35 days. A restore is followed by re-applying every erasure completed since the backup
  was taken, from a ledger kept outside the database; the mechanism is implemented and tested
  (NFR-25), and the retention figure awaits the Controller's confirmation.

---

## 10. Audit — Art. 28(3)(h)

10.1 The Processor makes available the information necessary to demonstrate compliance with this
agreement. Most of it is already public: this repository holds the requirements, the threat model,
the retention schedule, the record of processing, and the test suite that enforces them.

10.2 The Controller may audit no more than once a year, on 30 days' notice, at its own cost, unless
an audit follows a breach — in which case neither the frequency limit nor the cost allocation
applies.

---

## 11. Governing law

Brazilian law, with the courts of the Processor's domicile, without prejudice to a data subject's
right to bring proceedings where they are entitled to under LGPD Art. 22 or GDPR Art. 79.

---

## 12. Signatures

| | Controller | Processor |
| --- | --- | --- |
| **Name** | | Artur Rios |
| **Title** | | Controller / Encarregado |
| **Date** | | |
| **Signature** | | |
