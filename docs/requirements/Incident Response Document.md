---
title: "Incident Response Document"
linkTitle: "Incident Response Document"
weight: 49
description: "What counts as a personal data breach, who declares one, the clocks that start, and who has to be told."
---

# Incident Response Document — Heimdall API

## 1. Purpose

GDPR Art. 33 requires a personal data breach to be notified to the supervisory authority **within 72
hours** of the controller becoming aware of it, and Art. 34 requires the affected data subjects be
told without undue delay where the risk to them is high. LGPD Art. 48 requires communication to the
ANPD and the subjects, which **ANPD Resolution CD/ANPD nº 15 of 24 April 2024** fixes at **3 working
days**. Art. 33(5) requires every breach be documented, whether or not it was notifiable.

None of that is possible without deciding, in advance and in writing, what counts and who decides.
That is what this document is.

## 2. Who decides

| Role | Who | What they do |
| --- | --- | --- |
| **Incident lead** | Artur Rios (controller) | Declares an incident, decides whether it is a personal data breach, decides whether it is notifiable, and signs the register entry |
| **Encarregado** | Artur Rios | Notifies the ANPD, and the data subjects where required |
| **Processor contact** | Artur Rios | Notifies tenant controllers whose scopes are affected |

All three are the same person, which for a project this size is honest rather than a gap to paper
over — see §2 of the [Data Protection Document](Data%20Protection%20Document.md). The consequence
worth naming: there is nobody to escalate to, so the deadlines in §4 are the only forcing function,
and they are why they are written down.

## 3. What counts as a personal data breach

GDPR Art. 4(12): a breach of security leading to accidental or unlawful **destruction, loss,
alteration, unauthorised disclosure of, or access to** personal data. Not only theft — losing data
irrecoverably is a breach, and so is altering it without authority.

Concretely, for this system:

| Is a breach | Is not, by itself |
| --- | --- |
| The database or a backup copied by somebody unauthorised | A failed sign-in, however many |
| An identity acting outside its authorisation and reaching another tenant's data | A refused authorisation attempt — that is the control working |
| Password hashes, TOTP secrets or tokens disclosed | An expired token being rejected |
| An erasure that did not happen, or was undone by a restore and not reconciled | A scheduled pass failing once and succeeding on the next tick |
| Log files containing personal data leaving the host unencrypted | Logs reaching the operator's own collector as designed |
| A backup restored over current data, losing records irrecoverably | A restore that was reconciled per NFR-25 |

**A signal from §6 is not a breach.** It is a reason to look. Treating detection as declaration
would put the 72-hour clock in the hands of a threshold.

## 4. The clocks, and when they start

Both run from **awareness**, not from the incident. Awareness is when the incident lead has enough
information to conclude, on reasonable investigation, that personal data was probably affected — not
the moment of certainty, and not the moment a log line was written that nobody read.

| Obligation | Deadline | From |
| --- | --- | --- |
| Notify the ANPD (LGPD Art. 48, Res. 15/2024) | **3 working days** | Awareness |
| Notify the supervisory authority (GDPR Art. 33) | **72 hours** | Awareness |
| Notify affected data subjects (Art. 34, LGPD Art. 48) | Without undue delay, where risk is high | Awareness |
| Notify a tenant controller whose scope is affected | **24 hours** — DPA §8 | Awareness |

The 24 hours to tenants is deliberately the shortest. A tenant's own 72-hour clock starts when *they*
become aware, which our notification is what creates; a processor deadline equal to the controller's
would leave the controller no time at all.

**GDPR applies.** The determination was made on 10 September 2026 and is recorded in §7.2 of the
Data Protection Document: the service is open to EEA data subjects. Both sets of deadlines are
therefore live, and **the shorter of the two governs** — three working days to the ANPD, and, where
a supervisory authority is also owed notice, seventy-two hours.

## 5. The procedure

1. **Contain.** Stop the bleeding before investigating. Revoke the signing secret
   (`HEIMDALL_AUTH_TOKEN_SECRET`, rotating through `_PREVIOUS`), restrict or suspend the identities
   involved (UC-44), and if a scope is implicated, consider disabling its Google sign-in.
