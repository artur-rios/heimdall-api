# Docs moved to heimdall-docs Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:**
- heimdall-api and heimdall-ui point readers at heimdall-docs, the one documentation site.
- heimdall-api drops its own site.
- Both repositories send their requirements (and heimdall-api its OpenAPI document and coverage report) to
  heimdall-docs through yggdrasil's new `sync` section.

**Architecture:** Four pull requests, two per repository. Each repository gets:
- **A cleanup PR** (Tasks 1 and 2). It is mergeable now, because nothing in it needs a new yggdrasil.
- **A sync PR** (Task 3). It adds the `sync` section. It can only pass CI once yggdrasil's `sync` feature
  (artur-rios/yggdrasil#134) is released and Jenkins runs that version, because until then an unknown field fails
  `yggdrasil.yml` validation.

**Tech Stack:** Markdown, YAML, C# and Python and shell comments (heimdall-api), Dart comments (heimdall-ui).

**Spec:** `D:\Repositories\heimdall-docs\docs\superpowers\specs\2026-11-02-centralized-documentation-design.md`. This
plan is its step 4, "The code repositories", including the owner's decision that each README stays as it is. Its
follow-ups come from the reviews of steps 2 and 3, listed in artur-rios/heimdall-docs#7.

## Global Constraints

- **New addresses.** The new site is `https://docs.artur-rios.tech/heimdall-docs/`, with these pages (from
  heimdall-docs#7):

  | Topic | Page |
  |---|---|
  | Landing | `docs/` |
  | Overview | `docs/overview/` |
  | Architecture | `docs/architecture/` |
  | Domain model | `docs/architecture/domain-model/` |
  | Getting started and running | `docs/installation/` |
  | Environments and deployment | `docs/installation/environments-and-deployment/` |
  | API section | `docs/api/` |
  | API reference | `docs/api/reference/` |
  | API explorer | `docs/api/explorer/` |
  | Flows | `docs/api/flows/` |
  | Operations | `docs/api/operations/` |
  | Test coverage | `docs/api/test-coverage/` |
  | API requirements | `docs/api/requirements/` |
  | Application section | `docs/application/` |
  | UI requirements | `docs/application/requirements/` |

  - An anchor on an old page carries over when the moved page kept its heading. Verify each against heimdall-docs'
    branch `feature/centralized-docs`.
  - The CHANGELOG and CONTRIBUTING are linked as repository files, never as site pages.
- **READMEs stay as they are** (owner's decision). Only links that pointed at the old API site change, plus one link
  to the docs site near the top. No section is removed or trimmed, and the use case status tracking is not touched.
- **Development Workflow Documents and README tracker text are not edited.** Their GitHub project-board references are
  the owner's process, and a follow-up for them is listed in the PR body.
- **Stays in heimdall-api:**
  - `docs/requirements/`
  - `docs/superpowers/`
  - `docs/openapi/heimdall.json`, which is still generated and checked by `check`
  - `docs/coverage-report/`, which is still written by `scripts/coverage.py` and archived by `test`
- **heimdall-api does not release before step 3's operator steps are done.** A release without `docs.Dockerfile` would
  otherwise break the old `heimdall-api-docs` job. The cleanup PR's body says so.
- **Branches.** Each branch is cut from `origin/develop` of its repository, with a pull request into `develop`.
  Commits use lowercase Conventional Commits, and each PR adds a `CHANGELOG.md` entry under `## [Unreleased]` in that
  repository's style.
- **`yggdrasil.yml` is a protected file** on both repositories' `develop`: only the owner can merge a change to it,
  with **Merge anyway**. Every PR that touches it says so.

## Review Focus

1. **No link left to `docs.artur-rios.tech/heimdall-api`** in either repository, except inside `CHANGELOG.md`'s
   released entries and `docs/superpowers/` (history). Checked by grep in Tasks 1 and 2.
2. **Every re-pointed link resolves.** Each new URL and anchor must exist on heimdall-docs' `feature/centralized-docs`
   build. Checked in Tasks 1 and 2 by building heimdall-docs into a scratch folder and looking each one up.
3. **heimdall-api still builds and tests after the deletions.**
   - Nothing under `src/`, `tests/`, `scripts/`, `ci/` or `tools/` reads a deleted path.
   - `dotnet build` and `python -m unittest discover -s scripts -p "test_*.py"` pass.
   - `scripts/openapi.py --check` still passes. (Task 1)
4. **The `sync` sections are valid.** They must pass `pipeline_config.py describe` run from yggdrasil's
   `feature/repository-sync` (#134). That includes heimdall-docs being a basic-model target, `test` having an
   `archive`, and paths that pass the checks. (Task 3)
5. **The UI requirements keep their current page addresses** after gaining `slug` front matter, so heimdall-docs'
   links and anchors do not break. (Task 2)

---

### Task 1: heimdall-api cleanup PR

**Branch:** `feature/docs-moved-to-heimdall-docs`

**Files:**
- **Delete** these tracked site files:
  - `docs/hugo.toml`, `docs/content/`, `docs/layouts/`, `docs/assets/`, `docs/archetypes/`, `docs/i18n/`
  - `docs/package.json`, `docs/package-lock.json`
  - `docs/prompts/`, but only if nothing outside `docs/superpowers` uses it; `git grep docs/prompts` finds no use
    today
  - `docs.Dockerfile`, `docs.Dockerfile.dockerignore`
- **Delete** the untracked local `docs/themes/` (a leftover Docsy checkout), and say so in the report.
- **Modify:**
  - `README.md`:
    - The badge and the "Read the documentation website" link point at the new landing page.
    - The "Documentation" table rows point at their new pages; Changelog and Contributing point at the repository
      files.
    - The paragraph about Hugo, Docsy and `docs.Dockerfile` becomes one sentence saying the site lives in
      heimdall-docs, which shows this repository's requirements, OpenAPI document and coverage report as copies.
    - The inline links at about lines 277, 363, 492, 541 and 570 are re-pointed.
  - `CONTRIBUTING.md`:
    - The project structure line for `docs/`.
    - Coverage (about lines 117–118): the report is published by heimdall-docs.
    - The links at about lines 147 and 247.
    - The OpenAPI paragraph (about 165): heimdall-docs publishes the committed file.
    - The "Documentation site" section is replaced by a short section saying where the documentation lives, how to
      change it (a PR to heimdall-docs), and what reaches it from here and when (the copies, once the sync PR is in).
  - `api-client/README.md`: three links.
  - Comments that name the old site (wording only, no behaviour change):
    - `scripts/openapi.py` (docstring and the line-113 message)
    - `scripts/coverage.py` (docstring)
    - `ci/check.sh`, `ci/test.sh`
    - `src/Presentation/ArturRios.Heimdall.WebApi/Documentation/SwaggerConfiguration.cs`
    - `tools/ArturRios.Heimdall.OpenApiGen/Program.cs`
    - `.dockerignore`, `.gitignore`
  - `yggdrasil.yml`: the header comment no longer describes a second catalog entry. `deploy.inputs: [test]` is
    removed, and the `test` archive comment says the report goes to heimdall-docs.
  - `docs/requirements/Data Processing Agreement.md:48` and `docs/requirements/Operations & Infrastructure Document.md:223-224`:
    the old-site links point at their new pages.
  - `CHANGELOG.md`: a `### Changed` or `### Removed` entry under `## [Unreleased]`.

- [ ] **Step 1: Branch** with `git switch -c feature/docs-moved-to-heimdall-docs origin/develop`.
- [ ] **Step 2: Delete the site files.** Run `git grep -n -E "docs/(hugo\.toml|content|layouts|assets|archetypes|i18n|package|prompts)|docs\.Dockerfile"`
  outside `docs/superpowers` and `CHANGELOG.md`. Expected: only the files this task edits next.
- [ ] **Step 3: Edit the files listed above.** Look up every new link and anchor in a heimdall-docs scratch build:
  `git -C ../heimdall-docs worktree add <scratch>/hd feature/centralized-docs`, then `hugo --minify --destination <scratch>/public`
  from it, then look up each target in `<scratch>/public`. Remove the worktree afterwards.
- [ ] **Step 4: Verify.**
  - `git grep -n "docs.artur-rios.tech/heimdall-api" -- ':!CHANGELOG.md' ':!docs/superpowers'`. Expected: no output.
  - `dotnet build src/ArturRios.Heimdall.sln`. Expected: succeeds.
  - `python -m unittest discover -s scripts -p "test_*.py"`. Expected: passes, apart from the existing Windows-only
    failures, compared with `origin/develop`.
  - `python scripts/openapi.py --check`. Expected: up to date.
- [ ] **Step 5: Commit** with `git commit -m "docs: move the documentation site to heimdall-docs"`, including this plan.

### Task 2: heimdall-ui cleanup PR

**Branch:** `feature/docs-on-heimdall-docs`

**Files:**
- **Modify `README.md`.** Add one line near the top linking the docs site's Application section
  (`https://docs.artur-rios.tech/heimdall-docs/docs/application/`), and one sentence in "Documentation" saying the
  requirements are also published there. Nothing else changes.
- **Modify `docs/requirements/*.md` (7 files).** Add `slug:` front matter equal to the address heimdall-docs gives the
  page today (Hugo's urlize of the file name), so nothing moves. For example, `vision-document`, and
  `operations--infrastructure-document` with its double hyphen. Read each page's address from a heimdall-docs scratch
  build, as in Task 1.
- **Modify `lib/features/privacy/domain/privacy_notice.dart:17`.** The comment names "the API's docs site"; it now
  reads "heimdall-docs". This is a comment only.
- **Modify `CHANGELOG.md`.** Add an entry under `## [Unreleased]`.

- [ ] **Step 1: Branch** with `git switch -c feature/docs-on-heimdall-docs origin/develop`.
- [ ] **Step 2: Make the edits.** Check that each slug equals the page's current address in the scratch build.
- [ ] **Step 3: Verify.** Run `flutter analyze` and `flutter test test/features/privacy`. Expected: both pass. If
  Flutter is not installed, say so; the change is a comment and front matter.
- [ ] **Step 4: Commit** with `git commit -m "docs: point at the heimdall docs site"`.

### Task 3: the two sync PRs

**Branches:** heimdall-api `feature/sync-docs-to-heimdall-docs` and heimdall-ui `feature/sync-requirements-to-heimdall-docs`,
each from `origin/develop`. They are independent of Tasks 1 and 2.

**Files:**
- **heimdall-api `yggdrasil.yml`.** Add the spec's section:

  ```yaml
  sync:
    repository: heimdall-docs
    inputs: [test]
    paths:
      - { from: docs/coverage-report, to: static/api/coverage-report }
      - { from: docs/openapi/heimdall.json, to: static/api/openapi/heimdall.json }
      - { from: docs/requirements, to: content/en/docs/api/requirements/documents }
  ```

  Comment it in the file's voice. Add a `CHANGELOG.md` entry.
- **heimdall-ui `yggdrasil.yml`.** Add:

  ```yaml
  sync:
    repository: heimdall-docs
    paths:
      - { from: docs/requirements, to: content/en/docs/application/requirements/documents }
  ```

  Add a `CHANGELOG.md` entry.

- [ ] **Step 1: Validate each file.** Use a scratch worktree of yggdrasil at `feature/repository-sync`:
  `cd <worktree> && python scripts/pipeline_config.py describe heimdall-api <heimdall-api checkout>`, and the same for
  `heimdall-ui`. Expected: `"errors": []` and `"syncs": true`. If the catalog there still has `heimdall-api-docs`, it
  does not matter: `heimdall-api` is the deciding entry.
- [ ] **Step 2: Commit** in each repository with `git commit -m "feat: sync the docs to heimdall-docs"` (heimdall-ui:
  `"feat: sync the requirements to heimdall-docs"`).

### Task 4: pull requests

- [ ] Push the four branches and open four PRs into `develop` with `tea pulls create --repo artur-rios/<repo> --base develop --head <branch>`.
  - **Every body** says what the PR changes and how it was verified, and ends with the Claude Code attribution line.
  - **heimdall-api cleanup PR:** "do not release heimdall-api before yggdrasil#135's operator steps are done".
  - **Both sync PRs:** "merge only after yggdrasil#134 is released and Jenkins runs it; `yggdrasil.yml` needs the
    owner's Merge anyway".
  - **Both cleanup PRs:** the follow-up that the Development Workflow Document and the README tracker still name the
    GitHub project board.
