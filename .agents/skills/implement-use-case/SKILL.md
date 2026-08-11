---
name: implement-use-case
description: >-
  Use when the user wants to implement, build, or start a use case in the Heimdall API,
  identified by its use case number (e.g. "implement UC-03", "start UC-11", "let's do use case 7",
  "begin the create scope use case"). Orchestrates the full delivery flow defined in the Development
  Workflow Document — reading the specs from docs/requirements, refining spec and plan via the
  superpowers skills, branching, implementing to repository patterns, testing, and PR — while pausing
  for human review at every workflow-stage transition except moving the issue to In Progress. Invoke
  this whenever a message names a UC number in an implementation context, even if the word "skill" is
  never used.
---

# Implement a Use Case

This skill drives the implementation of a single Heimdall API use case from backlog to a
review-ready pull request, following the project's own [Development Workflow Document](../../../docs/requirements/Development%20Workflow%20Document.md).

## Invocation

The user names a use case by number, e.g. `UC-03`. If the number is missing or ambiguous, ask which
use case before doing anything else. One invocation handles exactly **one** use case.

## The golden rule: pause at every stage boundary

The user wants to review the work before it advances. Therefore:

- **The only status change you may make on your own is `Todo → In Progress`** (Step 3). It signals
  work has begun, right after the branch is created.
- **Every other stage transition requires explicit approval first.** Before moving the issue to
  **Testing**, before opening a **PR**, and before moving the issue to **Done**/closing it, stop,
  show what you've done, and ask. Do not batch these.
- **Never merge the PR, never self-approve, never delete the branch.** Review, merge, and branch
  deletion are human actions (Development Workflow §4, Step 7). You may *prepare and push* the PR.

When you pause, summarize what was completed in the stage, what comes next, and wait for a clear go.

## Workflow overview

```
Load specs → Refine (brainstorm → plan) → [approval] → Branch + issue→In Progress
  → Implement → [approval] → issue→Testing → Test until green → [approval]
  → Open PR → [human review + merge + delete branch] → [approval] → issue→Done + close
```

Steps 1–2 and every `[approval]` gate are where you stop and involve the user.

---

## Step 1 — Load the specifications

Read the relevant docs under `docs/requirements/` before designing anything. Pull the specifics for
this UC, don't work from memory:

- [Use Case Specification Document](../../../docs/requirements/Use%20Case%20Specification%20Document.md)
  — the target use case: actors, pre/postconditions, main flow, and every `AF-xx` alternative flow.
- [System Requirements Document](../../../docs/requirements/System%20Requirements%20Document.md)
  — the `FR-xx` requirements traced to this UC, plus the data model, endpoints, and authorization matrix.
- [Development Workflow Document](../../../docs/requirements/Development%20Workflow%20Document.md)
  — the delivery flow this skill follows.
- [Testing Specification Document](../../../docs/requirements/Testing%20Specification%20Document.md)
  — how the tests will be written in Step 6.
- [Technology Stack Document](../../../docs/requirements/Technology%20Stack%20Document.md)
  — the libraries, versions, and patterns to build with.

Then locate the GitHub issue for this UC (its title starts with the UC number). See
[references/project-board.md](references/project-board.md) for the exact commands to find the issue
and its project board item.

## Step 2 — Refine spec and plan with the superpowers skills

The use case spec is the *what*; you still need a refined, repo-specific *how* before coding. Use the
superpowers process so the design is deliberate and reviewable:

1. **`superpowers:brainstorming`** — turn the UC spec + traced requirements into a concrete design for
   *this* repository: which Command/Query handlers, validators, inputs/outputs, domain behavior,
   controller endpoint(s), entity map changes, and DI registrations are needed, and how the
   alternative flows map to errors/HTTP responses. Ground it in the repository patterns
   (see [references/repo-patterns.md](references/repo-patterns.md)).
2. **`superpowers:writing-plans`** — capture the result as a written, step-by-step implementation
   plan, sequenced test-first per the Testing Specification.

**Present the refined spec and plan to the user and wait for approval before writing any code.** This
is the first review gate. Adjust the plan based on their feedback.

## Step 3 — Branch and move the issue to In Progress

Once the plan is approved, create the feature branch from an up-to-date `main` using the exact naming
pattern from the workflow doc — `feature/uc-##-use-case-name` (zero-padded number, kebab-case name):

```bash
git switch main && git pull
git switch -c feature/uc-03-update-scope
```

Then — and this is the **one** status change you make without asking — move the issue to
**In Progress** on the project board (commands in [references/project-board.md](references/project-board.md)).

## Step 4 — Implement the use case

Execute the approved plan, following the repository's established patterns
([references/repo-patterns.md](references/repo-patterns.md)). Use
**`superpowers:test-driven-development`** (and `superpowers:executing-plans` if executing a written
plan across checkpoints) so implementation and its tests grow together. Implement the main flow **and
every alternative flow** from the spec. Commit on the feature branch as you go.

## Step 5 — Pause for review before Testing

When the implementation is code-complete, **stop and ask** before advancing. Summarize what was built
(handlers, endpoint, validators, mappings, DI). Only after the user approves, move the issue to
**Testing**.

## Step 6 — Test until green

Following the [Testing Specification Document](../../../docs/requirements/Testing%20Specification%20Document.md):

- Write **unit tests** for each Command/Query handler and any new Domain behavior (main flow + each
  applicable `AF-xx`).
- Write **functional tests** for each endpoint (main flow + every `AF-xx`, including authorization),
  end-to-end against Testcontainers PostgreSQL.
- Run the suite, fix failures, and **re-run until everything passes**:

```bash
dotnet test src/ArturRios.Heimdall.sln --filter "Category=Unit"
dotnet test src/ArturRios.Heimdall.sln --filter "Category=Functional"
```

Report the passing results. **Do not open a PR yet — stop and ask.**

## Step 7 — Open the pull request (after approval)

Once the user approves, push the branch and open a PR into `main` that references the issue so the
merge closes it:

```bash
gh pr create --repo artur-rios/heimdall-api --base main \
  --title "UC-03: Update Scope" \
  --body "Implements UC-03. Closes #4."
```

Then **hand off to the human** for review and merge. Do **not** merge or delete the branch yourself.

## Step 8 — Close out (after the human merges)

After the PR is merged and the branch deleted by the user, **ask** before finishing, then move the
issue to **Done** and confirm it is closed (a `Closes #` reference closes it automatically on merge —
still verify the board shows **Done**).

---

## Definition of Done

Mirror the workflow doc's checklist before calling the use case complete:

- [ ] Implemented on a `feature/uc-##-use-case-name` branch from `main`.
- [ ] Main flow and every alternative flow implemented.
- [ ] Unit tests cover handlers + new domain behavior; functional tests cover endpoints (incl. auth).
- [ ] Full suite passes (`Category=Unit` and `Category=Functional`).
- [ ] PR reviewed by a human and merged to `main`; feature branch deleted.
- [ ] Issue in **Done** and closed.

## Reference files

- [references/project-board.md](references/project-board.md) — GitHub issue/project-board commands
  (find the issue, find its item, change Status).
- [references/repo-patterns.md](references/repo-patterns.md) — the repository's layer responsibilities
  and the in-repo reference implementations to copy the pattern from.
