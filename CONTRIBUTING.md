# Contributing

## Prerequisites

- **.NET 10 SDK**
- **PostgreSQL** (the API's relational database; functional tests spin up their own via Testcontainers)
- **Docker** (functional tests start their PostgreSQL container through it)
- **Python 3** (the migration menu, the OpenAPI generator, the flake hunter and the other helpers under `scripts/`)
- The pinned EF Core CLI tool — restore it once after cloning:

  ```bash
  dotnet tool restore
  ```

To run the API locally, configure it first as described under
[Configure](./README.md#configure) in the README.

## Project structure

```
api-client/                                     Ready-to-send requests for every endpoint
  http/                                         JetBrains HTTP Client (.http) files
  bruno/                                        Bruno collection
docs/                                           Hugo (Docsy) documentation site — see Documentation site
  requirements/                                 Vision, requirements, use cases, tech stack, data protection, testing
  content/en/                                   The site's own pages (overview, architecture, flows…)
  openapi/                                      Generated OpenAPI document, published by the site
  themes/docsy/                                 The site theme, as a git submodule
scripts/                                        Tooling (EF Core migrations, coverage, OpenAPI)
tools/ArturRios.Heimdall.OpenApiGen/            Writes docs/openapi/heimdall.json (not in the solution)
src/
  Domain/ArturRios.Heimdall.Domain/      Domain entities & enums
  Application/
    ArturRios.Heimdall.Command/          Command (write) handlers — CQRS
    ArturRios.Heimdall.Query/            Query (read) handlers — CQRS
    ArturRios.Heimdall.Shared/           Shared messages/contracts
  Infrastructure/ArturRios.Heimdall.Data/ EF Core DbContext, entity maps, migrations, seeding
  Presentation/ArturRios.Heimdall.WebApi/ ASP.NET Core Web API (entry point)
  ArturRios.Heimdall.sln
tests/                                          Test projects mirroring src/ (unit + functional)
README.md
LICENSE
```

## Build

```bash
dotnet build src/ArturRios.Heimdall.sln
```

## Test

Run the whole suite:

```bash
dotnet test src/ArturRios.Heimdall.sln
```

Run one kind at a time — unit tests are isolated; functional tests run end-to-end against a real
PostgreSQL database provisioned by Testcontainers:

```bash
dotnet test src/ArturRios.Heimdall.sln --filter "Category=Unit"
```

```bash
dotnet test src/ArturRios.Heimdall.sln --filter "Category=Functional"
```

CI runs the two filters as separate steps and fails first if any test carries neither category, since neither
step would run it: use the `[UnitFact]` / `[FunctionalFact]` family of attributes on every test.

Every run writes a `.trx` under each project's `TestResults/` (git-ignored), so a failure is always
recorded by name rather than only as a console count. To chase a test that fails only occasionally,
repeat the run and let the harness collect the names:

```bash
python scripts/flake_hunt.py --runs 25
```

See the [Testing Specification Document](./docs/requirements/Testing%20Specification%20Document.md) for
the full testing standard.

### Coverage

Run the tests, collect coverage and build the HTML report in one step:

```bash
python scripts/coverage.py
```

The report is written to `docs/coverage-report` (git-ignored), and the run fails when merged line coverage
drops below the floor the
[Testing Specification Document](./docs/requirements/Testing%20Specification%20Document.md#21-the-coverage-floor)
sets; the report is written either way, since it is how you find what lost coverage. `--report-only` builds the
HTML from coverage files already on disk without running any tests, which is what CI does after its own two test
steps. `--no-threshold` generates without enforcing the floor.

Add `--clean` to delete every `bin/` and `obj/` first. `dotnet build` only overwrites the files it produces, so
assemblies from a previous project name survive in the output directories, and coverlet instruments whatever it
finds there — which is how the report once ended up a third full of `ArturRios.IdentityManager.*` classes that no
longer existed. `dotnet clean` does not remove them; `--clean` does. The script also empties
`docs/coverage-report` before writing, since ReportGenerator does not remove pages whose class has disappeared.

```bash
python scripts/coverage.py --clean
```

If `REPORTGENERATOR_LICENSE` is set, the script passes it to ReportGenerator and the PRO version generates the
report; the key is never echoed. CI reads it from the repository secret of the same name. Unset — as on a pull
request from a fork, where GitHub withholds secrets — generation still succeeds with the free version.

The published report is not committed. The Tests workflow uploads it as an artifact, and the Build Docs workflow
unpacks the one from the latest successful run on `main` into `docs/coverage-report` before Hugo builds, so a
change under `src/` moves the published numbers once it reaches `main`. The report itself is the only statement of
the current numbers; figures quoted in prose go stale.

## Migrations

The schema is managed with **EF Core migrations, applied explicitly** — the API never migrates on
startup, and refuses to start when migrations are pending. Use the interactive migration menu to
**list, create (generate), or apply** migrations:

```bash
python scripts/migrations.py
```

It asks which environment file to load (for the connection string), then offers the migration
actions. Creating a migration prompts for its name and adds it to
`src/Infrastructure/ArturRios.Heimdall.Data/Migrations`. Requires `dotnet tool restore` (above)
to have been run once.

## OpenAPI document

The API explorer renders `docs/openapi/heimdall.json`, which is generated and committed. Regenerate
it after changing anything it describes:

```bash
python scripts/openapi.py
```

[`check-openapi.yml`](./.github/workflows/check-openapi.yml) fails the build if it is out of date —
otherwise a controller change would keep being published with the old document, since the docs site
publishes the committed file and nothing else regenerates it.

## Documentation site

The site is built with [Hugo](https://gohugo.io/) and the [Docsy](https://www.docsy.dev/) theme from
`docs/`. To preview it locally — Hugo Extended 0.160.1+, Node.js 24+, and
[Dart Sass](https://sass-lang.com/install/) 1.95.0+ (the `sass` CLI on `PATH`, which Docsy 0.17+ compiles its
stylesheets with) required:

```bash
git submodule update --init --recursive
```

```bash
npm run install:theme-deps --prefix docs/themes/docsy
```

```bash
hugo -s docs server
```

The site's Changelog and Contributing pages render this file and [CHANGELOG.md](./CHANGELOG.md) from the repository
root (mounted in `docs/hugo.toml`), so they are edited here, never copied into `docs/content/`. Build, test and
release material belongs in this file; the site keeps the usage, reference and architecture pages.

## Branching model

```
feature/<name> ─┐
fix/<name> ─────┴─▶ develop ──▶ release/x.y.z ──▶ main  (tag vx.y.z)
```

| Branch | Cut from | Merges into | How |
|---|---|---|---|
| `feature/<name>`, `fix/<name>` | `develop` | `develop` | Pull request, squash or merge. The branch is deleted on merge. |
| `release/x.y.z` | `develop` | `main` | Pull request. **Never merged by hand** — see below. |
| `develop`, `main` | — | — | Protected: no direct pushes, no force pushes, no deletion. |

Names are lowercase: letters, digits, `.`, `_` and `-`. A `release/` branch is a snapshot of
`develop` and carries no commits of its own: a fix for a release lands on `develop` through a
`fix/` branch and a new release branch is cut.

The **Branch Policy** workflow checks all of this on every pull request and is a required check
on `develop` and `main`.

Each use case ships on its own `feature/` branch, issue and pull request into `develop` — see the
[Development Workflow Document](./docs/requirements/Development%20Workflow%20Document.md) for the
issue status and testing gate it follows.

## Commits and the changelog

Commit messages follow [Conventional Commits](https://www.conventionalcommits.org/) with a lowercase subject, e.g.
`feat: let a data subject export their own data` or `fix: give a login attempt one deadline instead of two`.

Record every change an API client or operator would notice under `## [Unreleased]` in
[CHANGELOG.md](./CHANGELOG.md), in the same pull request that makes it.

## Versioning

The API follows [Semantic Versioning](https://semver.org/spec/v2.0.0.html). Its public surface is everything an
API client or an operator depends on: the HTTP contract (routes, request and response bodies, status codes and
messages, the claims in the tokens it issues, the roles that may call each endpoint), the configuration a
deployment supplies, and the database the deployment keeps between releases.

- **Major** — a release an existing client or deployment cannot take without changing something. An endpoint,
  field or claim is removed or renamed, a response changes shape or status, a request that used to be accepted is
  refused; an environment variable is renamed, removed or becomes required, or a default changes in a way a
  running deployment would notice; a migration needs manual work before or after it is applied, or discards data.
  Its entry in [CHANGELOG.md](./CHANGELOG.md) carries an `### Upgrading from <X>.x to <Y>.0` section saying what to
  change.
- **Minor** — backwards-compatible additions: a new endpoint, an optional request field, a new response field
  (clients must ignore fields they do not know), a new optional setting whose default keeps the previous
  behaviour, a migration that only adds to the schema.
- **Patch** — fixes that change no contract: a defect, a performance or security fix, a dependency update,
  documentation.

The version is not stored in the source. It is the name of the release branch: `release/1.4.0` is deployed as the
image tag `1.4.0-<short commit>`, which is what the yggdrasil console shows, and when it reaches production Jenkins
tags the merge `v1.4.0` and creates the GitHub release of that name. The Branch Policy workflow refuses a release
branch whose version already has a tag. The `v1` in the OpenAPI document and its URL (`/swagger/v1/swagger.json`)
is the document's name, not the release version, and does not change with it.

## Releasing

Four environments, described in
[Environments and deployment](https://artur-rios.github.io/heimdall-api/docs/environments-and-deployment/):
`local` is the developer's own Docker Desktop, deployed by hand; `development`, `homologation` and
`production` share one VPS and are deployed by Jenkins.

| Event | Deploys to |
|---|---|
| Push to `develop` (every merged pull request) | **development** — left stopped if it was stopped |
| Push of a `release/x.y.z` branch | **homologation** — likewise |
| Pull request `release/x.y.z → main` with every check green | **production**, then merge and tag |

Development and homologation are **on demand**: they run only while somebody uses them, and a deploy
does not start a stopped one. To try a merge or a release candidate, turn the environment on, on
the VPS — `scripts/ygg.sh env start development` (or `homologation`) — and off again with
`scripts/ygg.sh env stop <environment>` when done. It is then at `https://heimdall-api-dev.example.com`
(`-hml` for homologation), `example.com` standing for the real domain.

Before cutting the release, finalize [CHANGELOG.md](./CHANGELOG.md) on `develop` through a normal `feature/` or
`fix/` pull request, since the release branch can carry no commits of its own: rename `## [Unreleased]` to
`## [1.4.0] - <yyyy-mm-dd>` above a fresh, empty `## [Unreleased]`, and update the compare links at the bottom.

1. `git switch develop && git pull && git switch -c release/1.4.0 && git push -u origin release/1.4.0`
   — Jenkins deploys the branch to **homologation**. Start homologation to check the release there
   before step 2.
2. Open a pull request `release/1.4.0 → main`.
3. When every GitHub check on the pull request passes, Jenkins deploys to **production**. On
   success it sets the `deploy/production` status, merges the pull request with a merge commit,
   creates the tag and GitHub release `v1.4.0`, and deletes the release branch.
4. If the production deploy fails, Jenkins rolls back to the previous image and the pull request
   stays open. Fix on `develop`, then cut a new release.

The GitHub release's notes are generated from the pull requests merged since the previous tag;
CHANGELOG.md is the curated record.

Follow a release in the **yggdrasil console** (`https://yggdrasil.<domain>`, or the Android app).
The system card shows each environment — development, homologation, production — with this
application's version, commit, deploy time and health in it; a stopped on-demand environment shows
as *Stopped*, not as a problem.

The repository owner can bypass these rules. That is for emergencies, not for routine work.

## Where this is deployed from

Deployment is managed by [yggdrasil](https://github.com/artur-rios/yggdrasil). This repository is
the application `heimdall-api` in its `catalog.yaml`, which is what gives it:
- its Jenkins deploy job, and its place in each of the catalog's environments (`local`,
  `development`, `homologation`, `production`)
- its GitHub rulesets and required checks (the catalog's `checks`)
- its Prometheus scraping
- its place in the console

If a required check is renamed or added here, update the catalog entry, then run
`python github/rulesets.py heimdall-api` in yggdrasil.

The VPS environments' env files live on the VPS at `/etc/yggdrasil/<environment>/heimdall-api.env`,
filled in from `docker/<environment>.env.example`. A variable added to the API needs adding to the
templates here and, where its value is not the default, to those files.