2. **Record the time of awareness.** In the register, before anything else. Every deadline in §4
   counts from this timestamp, and reconstructing it afterwards from memory is how a 72-hour
   obligation becomes a missed one.
3. **Establish scope.** Which identities, which scopes, which categories of data, and over what
   period. The audit trail is the primary source; the application logs are the second.
4. **Decide: is it a personal data breach?** Against §3. Record the reasoning either way.
5. **Decide: is it notifiable?** A breach is notifiable unless it is unlikely to result in a risk to
   the rights and freedoms of the data subjects. Record the reasoning either way — **this is the
   part regulators ask about**, and a register full of entries reading "not notifiable" with no
   reasoning is worse than no register.
6. **Notify**, per §4.
7. **Complete the register entry**, per §7.
8. **Review.** What made it possible, what made it detectable, what delayed either. Feed the answers
   into the [Threat Model](Threat%20Model%20Document.md) and the periodic review in the
   [Development Workflow](Development%20Workflow%20Document.md).

## 6. Detection

NFR-26. The system produced strong signals from the beginning and nothing consumed any of them — the
audit trail recorded every refused write with its reason, the lockout counters tracked per-account
brute force, and the hash gate shed under load. All written down; none read. **A trail nobody reads
does not make anyone aware of anything**, and awareness is what starts every clock in §4.

`SecurityMonitoringService` now reads them on a schedule and writes what it finds to the log at
warning level, each line carrying the marker `SECURITY_SIGNAL` and a stable `Kind`:

| Kind | Raised when | Why it matters |
| --- | --- | --- |
| `REPEATED_REFUSALS` | One identity has more refused writes than the threshold, within the window | Sustained refusals are what somebody probing for what they may do looks like |
| `LOCKOUT_SPIKE` | More accounts are locked out at once than the threshold | One is ordinary; many together is credential stuffing against a list of addresses |
| `CREDENTIAL_VERIFICATION_SHEDDING` | The hash gate refuses more often than the threshold | The process is at its concurrent-derivation limit (TH-03) — a load condition, and also a lever |

**Every threshold is a rate, not a total.** Twenty refusals over a year is somebody who forgets their
password; twenty in a quarter of an hour is somebody trying things. A total alerts on the first and
never notices the second.

**Signals repeat while the condition persists.** There is no suppression, deliberately: an attack
that continues is more interesting on its tenth tick than its first, and a detector that goes quiet
while the thing it detects is still happening is worse than none.

**Where the alerts go is the operator's choice.** This API has no opinion about pagers or SIEMs, and
building an integration for one would be guessing. What it does is make the signal unmistakable and
machine-matchable where the operator already collects — the logs, which have a retention period
(NFR-22) and ship off the host. Match on `SECURITY_SIGNAL` and the `Kind`, not the prose.

Detection is off only if `HEIMDALL_MONITORING_ENABLED=false`, which logs a warning saying what has
been given up.

## 7. The breach register

Art. 33(5) requires documentation of **every** breach, including those judged not notifiable. Keep
it outside this repository — it will contain details of real incidents — and use this shape:

| Field | Notes |
| --- | --- |
| Reference | Sequential |
| **Time of awareness** | The timestamp from step 2. Every deadline counts from here |
| Time of the incident | Where known; often it is not, and "unknown" is a legitimate entry |
| How it came to attention | A `SECURITY_SIGNAL`, a report, a routine review — this is what tells you whether detection works |
| Nature | Destruction, loss, alteration, disclosure, or unauthorised access |
| Categories and approximate number of data subjects | Approximate is what Art. 33(3)(a) asks for |
| Categories and approximate number of records | |
| Likely consequences | Art. 33(3)(c) |
| Measures taken | Containment, remediation, mitigation of adverse effects |
| **Notifiable? With reasoning** | Both answers need reasoning. "No" needs it most |
| Authority notified, when | Or why not |
| Subjects notified, when | Or why not |
| Tenant controllers notified, when | Per DPA §8 |
| Signed off by | The incident lead |

A register entry is written for an incident **judged not to be a breach at all**, too, with that
reasoning. The judgement is the evidence that the question was asked.

## 8. Reviewing this document

Reviewed after every incident, and on the cadence in the
[Development Workflow](Development%20Workflow%20Document.md) — whichever comes first. An untested
incident procedure is a document, not a capability.
