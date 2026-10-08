+++
title = 'Password recovery'
linkTitle = 'Password recovery'
weight = 50
description = 'UC-12 and UC-13 — an endpoint whose whole design is about what it must not reveal.'
+++

Two anonymous, rate-limited endpoints: one asks for a link, the other spends it.

## Requesting a link — UC-12

```mermaid
sequenceDiagram
    autonumber
    actor C as Anonymous caller
    participant AC as AuthController
    participant H as PasswordRecoveryCommandHandler
    participant Q as PasswordRecoveryQueue
    participant W as PasswordRecoveryDispatcher
    participant PR as PasswordRecoveryProcessor
    participant PS as IPasswordResetService
    participant S as IPasswordResetSender
    participant DB as PostgreSQL

    C->>AC: POST /api/auth/password-recovery {email, scopeId?}
    AC->>H: HandleAsync
    H->>H: validate input shape (NFR-10)
    Note right of H: the only rejection this endpoint ever issues
    H->>Q: TryEnqueue(email, scopeId)
    H-->>C: 200 — identical response, in the same time, either way

    Q-->>W: next request (background, own DI scope)
    W->>PR: ProcessAsync
    PR->>DB: find by the lookup the role implies
    alt person found and eligible
        PR->>PS: issue token
        PS->>DB: INSERT PasswordResetToken (time-limited)
        PS->>S: SendAsync(email, token)
    else nobody, deleted or restricted person, or deleted scope (AF-12a)
        Note over PR,DB: no row written, no email sent
    end
```

**Every path returns the same success output.** AF-12a — the address belongs to nobody — is not an
error flow but the *absence* of one: the handler issues no token and answers exactly as it would
have. A logically deleted person, and a `User` whose scope is deleted, are treated the same way.

The only thing that distinguishes the two paths is a row that does not get written and an email that
consequently never arrives — neither of which is visible to the caller.

**The same answer, in the same time.** While the lookup, the token insert and the Mailgun call ran on
the request, a registered address answered a Mailgun round trip later than an unknown one — a timing
difference that answered exactly what the uniform response refuses. So the handler only validates and
queues; everything that depends on the address happens afterwards, in `PasswordRecoveryProcessor`,
run by a hosted service in a DI scope of its own (never the finished request's). The queue is in
memory and bounded: a durable outbox would keep every address anybody typed, registered or not, and
a response-time floor would have to outlast Mailgun's slowest send. The cost is that a restart drops
whatever is still queued, which a person who receives nothing simply asks for again.

{{% alert title="An outage must not become an oracle" color="warning" %}}
A Mailgun failure here is **logged, never surfaced**. If a delivery error turned into a 500, the
difference between "your guess was a real account" and "it wasn't" would be an HTTP status code.
{{% /alert %}}

## Setting the new password — UC-13

```mermaid
sequenceDiagram
    autonumber
    actor P as Person
    participant FE as Front-end reset page
    participant AC as AuthController
    participant H as ResetPasswordCommandHandler
    participant DB as PostgreSQL

    P->>FE: opens the emailed link ?token=…
    FE->>AC: POST /api/auth/password-reset {token, newPassword}
    AC->>H: HandleAsync
    H->>H: validate the new password's shape
    H->>DB: SELECT token
    alt unknown / expired / already spent
        H-->>FE: 400 — each rejection named
    end
    H->>DB: spend the token (UPDATE … WHERE NOT used AND unexpired)
    alt a parallel request spent it first
        H-->>FE: 400 — already used (AF-13b)
    end
    H->>DB: UPDATE person — fresh Argon2id hash + new salt
    H->>DB: mark every other live reset token for this person Used
    H-->>FE: 200
```

Both endpoints are **anonymous** because someone arriving from a link in their mail client holds no
token of any other kind.

Spending a reset token retires every other live reset token the person holds. A second "forgot
password" click cannot be replayed after the first has already changed the password.

## Why login costs what it costs

Password verification is Argon2id — by this codebase's hashing library defaults, 600 MB and 16
threads per verification. That is deliberate for a stored credential, and it is also precisely why
these endpoints are rate-limited: an unthrottled burst against `/login` is a memory and CPU
exhaustion vector before it is a brute-force one. See
[Operations](../../operations/#rate-limiting).
