---
title: "Privacy Notice"
linkTitle: "Privacy Notice"
weight: 47
description: "What Heimdall does with personal data, told to the people it belongs to — LGPD Art. 9, GDPR Art. 13 and 14."
---

# Privacy Notice — Heimdall API

**Version 1.0 — 9 September 2026**

## 1. What this is

Heimdall is a centralised identity service. If you have an account with a system that uses it, this
notice tells you what it holds about you, why, for how long, and what you can make it do.

It is written to be read by the person it is about. The formal record behind it — legal bases,
recipients, transfer mechanisms — is the
[Data Protection Document](Data%20Protection%20Document.md).

> **The version matters.** This notice is versioned, and the version in force when your account was
> created is recorded against it, so it is always possible to say what you were actually told rather
> than what you would be told today.

## 2. Who is responsible

| | |
| --- | --- |
| **Controller** | Artur Rios |
| **Encarregado (Data Protection Officer)** | Artur Rios |
| **Contact for anything in this notice** | <arturdev@duck.com> |

Published here because LGPD Art. 41 §1 requires the Encarregado's identity and contact to be
publicly disclosed.

**If your account belongs to another organisation's system**, that organisation decides why your
data is held, and it is the controller for it — Heimdall processes it on their instructions. You can
raise anything with the address above, and it will be answered or passed to them; the
[Data Processing Agreement](Data%20Processing%20Agreement.md) sets out which.

## 3. What is held about you

| | |
| --- | --- |
| **Your name and email address** | To identify your account and to write to you about it |
| **Your password, stored only as a hash** | To check it is you signing in. The password itself is never stored and cannot be recovered from what is |
| **Two-factor settings and codes** | If you turn two-factor authentication on. The secret is encrypted; recovery codes are stored only as hashes |
| **Your Google account identifier, name, address and profile picture** | Only if you sign in with Google, and only what Google gives us |
| **Failed sign-in attempts and lockout state** | To stop somebody guessing your password |
| **A record of actions taken on your account** | To be able to say what happened, and when, if it is ever disputed |

Your IP address is used briefly, in memory only, to limit how many sign-in attempts can come from
one place in a minute. It is not stored and not written to logs.

**Nothing here is sold, and nothing is used for advertising or profiling.** No decision affecting
you is made automatically.

## 4. Why, and on what basis

Almost everything here is held because it is **necessary to run an account you asked for** — LGPD
Art. 7 V, GDPR Art. 6(1)(b). The security data is held on **legitimate interests** (LGPD Art. 7 IX,
GDPR Art. 6(1)(f)): defending accounts against people trying to break into them. The erasure records
are held because the **law requires** it (LGPD Art. 7 II, GDPR Art. 6(1)(c)).

We do not ask you to consent to any of it, deliberately. Consent can be withdrawn at any moment, and
an identity service that stopped checking passwords when you withdrew it would not be protecting
you — it would be broken. Basing it on necessity instead is more honest about what is actually
happening.

## 5. Who else sees it

| | |
| --- | --- |
| **Mailgun** (United States) | Receives your email address to deliver verification, password reset and two-factor messages |
| **The organisation whose system you use** | Sees the accounts in its own scope |
| **The hosting provider** (Brazil) | Stores the database |

Data is held in **Brazil**, and your email address is sent to the **United States** to deliver the
messages above. That is an international transfer, made because sending you the email that activates
or restores your account is part of running the account you asked for — LGPD Art. 33 IX with Art. 7
V, and where it applies, the standard contractual clauses in Sinch's own terms. The detail is in
[§7 of the Data Protection Document](Data%20Protection%20Document.md).

**If you sign in with Google, nothing about you is sent to Google by us.** Google is where your name,
address and picture *came from* — the token it issues when you sign in is checked here against
Google's published certificates, on this server. Your sign-in at Google itself is between you and
Google, under their terms.

## 6. How long it is kept

The full schedule is the [Data Retention Schedule](Data%20Retention%20Schedule%20Document.md). The
short version:

| | |
| --- | --- |
| **Your account** | While it exists |
| **If you ask to be erased** | Anonymised within **30 days** |
| **If an administrator deletes your account** | Anonymised after **90 days**, so a mistake can be undone |
| **Password reset and verification links** | A week after they expire |
| **Records of actions on your account** | 18 months, then stripped of anything identifying you |

"Anonymised" means the name, address and credentials are overwritten and cannot be recovered. The
row itself stays, because other records point at it, but nothing in it refers to you any more.

## 7. What you can do

| You can | How | By when |
| --- | --- | --- |
| **Be erased** | `POST /api/auth/erasure-request` — you will be asked for your password, or a fresh Google sign-in, because it cannot be undone | 30 days |
| **Correct your details** | Update your own record | Immediately |
| **Get a copy of your data** | `POST /api/auth/data-export` — everything held about you, plus who else sees it and how long it is kept | Immediately |
| **Have processing restricted, or object** | `POST /api/auth/processing-restriction` — suspends your account without deleting anything, while something about it is disputed | Immediately |
| **Ask anything, or complain** | <arturdev@duck.com> | 15 days |

One of those is honestly marked unavailable. Publishing a notice that promised rights the system
cannot yet deliver would be worse than admitting the gap: it is the kind of claim a regulator checks.

**You can also complain to a supervisory authority.** In Brazil that is the
[ANPD](https://www.gov.br/anpd/). If you are in the EEA, it is your national data protection
authority.

If you ask to be erased and you are the last administrator of an organisation's scope, the request
is still recorded and the clock still runs, but it cannot be completed until that organisation
appoints another administrator. You will be told if that applies to you.

## 8. Changes

This notice is versioned. Material changes raise the version, and the version you were shown is
recorded against your account.

| Version | Date | Change |
| --- | --- | --- |
| 1.0 | 9 September 2026 | First published |
