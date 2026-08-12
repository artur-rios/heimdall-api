+++
title = 'Two-factor authentication'
linkTitle = 'Two-factor'
weight = 20
description = 'UC-36 to UC-40 — setup, confirmation, the challenge-token round trip, and recovery codes.'
+++

Two-factor authentication is opt-in, per person, and available to `User`, `ScopeAdmin`, and
`SystemAdmin` alike. It is **not** available to Google Users, who are already subject to Google's own
account security.

Every 2FA endpoint acts on the caller themselves — the person comes from the bearer token, never from
a path or body — so no caller can reach another person's configuration.

## The lifecycle

```mermaid
stateDiagram-v2
    [*] --> None: no TwoFactorAuth row
    None --> Pending: UC-36 enable<br/>(row created, IsActive = false)
    Pending --> Pending: UC-36 again<br/>(AF-36d — selection overwritten)
    Pending --> Active: UC-37 confirm<br/>(every selected method proven)
    Active --> Active: UC-40 regenerate<br/>(ten fresh recovery codes)
    Active --> None: UC-39 disable
    Active --> Active: UC-38 verify<br/>(completes a gated login)
```

## Setup — UC-36 and UC-37

```mermaid
sequenceDiagram
    autonumber
    actor P as Person (authenticated)
    participant AC as AuthController
    participant EH as EnableTwoFactorAuthCommandHandler
    participant CH as ConfirmTwoFactorAuthCommandHandler
    participant TP as ITotpSecretProtector
    participant ES as ITwoFactorEmailSender
    participant DB as PostgreSQL

    P->>AC: POST /api/auth/2fa/enable {app?, email?}
    AC->>EH: HandleAsync
    EH->>DB: SELECT TwoFactorAuth for caller
    alt already active (AF-36a)
        EH-->>P: rejected — configuration untouched
    end
    EH->>DB: UPSERT pending row (IsActive = false)
    Note over EH,DB: AF-36d — re-initiating overwrites<br/>the pending selection entirely

    opt App selected
        EH->>TP: Protect(fresh TOTP secret)
        TP-->>EH: encrypted secret
        EH->>DB: store TotpSecretEncrypted
        EH-->>P: secret returned once, for enrolment
    end
    opt Email selected
        EH->>DB: retire outstanding codes, INSERT fresh 6-digit code (hash + salt, 10 min)
        EH->>ES: SendAsync(email, code)
    end

    P->>AC: POST /api/auth/2fa/confirm {appCode?, emailCode?}
    AC->>CH: HandleAsync
    CH->>DB: SELECT pending row
    CH->>CH: check appCode if AppEnabled (AF-37b)
    CH->>CH: check emailCode if EmailEnabled (AF-37c)
    CH->>DB: mark the confirming email code Used
    CH->>DB: INSERT 10 recovery codes (hashes only)
    CH->>DB: UPDATE IsActive = true
    CH-->>P: 200 — ten recovery codes, shown exactly once
```

`IsActive` is what separates a *confirmed* configuration from one still pending. A row with
`IsActive = false` means UC-36 created it and UC-37 has not yet activated it — and only an active
configuration gates a login.

Confirmation requires proof of **every** method selected at setup: an `appCode` if `AppEnabled`, an
`emailCode` if `EmailEnabled`, both if both.

## Completing a gated login — UC-38

```mermaid
sequenceDiagram
    autonumber
    actor C as Client
    participant AC as AuthController
    participant VH as VerifyTwoFactorAuthCommandHandler
    participant CT as JwtTwoFactorChallengeTokenIssuer
    participant FV as ITwoFactorFactorVerifier
    participant TS as PersonAuthTokenService
    participant DB as PostgreSQL

    Note over C: login answered requiresTwoFactor<br/>with a challenge token
    C->>AC: POST /api/auth/2fa/verify {challengeToken, code | recoveryCode}
    AC->>VH: HandleAsync

    VH->>CT: validate signature, expiry, mfaPending claim
    alt invalid (AF-38a)
        CT-->>VH: rejected
        VH-->>C: 401 challenge invalid
    end

    VH->>DB: SELECT person + active TwoFactorAuth
    VH->>FV: TOTP? live email code? unused recovery code?
    alt none matches (AF-38b / AF-38c)
        FV-->>VH: no
        VH-->>C: 401 factor invalid
    end

    VH->>TS: TryBuildSubject(person)
    alt scope no longer eligible
        VH-->>C: 401 scope no longer eligible
    end

    VH->>DB: mark the redeemed recovery code Used + UsedAt
    VH->>DB: mark the redeemed email code Used
    VH->>TS: IssueAsync(subject)
    TS-->>VH: full authentication token
    VH-->>C: 200 {token, expiresAt}
```

Three details in that diagram carry real weight:

**AF-38b and AF-38c answer identically.** A wrong code and an already-used recovery code both return
`factor invalid`, 401 — the same reasoning that collapses UC-11's five rejections into one.

**Scope ineligibility gets its own message.** If the challenge token and the second factor were both
valid but the person's scope eligibility no longer holds, the answer is
`scope no longer eligible`, not `challenge invalid`. Collapsing that case would misdescribe what
actually happened — nothing the caller supplied was wrong.

**Nothing can be replayed.** The redeemed recovery code and the redeemed email code are both marked
used before the token is issued.

The final token is issued by `PersonAuthTokenService` — the same service a direct login uses — so a
2FA-gated login ends with exactly the token a direct one would have produced.

## Why the guard filter never fires here

`MfaPendingGuardFilter` runs on every controller action and rejects any identity carrying
`MfaPending`. It never trips on `POST /api/auth/2fa/verify` because that endpoint is
`[AllowAnonymous]` and reads the challenge token as a **body field**, not as an
`Authorization: Bearer` credential — so the pipeline never attaches an MFA-pending identity for that
call. The filter only ever fires when a challenge token is misused as a bearer token somewhere
else — exactly the case **FR-2F-10** wants blocked.

## Disable and regenerate — UC-39 and UC-40

Both require a valid second factor as well as authentication, and both reuse
`ITwoFactorFactorVerifier`, so "match against TOTP, or the current email code, or an unused recovery
code" is implemented in exactly one place.

| | UC-39 disable | UC-40 regenerate |
| --- | --- | --- |
| Requires | Bearer token + second factor | Bearer token + second factor |
| Effect | Removes the configuration | Replaces all ten recovery codes |
| Response | Confirmation | The ten new codes, shown once |

## Storage of secrets

| | Stored as | Returned |
| --- | --- | --- |
| TOTP secret | Encrypted at rest via ASP.NET Core Data Protection | Once, at provisioning |
| Recovery codes | Hash only — high-entropy random text needs no per-code salt | Once, when generated |
| Email codes | Hash **+ per-code salt** — only 10⁶ possible values | Never; mailed |

That is **NFR-16**. See [Domain model](../../domain-model/#secrets-at-rest).
